using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Shared.Residents;

public static class DynamoOwnerApartments
{
    public static OwnerApartments? Read(Dictionary<string, AttributeValue> row, string complexId, long ownerId)
    {
        if (row.GetValueOrDefault("payload")?.S is not { } payload) return null;
        var value = JsonSerializer.Deserialize<OwnerApartments>(payload);
        return value is not null && value.ComplexId == complexId && value.OwnerId == ownerId
            ? value : throw new InvalidOperationException("Invalid owner apartment record.");
    }

    public static TransactWriteItem Save(this OwnerApartments apartments, string table, int? previousVersion)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new(OwnerApartments.Key(apartments.ComplexId, apartments.OwnerId)),
            ["version"] = new() { N = apartments.Version.ToString(CultureInfo.InvariantCulture) },
            ["payload"] = new(JsonSerializer.Serialize(apartments))
        };
        if (apartments.Items.Any(apartment => apartment.Status == ApartmentReviewStatus.Pending))
        {
            item[ResidentRecordKey.IndexGroupAttribute] = new(OwnerApartments.ReviewGroup(apartments.ComplexId));
            item["recordOrder"] = new(apartments.OwnerId.ToString("D20", CultureInfo.InvariantCulture));
        }
        var put = new Put
        {
            TableName = table,
            Item = item,
            ConditionExpression = "attribute_not_exists(pk)"
        };
        if (previousVersion is { } expected)
        {
            put.ConditionExpression = "#version = :version";
            put.ExpressionAttributeNames = new() { ["#version"] = "version" };
            put.ExpressionAttributeValues = new() { [":version"] = new() { N = expected.ToString(CultureInfo.InvariantCulture) } };
        }
        return new() { Put = put };
    }

    public static ConditionCheck CheckProfile(string table, string complexId, long ownerId, int version) => new()
    {
        TableName = table,
        Key = new() { ["pk"] = new(ResidentRecordKey.Create(complexId, FormattableString.Invariant($"USER#{ownerId}"), "REGISTRATION").Key) },
        ConditionExpression = "complexId = :complex AND #version = :version",
        ExpressionAttributeNames = new() { ["#version"] = "version" },
        ExpressionAttributeValues = new()
        {
            [":complex"] = new(complexId),
            [":version"] = new() { N = version.ToString(CultureInfo.InvariantCulture) }
        }
    };
}
