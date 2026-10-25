using System.Globalization;

namespace OsbbRequests.Shared.Models;

public static class RequestPriorityOrder
{
    public static int Rank(string? priority) => priority switch { "Urgent" => 3, "High" => 2, "Low" => 0, _ => 1 };
    public static string Key(MaintenanceRequest request) => Rank(request.Priority).ToString(CultureInfo.InvariantCulture) + "#" +
        request.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "#" + request.Id.ToString("D20", CultureInfo.InvariantCulture);
}
