using System.Text.Json;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.DataImport.Storage;

public sealed class RequestEventSnapshot
{
    public string? AssignedTo { get; set; }
    public string? Priority { get; set; }
    public DateOnly? PlannedDate { get; set; }
    public int Visibility { get; set; }
    public string? Scope { get; set; }
    public string? BuildingNumber { get; set; }
    public int? Floor { get; set; }
    public int? Entrance { get; set; }
    public bool HasEntrance { get; set; }
    public bool? KeySerialNumbersChanged { get; set; }

    public static RequestEventSnapshot Capture(MaintenanceRequest previous, MaintenanceRequest next) => new()
    {
        AssignedTo = next.AssignedTo,
        Priority = next.Priority,
        PlannedDate = next.PlannedDate,
        Visibility = (int)next.Visibility,
        Scope = next.Scope?.ToString(),
        BuildingNumber = next.BuildingNumber,
        Floor = next.Floor,
        Entrance = next.Entrance,
        HasEntrance = true,
        KeySerialNumbersChanged = !Enumerable.SequenceEqual(
            previous.KeyItems?.Select(key => key.SerialNumber) ?? [],
            next.KeyItems?.Select(key => key.SerialNumber) ?? [])
    };

    public string SerializeForHistory()
    {
        var fields = new Dictionary<string, object?>
        {
            [nameof(AssignedTo)] = AssignedTo,
            [nameof(Priority)] = Priority,
            [nameof(PlannedDate)] = PlannedDate,
            [nameof(Visibility)] = Visibility,
            [nameof(Scope)] = Scope,
            [nameof(BuildingNumber)] = BuildingNumber,
            [nameof(Floor)] = Floor
        };
        if (HasEntrance) fields[nameof(Entrance)] = Entrance;
        if (KeySerialNumbersChanged is { } changed) fields[nameof(KeySerialNumbersChanged)] = changed;
        return JsonSerializer.Serialize(fields);
    }
}
