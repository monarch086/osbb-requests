using System.Text.Json.Serialization;
using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OsbbRequests.Data.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OsbbRequests.Cognito;
using OsbbRequests.Postgres;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCognitoAuthentication();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return new BadRequestObjectResult(new { error = "Некоректні дані запиту." });
    };
}).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:4201", "http://127.0.0.1:4201")
    .WithMethods("GET", "POST", "PATCH").WithHeaders("authorization", "content-type")));

builder.Services.AddSingleton<IAmazonCognitoIdentityProvider>(services =>
    new AmazonCognitoIdentityProviderClient(new AnonymousAWSCredentials(), CognitoRegion(services)));
builder.Services.AddSingleton<IIdentityProvider, CognitoIdentity>();
builder.Services.AddDbContextFactory<DataContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Postgres") ?? new NpgsqlConnectionStringBuilder
    {
        Host = "postgres",
        Database = "osbb_web_api",
        Username = "osbb",
        Password = builder.Configuration["Postgres:Password"],
        GssEncryptionMode = GssEncryptionMode.Disable
    }.ConnectionString));
builder.Services.AddSingleton(new PostgresOptions(
    builder.Configuration["Postgres:Region"] ?? "eu-central-1",
    builder.Configuration["Postgres:Stage"] ?? "dev",
    builder.Configuration["Postgres:ComplexId"] ?? "osbb-requests-dev",
    string.IsNullOrWhiteSpace(builder.Configuration["Postgres:AccountId"]) ? null : builder.Configuration["Postgres:AccountId"]));
builder.Services.AddScoped<PostgresAdminStore>();
builder.Services.AddScoped<IAdministratorAccounts>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<IAdministratorStore>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<IAdministratorDirectory>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<IRequestStore>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<IResidentStore>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<IMessageStore>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<IStatisticsReader>(services => services.GetRequiredService<PostgresAdminStore>());
builder.Services.AddScoped<AdminAccountService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<ResidentService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<StatisticsService>();

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    AllowStatusCode404Response = true,
    SuppressDiagnosticsCallback = _ => true,
    ExceptionHandler = async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.Headers.CacheControl = "no-store";
        if (error is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "Тіло запиту завелике." });
            return;
        }
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
