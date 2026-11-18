using System.Globalization;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Shared.Residents;

public sealed record ResidentProjection(string Key, string ComplexId, int Version, string Payload, string? Directory,
    string? DateOrder, string? BuildingNumber, string? ResidentType)
{
    public static ResidentProjection Create(ResidentRegistration resident)
    {
        var key = "COMPLEX#" + resident.BuildingId + "#USER#" + resident.UserId.ToString(CultureInfo.InvariantCulture);
        var indexed = resident.Stage is RegistrationStage.Pending or RegistrationStage.Approved && !resident.PersonalDataDeleted;
        if (!indexed) return new(key, resident.BuildingId, resident.Version, System.Text.Json.JsonSerializer.Serialize(resident), null, null, null, null);
        var time = resident.Stage == RegistrationStage.Approved ? resident.ApprovedAt ?? resident.UpdatedAt : resident.UpdatedAt;
        var date = time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "#" + resident.UserId.ToString("D20", CultureInfo.InvariantCulture);
        return new(key, resident.BuildingId, resident.Version, System.Text.Json.JsonSerializer.Serialize(resident),
            "COMPLEX#" + resident.BuildingId + "#" + resident.Stage, date, resident.BuildingNumber ?? "", resident.ResidentType.ToString());
    }
}
