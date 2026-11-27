using System.Text.Json;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Domain.Admin;

public static class RequestAudit
{
    public static string SerializeChanges(MaintenanceRequest previous, MaintenanceRequest next) =>
        JsonSerializer.Serialize(new
        {
            next.AssignedTo,
            next.Priority,
            next.PlannedDate,
            next.Visibility,
            next.Scope,
            next.BuildingNumber,
            next.Floor,
            next.Entrance,
            KeySerialNumbersChanged = KeySerialNumbersChanged(previous, next)
        });

    public static bool KeySerialNumbersChanged(MaintenanceRequest previous, MaintenanceRequest next) =>
        !Enumerable.SequenceEqual(previous.KeyItems?.Select(key => key.SerialNumber) ?? [],
            next.KeyItems?.Select(key => key.SerialNumber) ?? []);
}
