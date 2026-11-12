using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Persistence;

public sealed partial class DynamoCommandRepository
{
    public async Task<ResidentProfileDialog?> GetProfileDialogAsync(long userId, CancellationToken cancellationToken)
    {
        var item = residentsTable is not null && complexId is not null
            ? await GetResidentRecordAsync(complexId, "USER#" + Number(userId), "PROFILE_DIALOG", cancellationToken)
            : await GetAsync("USER#" + Number(userId), "PROFILE_DIALOG", cancellationToken);
        var dialog = item.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<ResidentProfileDialog>(payload.S) : null;
        return dialog is not null && (complexId is null || dialog.ComplexId == complexId && dialog.UserId == userId) ? dialog : null;
    }
    private async Task AddProfileWritesAsync(List<TransactWriteItem> writes, CommandEnvelope command, CommandOutcome outcome, CancellationToken ct)
    {
        if (outcome.ProfileDialog is { } dialog)
        {
            if (dialog.Value.UserId != command.UserId) throw new InvalidOperationException("Profile dialog owner mismatch.");
            var item = Key("USER#" + Number(command.UserId), "PROFILE_DIALOG");
            item["payload"] = new(JsonSerializer.Serialize(dialog.Value));
            item["version"] = new() { N = Number(dialog.Value.Version) };
            writes.Add(VersionedProfilePut(item, dialog.ExpectedVersion));
            if (residentsTable is not null) writes.Add(RelatedResidentPut(item, dialog.Value.ComplexId, dialog.ExpectedVersion));
        }
        if (outcome.Profile is not { } update) return;
        var profile = update.Value;
        if (profile.UserId != command.UserId || outcome.ProfileDialog?.Value is not { Active: false } state || state.ComplexId != profile.BuildingId)
            throw new InvalidOperationException("Profile update identity mismatch.");
        var memberKey = Key("USER#" + Number(command.UserId), "MEMBERSHIP");
        var values = new Dictionary<string, AttributeValue> { [":complex"] = new(profile.BuildingId), [":yes"] = new() { BOOL = true } };
        var names = new Dictionary<string, string> { ["#active"] = "active" };
        if (profile.PersonalDataDeleted)
        {
            if (profile.Stage != OsbbRequests.Shared.Models.RegistrationStage.Deleted)
                throw new InvalidOperationException("Deleted profile must revoke resident access.");
            if (residentsTable is not null)
            {
                var membership = complexId is not null
                    ? await GetResidentRecordAsync(profile.BuildingId, "USER#" + Number(command.UserId), "MEMBERSHIP", ct)
                    : await GetAsync("USER#" + Number(command.UserId), "MEMBERSHIP", ct);
                if (membership.GetValueOrDefault("buildingId")?.S != profile.BuildingId || membership.GetValueOrDefault("active")?.BOOL != true)
                    throw new InvalidOperationException("Resident membership is unavailable.");
                membership["active"] = new() { BOOL = false };
                var mirror = RelatedResidentPut(membership, profile.BuildingId);
                if (complexId is not null)
                {
                    mirror.Put.ConditionExpression = "complexId = :complex AND recordData.buildingId = :complex AND recordData.#active = :yes";
                    mirror.Put.ExpressionAttributeNames = new() { ["#active"] = "active" };
                    mirror.Put.ExpressionAttributeValues = new() { [":complex"] = new(profile.BuildingId), [":yes"] = new() { BOOL = true } };
                }
                writes.Add(mirror);
            }
            values[":no"] = new() { BOOL = false };
            writes.Add(new()
            {
                Update = new()
                {
                    TableName = tableName,
                    Key = memberKey,
                    UpdateExpression = "SET #active = :no",
                    ConditionExpression = "buildingId = :complex AND #active = :yes",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values
                }
            });
        }
        else writes.Add(new() { ConditionCheck = MembershipCheck(command.UserId, profile.BuildingId) });
        if (residentsTable is not null) writes.Add(ResidentMirrorWrite(profile, update.ExpectedVersion));
        var userProfile = RegistrationItem("USER#" + Number(command.UserId), "REGISTRATION", profile);
        writes.Add(VersionedProfilePut(userProfile, update.ExpectedVersion));
        if (residentsTable is not null) writes.Add(RelatedResidentPut(userProfile, profile.BuildingId, update.ExpectedVersion));
        var registrationMeta = RegistrationItem("REGISTRATION#" + profile.Id, "META", profile);
        writes.Add(VersionedProfilePut(registrationMeta, update.ExpectedVersion, allowMissing: true));
        if (residentsTable is not null) writes.Add(RelatedResidentPut(registrationMeta, profile.BuildingId, update.ExpectedVersion));
        var residentEntry = RegistrationItem("BUILDING#" + profile.BuildingId,
            "RESIDENT#" + command.UserId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), profile);
        writes.Add(VersionedProfilePut(residentEntry, update.ExpectedVersion, allowMissing: true));
        if (residentsTable is not null) writes.Add(RelatedResidentPut(residentEntry, profile.BuildingId, update.ExpectedVersion));
    }
    private TransactWriteItem VersionedProfilePut(Dictionary<string, AttributeValue> item, int? expected, bool allowMissing = false)
    {
        var write = Put(item);
        if (expected is { } version)
        {
            write.Put.ConditionExpression = (allowMissing ? "attribute_not_exists(pk) OR " : "") + "#v = :expected";
            write.Put.ExpressionAttributeNames = new() { ["#v"] = "version" };
            write.Put.ExpressionAttributeValues = new() { [":expected"] = new() { N = Number(version) } };
        }
        return write;
    }
}
