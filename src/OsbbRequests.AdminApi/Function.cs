using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace OsbbRequests.AdminApi;

public sealed class Function
{
    public APIGatewayHttpApiV2ProxyResponse FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var isHealth = request.RequestContext?.Http?.Method == "GET" && request.RawPath == "/health";
        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = isHealth ? 200 : 404,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            Body = isHealth ? "{\"status\":\"ok\"}" : "{\"error\":\"not_found\"}"
        };
    }
}
