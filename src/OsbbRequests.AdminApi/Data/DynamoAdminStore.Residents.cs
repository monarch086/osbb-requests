using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    private TransactWriteItem PhoneAssociationPut(ResidentRegistration resident)
    {
        var phone = resident.Phone ?? throw new InvalidOperationException("Resident phone is missing.");
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = S(ResidentPhoneIndex.ItemKey(resident.BuildingId, resident.UserId, phone)),
            ["phoneLookup"] = S(ResidentPhoneIndex.LookupKey(resident.BuildingId, phone)),
            ["complexId"] = S(resident.BuildingId),
            ["residentId"] = new AttributeValue { N = resident.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        return new()
        {
            Put = new()
            {
                TableName = residentsTable!,
                Item = item,
                ConditionExpression = "attribute_not_exists(pk) OR (complexId = :complex AND residentId = :resident)",
                ExpressionAttributeValues = new() { [":complex"] = S(resident.BuildingId), [":resident"] = new AttributeValue { N = resident.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
            }
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

    private TransactWriteItem RelatedResidentDelete(Dictionary<string, AttributeValue> sourceKey, string complexId, int expectedVersion)
    {
        var key = ResidentRecordKey.Create(complexId, sourceKey["pk"].S, sourceKey["sk"].S);
        return new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = residentsTable!,
                Key = new() { ["pk"] = new(key.Key) },
                ConditionExpression = "#v = :expected AND complexId = :complex",
                ExpressionAttributeNames = new() { ["#v"] = "version" },
                ExpressionAttributeValues = new() { [":expected"] = new() { N = expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) }, [":complex"] = new(complexId) }
            }
        };
    }

}
