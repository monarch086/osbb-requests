using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Persistence;

public sealed partial class DynamoCommandRepository
{
    public async Task<ResidentProfileDialog?> GetProfileDialogAsync(long userId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("USER#" + Number(userId), "PROFILE_DIALOG", cancellationToken);
        return item.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<ResidentProfileDialog>(payload.S) : null;
    }
    private void AddProfileWrites(List<TransactWriteItem> writes, CommandEnvelope command, CommandOutcome outcome)
    {
        if (outcome.ProfileDialog is { } dialog)
        {
            if (dialog.Value.UserId != command.UserId) throw new InvalidOperationException("Profile dialog owner mismatch.");
            var item = Key("USER#" + Number(command.UserId), "PROFILE_DIALOG");
            item["payload"] = new(JsonSerializer.Serialize(dialog.Value));
            item["version"] = new() { N = Number(dialog.Value.Version) };
            writes.Add(VersionedProfilePut(item, dialog.ExpectedVersion));
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
        else writes.Add(new()
        {
            ConditionCheck = new()
            {
                TableName = tableName,
                Key = memberKey,
                ConditionExpression = "buildingId = :complex AND #active = :yes",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values
            }
        });
        writes.Add(VersionedProfilePut(RegistrationItem("USER#" + Number(command.UserId), "REGISTRATION", profile), update.ExpectedVersion));
        writes.Add(VersionedProfilePut(RegistrationItem("REGISTRATION#" + profile.Id, "META", profile), update.ExpectedVersion, allowMissing: true));
        writes.Add(VersionedProfilePut(RegistrationItem("BUILDING#" + profile.BuildingId,
            "RESIDENT#" + command.UserId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), profile), update.ExpectedVersion, allowMissing: true));
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
