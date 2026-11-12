using System.Globalization;
using System.Text;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Shared.Residents;

public sealed record ResidentProjection(string Key, string ComplexId, int Version, string Payload, string? Directory,
    string? DateOrder, string? NameOrder, string? BuildingNumber, string? ResidentType)
{
    private const string Alphabet = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя";
    private const string UpperAlphabet = "АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ";
    public static ResidentProjection Create(ResidentRegistration resident)
    {
        var key = "COMPLEX#" + resident.BuildingId + "#USER#" + resident.UserId.ToString(CultureInfo.InvariantCulture);
        var indexed = resident.Stage is RegistrationStage.Pending or RegistrationStage.Approved && !resident.PersonalDataDeleted;
        if (!indexed) return new(key, resident.BuildingId, resident.Version, System.Text.Json.JsonSerializer.Serialize(resident), null, null, null, null, null);
        var time = resident.Stage == RegistrationStage.Approved ? resident.ApprovedAt ?? resident.UpdatedAt : resident.UpdatedAt;
        var date = time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "#" + resident.UserId.ToString("D20", CultureInfo.InvariantCulture);
        var name = (string.IsNullOrWhiteSpace(resident.LastName) ? "1#" : "0#" + SortText(resident.LastName)) + "#" + SortText(resident.FirstName) + "#" + resident.UserId.ToString("D20", CultureInfo.InvariantCulture);
        return new(key, resident.BuildingId, resident.Version, System.Text.Json.JsonSerializer.Serialize(resident),
            "COMPLEX#" + resident.BuildingId + "#" + resident.Stage, date, name, resident.BuildingNumber ?? "", resident.ResidentType.ToString());
    }

    private static string SortText(string? input)
    {
        var result = new StringBuilder();
        foreach (var rune in (input ?? "").Trim().Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var upperIndex = UpperAlphabet.IndexOf(rune.ToString(), StringComparison.Ordinal);
            var index = upperIndex >= 0 ? upperIndex : Alphabet.IndexOf(rune.ToString(), StringComparison.Ordinal);
            var character = rune.Value is >= 'A' and <= 'Z' ? rune.Value + ('a' - 'A') : rune.Value;
            var weight = index >= 0 ? 0x200000 + index : character;
            result.Append(weight.ToString("X6", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
}
