using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
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
            put.ConditionExpression = "attribute_not_exists(pk) OR (#version = :version AND complexId = :complex)";
            put.ExpressionAttributeNames = new() { ["#version"] = "version" };
            put.ExpressionAttributeValues = new() { [":version"] = new() { N = version.ToString(System.Globalization.CultureInfo.InvariantCulture) }, [":complex"] = new(value.ComplexId) };
        }
        else put.ConditionExpression = "attribute_not_exists(pk)";
        return new TransactWriteItem { Put = put };
    }
}
