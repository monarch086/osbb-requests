using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using OsbbRequests.AdminApi.Auth;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Telegram;
using OsbbRequests.AdminApi.Statistics;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace OsbbRequests.AdminApi;

public sealed class Function
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ApiHandler handler;
    private readonly IServiceNotifier? notifier;
    private readonly IStatisticsStore? statistics;
    private static string Config(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing configuration: " + name);
    private static bool PrioritySorting() => Environment.GetEnvironmentVariable("REQUEST_SORT_MODE") switch
    {
        null or "Priority" => true,
        "Date" => false,
        _ => throw new InvalidOperationException("REQUEST_SORT_MODE must be Priority or Date.")
    };
    public Function()
    {
        statistics = new StatisticsStore(new AmazonDynamoDBClient(), Config("STATISTICS_TABLE"));
        handler = new ApiHandler(new DynamoAdminStore(new MeteredDynamoDb(), Config("ADMINISTRATORS_TABLE"),
            Config("COMMAND_DATA_TABLE"), Config("BUILDING_ID"), PrioritySorting(), Config("RESIDENTS_TABLE"), Environment.GetEnvironmentVariable("COMPACT_RESIDENT_DIRECTORY") == "true"), new CognitoIdentity(new AmazonCognitoIdentityProviderClient()), statistics);
        notifier = TelegramServiceNotifier.FromEnvironment(Http);
    }
    public Function(ApiHandler handler, IServiceNotifier? notifier = null, IStatisticsStore? statistics = null)
    { this.handler = handler; this.notifier = notifier; this.statistics = statistics; }
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var endpoint = RequestMeasurement.Endpoint(request);
        var measurement = endpoint is null ? null : new RequestMeasurement();
        MeteredDynamoDb.Measurement.Value = measurement;
        try { return await handler.HandleAsync(request, timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return ApiHandler.Reply(504, new { error = "Не вдалося завершити запит вчасно. Спробуйте звузити фільтри." }); }
        catch (ApiError error)
        {
            if (error.Status >= 500) await notifier.ReportSafelyAsync("admin_api", error, context.AwsRequestId, context.Logger.LogLine);
            return ApiHandler.Reply(error.Status, new { error = error.Message });
        }
        catch (Exception error)
        {
            context.Logger.LogLine($"operation=admin_api errorType={error.GetType().Name}");
            await notifier.ReportSafelyAsync("admin_api", error, context.AwsRequestId, context.Logger.LogLine);
            return ApiHandler.Reply(503, new { error = "Сервіс тимчасово недоступний. Спробуйте пізніше." });
        }
        finally
        {
            MeteredDynamoDb.Measurement.Value = null;
            if (measurement is not null && endpoint is not null && statistics is not null)
            {
                var values = measurement.Finish();
                try { using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)); await statistics.RecordAsync(endpoint, values.Read, values.Write, values.Duration, limit.Token); }
                catch (Exception error) { context.Logger.LogLine($"operation=statistics_write errorType={error.GetType().Name}"); }
            }
        }
    }
}
