using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Aws;

public static class DynamoNotificationWrite
{
    public static TransactWriteItem Create(string table, string complex, ResidentNotification notification) => new()
    {
        Put = new()
        {
            TableName = table,
            Item = new()
            {
                ["pk"] = new(ResidentNotification.Partition(complex)),
                ["sk"] = new(ResidentNotification.DueKey(notification.CreatedAt, notification.Id)),
                ["payload"] = new(JsonSerializer.Serialize(notification)),
                ["attempts"] = new() { N = "0" }
            },
            ConditionExpression = "attribute_not_exists(pk)"
        }
    };
}
