using System.Text.Json.Serialization;
using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using OsbbRequests.Aws;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCognitoAuthentication();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:4201", "http://127.0.0.1:4201")
    .WithMethods("GET", "POST", "PATCH").WithHeaders("authorization", "content-type")));

builder.Services.AddSingleton<IAmazonCognitoIdentityProvider>(services =>
    new AmazonCognitoIdentityProviderClient(CognitoRegion(services)));
builder.Services.AddSingleton<IAmazonDynamoDB>(services => new AmazonDynamoDBClient(CognitoRegion(services)));
builder.Services.AddSingleton<IIdentityProvider, CognitoIdentity>();
builder.Services.AddSingleton(services => new DynamoAdminStore(
    services.GetRequiredService<IAmazonDynamoDB>(),
    builder.Configuration["DynamoDb:AdministratorsTable"] ?? "osbb-requests-dev-administrators",
    builder.Configuration["DynamoDb:CommandDataTable"] ?? "osbb-requests-dev-command-data",
    builder.Configuration["DynamoDb:ComplexId"] ?? "osbb-requests-dev",
    prioritizeRequests: builder.Configuration.GetValue("DynamoDb:PrioritizeRequests", true),
    residentsTable: builder.Configuration["DynamoDb:ResidentsTable"] ?? "osbb-requests-dev-residents",
    compactResidentDirectory: builder.Configuration.GetValue("DynamoDb:CompactResidentDirectory", true),
    compactResidentNameDirectory: builder.Configuration.GetValue("DynamoDb:CompactResidentNameDirectory", true)));
builder.Services.AddSingleton<IAdministratorAccounts>(services => services.GetRequiredService<DynamoAdminStore>());
builder.Services.AddSingleton<IAdministratorStore>(services => services.GetRequiredService<DynamoAdminStore>());
builder.Services.AddSingleton<IAdministratorDirectory>(services => services.GetRequiredService<IAdministratorStore>());
builder.Services.AddSingleton<IRequestStore>(services => services.GetRequiredService<DynamoAdminStore>());
builder.Services.AddSingleton<IResidentStore>(services => services.GetRequiredService<DynamoAdminStore>());
builder.Services.AddSingleton<IMessageStore>(services => services.GetRequiredService<DynamoAdminStore>());
builder.Services.AddSingleton<IStatisticsStore>(services => new DynamoStatisticsStore(services.GetRequiredService<IAmazonDynamoDB>(),
    builder.Configuration["DynamoDb:StatisticsTable"] ?? "osbb-requests-dev-statistics"));
builder.Services.AddSingleton<AdminAccountService>();
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddSingleton<RequestService>();
builder.Services.AddSingleton<ResidentService>();
builder.Services.AddSingleton<MessageService>();
builder.Services.AddSingleton<StatisticsService>();

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    AllowStatusCode404Response = true,
    SuppressDiagnosticsCallback = _ => true,
    ExceptionHandler = async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.Headers.CacheControl = "no-store";
        if (error is AdminOperationException operation)
        {
            context.Response.StatusCode = operation.Failure switch
            {
                AdminFailure.InvalidInput => 400,
                AdminFailure.Unauthenticated => 401,
                AdminFailure.Forbidden => 403,
                AdminFailure.NotFound => 404,
                AdminFailure.Conflict => 409,
                AdminFailure.TooLarge => 413,
                AdminFailure.Unavailable => 503,
                _ => 503
            };
            await context.Response.WriteAsJsonAsync(new { error = operation.Message });
            return;
        }
        app.Logger.LogError("Admin API operation failed: {ErrorType}", error?.GetType().Name);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "Сервіс тимчасово недоступний. Спробуйте пізніше." });
    }
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.Run();

static RegionEndpoint CognitoRegion(IServiceProvider services) =>
    RegionEndpoint.GetBySystemName(services.GetRequiredService<IOptions<CognitoOptions>>().Value.Region);

public partial class Program;
