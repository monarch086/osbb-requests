using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Bot.Persistence;

public sealed partial class DynamoCommandRepository
{
    private async Task<Dictionary<string, AttributeValue>> GetResidentRecordAsync(string complex, string pk, string sk, CancellationToken ct)
    {
        if (complexId is not null && complex != complexId) throw new InvalidOperationException("Resident complex mismatch.");
        var key = ResidentRecordKey.Create(complex, pk, sk);
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = residentsTable,
            Key = new() { ["pk"] = new(key.Key) },
            ConsistentRead = true
        }, ct);
        return response.Item?.GetValueOrDefault("recordData")?.M ?? [];
    }
    private ConditionCheck MembershipCheck(long userId, string complex)
    {
        if (complexId is not null && complex != complexId) throw new InvalidOperationException("Resident complex mismatch.");
        var pk = "USER#" + Number(userId);
        var migrated = residentsTable is not null && complexId is not null;
        return new ConditionCheck
        {
            TableName = migrated ? residentsTable : tableName,
            Key = migrated ? new() { ["pk"] = new(ResidentRecordKey.Create(complex, pk, "MEMBERSHIP").Key) } : Key(pk, "MEMBERSHIP"),
            ConditionExpression = migrated ? "recordData.buildingId = :complex AND recordData.#active = :active" : "buildingId = :complex AND #active = :active",
            ExpressionAttributeNames = new() { ["#active"] = "active" },
            ExpressionAttributeValues = new() { [":complex"] = new(complex), [":active"] = new() { BOOL = true } }
        };
    }
    private TransactWriteItem ResidentMirrorWrite(ResidentRegistration resident, int? expectedVersion)
    {
        var value = ResidentProjection.Create(resident);
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new(value.Key),
            ["complexId"] = new(value.ComplexId),
            ["payload"] = new(value.Payload),
            ["version"] = new() { N = value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        if (value.Directory is not null)
        {
            item["directory"] = new(value.Directory);
            item["dateOrder"] = new(value.DateOrder);
            item["nameOrder"] = new(value.NameOrder);
            item["buildingNumber"] = new(value.BuildingNumber);
            item["residentType"] = new(value.ResidentType);
        }
        var put = new Put { TableName = residentsTable!, Item = item };
        if (expectedVersion is { } version)
        {
            put.ConditionExpression = "#version = :version AND complexId = :complex";
            put.ExpressionAttributeNames = new() { ["#version"] = "version" };
            put.ExpressionAttributeValues = new() { [":version"] = new() { N = version.ToString(System.Globalization.CultureInfo.InvariantCulture) }, [":complex"] = new(value.ComplexId) };
        }
        else put.ConditionExpression = "attribute_not_exists(pk)";
        return new TransactWriteItem { Put = put };
    }
    private TransactWriteItem RelatedResidentPut(Dictionary<string, AttributeValue> source, string complexId, int? expectedVersion = null, bool immutable = false)
    {
        var sourcePk = source["pk"].S;
        var sourceSk = source["sk"].S;
        var key = ResidentRecordKey.Create(complexId, sourcePk, sourceSk);
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new(key.Key),
            ["recordGroup"] = new(key.Group),
            ["recordOrder"] = new(key.Order),
            ["sourcePk"] = new(sourcePk),
            ["sourceSk"] = new(sourceSk),
            ["complexId"] = new(complexId),
            ["recordData"] = new() { M = new Dictionary<string, AttributeValue>(source) }
        };
        if (source.TryGetValue("version", out var itemVersion)) item["version"] = itemVersion;
        var put = new Put { TableName = residentsTable!, Item = item };
        if (immutable) put.ConditionExpression = "attribute_not_exists(pk)";
        else if (expectedVersion is { } version)
        {
            put.ConditionExpression = "#v = :expected AND complexId = :complex";
            put.ExpressionAttributeNames = new() { ["#v"] = "version" };
            put.ExpressionAttributeValues = new() { [":expected"] = new() { N = version.ToString(System.Globalization.CultureInfo.InvariantCulture) }, [":complex"] = new(complexId) };
        }
        else
        {
            put.ConditionExpression = "attribute_not_exists(pk) OR complexId = :complex";
            put.ExpressionAttributeValues = new() { [":complex"] = new(complexId) };
        }
        return new TransactWriteItem { Put = put };
    }

}
