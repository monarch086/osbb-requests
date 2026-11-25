using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsbbRequests.AdminApi;

internal static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}
