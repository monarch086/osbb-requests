using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Shared.Residents;

public enum ApartmentReviewStatus { Pending, Approved, Rejected }

public sealed record OwnerApartment(string Id, string BuildingNumber, int ApartmentNumber, DateTimeOffset SubmittedAt,
    ApartmentReviewStatus Status = ApartmentReviewStatus.Pending, DateTimeOffset? ReviewedAt = null, string? ReviewedBy = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Address => $"Будинок {BuildingNumber}, кв. {ApartmentNumber}";
}

public sealed record OwnerApartments(string ComplexId, long OwnerId, string RegistrationId, int Version, IReadOnlyList<OwnerApartment> Items)
{
    public const int MaxAdditional = 10;
    public static string Key(string complexId, long ownerId) =>
        FormattableString.Invariant($"COMPLEX#{complexId}#OWNER_APARTMENTS#{ownerId}");

    public static string ReviewGroup(string complexId) => "APARTMENT_REVIEWS#" + complexId;

    public static bool CanManage(ResidentRegistration? profile) =>
        profile is { Stage: RegistrationStage.Approved, ResidentType: ResidentType.Owner, PersonalDataDeleted: false };

    public static OwnerApartments? Read(Dictionary<string, AttributeValue> row, string complexId, long ownerId)
    {
        if (row.GetValueOrDefault("payload")?.S is not { } payload) return null;
        var value = JsonSerializer.Deserialize<OwnerApartments>(payload);
        return value is not null && value.ComplexId == complexId && value.OwnerId == ownerId
            ? value : throw new InvalidOperationException("Invalid owner apartment record.");
    }

    public TransactWriteItem Save(string table, int? previousVersion)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new(Key(ComplexId, OwnerId)),
            ["version"] = new() { N = Version.ToString(CultureInfo.InvariantCulture) },
            ["payload"] = new(JsonSerializer.Serialize(this))
        };
        if (Items.Any(apartment => apartment.Status == ApartmentReviewStatus.Pending))
        {
            item[ResidentRecordKey.IndexGroupAttribute] = new(ReviewGroup(ComplexId));
            item["recordOrder"] = new(OwnerId.ToString("D20", CultureInfo.InvariantCulture));
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
