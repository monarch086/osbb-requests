using Amazon.DynamoDBv2.Model;
using System.Text.Json;
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
    public async Task<bool> ResidentPhoneExistsAsync(string complex, string phone, CancellationToken ct)
    {
        if (residentsTable is null || complexId is null || complex != complexId) throw new InvalidOperationException("Resident complex mismatch.");
        if (usePhoneIndex) return await IndexedPhoneExistsAsync(complex, phone, ct);
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var page = await client.QueryAsync(new QueryRequest
            {
                TableName = residentsTable,
                IndexName = "ResidentsByDate",
                KeyConditionExpression = "directory = :directory",
                ExpressionAttributeValues = new() { [":directory"] = new("COMPLEX#" + complex + "#Approved") },
                ExclusiveStartKey = cursor
            }, ct);
            foreach (var item in page.Items ?? [])
            {
                var candidate = JsonSerializer.Deserialize<ResidentRegistration>(item["payload"].S);
                if (candidate is null || candidate.BuildingId != complex || candidate.Stage != RegistrationStage.Approved ||
                    candidate.PersonalDataDeleted || candidate.Phone != phone) continue;
                var record = await GetResidentRecordAsync(complex, "USER#" + Number(candidate.UserId), "REGISTRATION", ct);
                var current = record.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<ResidentRegistration>(payload.S) : null;
                if (current is not null && current.UserId == candidate.UserId && current.BuildingId == complex &&
                    current.Stage == RegistrationStage.Approved && !current.PersonalDataDeleted && current.Phone == phone) return true;
            }
            cursor = page.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });
        return false;
    }
    private async Task<bool> IndexedPhoneExistsAsync(string complex, string phone, CancellationToken ct)
    {
        var normalized = ResidentPhoneIndex.Normalize(phone);
        var lookup = ResidentPhoneIndex.LookupKey(complex, normalized);
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var page = await client.QueryAsync(new QueryRequest
            {
                TableName = residentsTable,
                IndexName = ResidentPhoneIndex.IndexName,
                KeyConditionExpression = "phoneLookup = :phone",
                ExpressionAttributeValues = new() { [":phone"] = new(lookup) },
                ExclusiveStartKey = cursor
            }, ct);
            foreach (var candidate in page.Items ?? [])
            {
                if (!candidate.TryGetValue("pk", out var key) || string.IsNullOrEmpty(key.S)) continue;
                var association = await client.GetItemAsync(new GetItemRequest
                {
                    TableName = residentsTable,
                    Key = new() { ["pk"] = key },
                    ConsistentRead = true
                }, ct);
                var row = association.Item;
                if (row is null || row.GetValueOrDefault("complexId")?.S != complex || row.GetValueOrDefault("phoneLookup")?.S != lookup ||
                    !long.TryParse(row.GetValueOrDefault("residentId")?.N, out var id) || id <= 0 ||
                    key.S != ResidentPhoneIndex.ItemKey(complex, id, normalized)) continue;
                var source = await GetResidentRecordAsync(complex, "USER#" + Number(id), "REGISTRATION", ct);
                var current = source.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<ResidentRegistration>(payload.S) : null;
                if (current is not null && current.UserId == id && current.BuildingId == complex &&
                    current.Stage == RegistrationStage.Approved && !current.PersonalDataDeleted &&
                    ResidentPhoneIndex.TryNormalize(current.Phone, out var currentPhone) && currentPhone == normalized) return true;
            }
            cursor = page.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });
        return false;
    }
    private TransactWriteItem PhoneAssociationPut(ResidentRegistration resident, string phone)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new(ResidentPhoneIndex.ItemKey(resident.BuildingId, resident.UserId, phone)),
            ["phoneLookup"] = new(ResidentPhoneIndex.LookupKey(resident.BuildingId, phone)),
            ["complexId"] = new(resident.BuildingId),
            ["residentId"] = new() { N = Number(resident.UserId) }
        };
        return new()
        {
            Put = new()
            {
                TableName = residentsTable!,
                Item = item,
                ConditionExpression = "attribute_not_exists(pk) OR (complexId = :complex AND residentId = :resident)",
                ExpressionAttributeValues = new() { [":complex"] = new(resident.BuildingId), [":resident"] = new() { N = Number(resident.UserId) } }
            }
        };
    }

    private TransactWriteItem PhoneAssociationDelete(ResidentRegistration resident, string phone) => new()
    {
        Delete = new()
        {
            TableName = residentsTable!,
            Key = new() { ["pk"] = new(ResidentPhoneIndex.ItemKey(resident.BuildingId, resident.UserId, phone)) },
            ConditionExpression = "attribute_not_exists(pk) OR (complexId = :complex AND residentId = :resident)",
            ExpressionAttributeValues = new() { [":complex"] = new(resident.BuildingId), [":resident"] = new() { N = Number(resident.UserId) } }
        }
    };

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
