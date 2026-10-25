using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using OsbbRequests.AdminApi.Auth;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace OsbbRequests.AdminApi;

public sealed class Function
{
    private readonly ApiHandler handler;
    private static string Config(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing configuration: " + name);
    private static bool PrioritySorting() => Environment.GetEnvironmentVariable("REQUEST_SORT_MODE") switch
    {
        null or "Priority" => true,
        "Date" => false,
        _ => throw new InvalidOperationException("REQUEST_SORT_MODE must be Priority or Date.")
    };
    public Function() : this(new ApiHandler(new DynamoAdminStore(new AmazonDynamoDBClient(), Config("ADMINISTRATORS_TABLE"),
        Config("COMMAND_DATA_TABLE"), Config("BUILDING_ID"), PrioritySorting()), new CognitoIdentity(new AmazonCognitoIdentityProviderClient())))
    { }
    public Function(ApiHandler handler) => this.handler = handler;
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { return await handler.HandleAsync(request, timeout.Token); }
        catch (ApiError error) { return ApiHandler.Reply(error.Status, new { error = error.Message }); }
        catch (Exception error)
        {
            context.Logger.LogLine($"operation=admin_api errorType={error.GetType().Name}");
            return ApiHandler.Reply(503, new { error = "Сервіс тимчасово недоступний. Спробуйте пізніше." });
        }
    }
}
