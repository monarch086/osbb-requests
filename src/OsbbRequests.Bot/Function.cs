using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace OsbbRequests.Bot;

public sealed class Function
{
    // No public HTTP route until authentication and durable processing exist.
    public APIGatewayHttpApiV2ProxyResponse FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context) => new()
        {
            StatusCode = 503,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Body = "{\"error\":\"bot_not_implemented\"}"
        };
}
