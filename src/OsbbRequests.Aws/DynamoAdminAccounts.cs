using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;

namespace OsbbRequests.Aws;

public sealed class DynamoAdminAccounts(IAmazonDynamoDB db, string adminsTable, string dataTable, string building) : IAdministratorAccounts
{
    private static AttributeValue S(string value) => new(value);
    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> AdminKey(string id) => new() { ["UserId"] = S(id) };

    public static AdminUser ReadAdmin(Dictionary<string, AttributeValue> item) => new(item["UserId"].S,
        item["Email"].S, item.GetValueOrDefault("DisplayName")?.S ?? item["Email"].S,
        item.GetValueOrDefault("IsActive")?.BOOL == true, item.GetValueOrDefault("IsSuperAdmin")?.BOOL == true,
        int.Parse(item.GetValueOrDefault("Version")?.N ?? "0", CultureInfo.InvariantCulture),
        item.GetValueOrDefault("TelegramChatId")?.S, item.GetValueOrDefault("NotifyNewResidents")?.BOOL == true,
        item.GetValueOrDefault("NotifyNewRequests")?.BOOL == true, item.GetValueOrDefault("NotifyNewAdministrators")?.BOOL == true,
        new PagePermissions(item.GetValueOrDefault("AccessComplexRequests")?.BOOL ?? true,
            item.GetValueOrDefault("AccessKeyOrders")?.BOOL ?? true, item.GetValueOrDefault("AccessRegisteredResidents")?.BOOL ?? true,
            item.GetValueOrDefault("AccessPendingResidents")?.BOOL ?? true, item.GetValueOrDefault("AccessMessages")?.BOOL ?? false),
        item.GetValueOrDefault("NotifyNewKeyOrders")?.BOOL ?? item.GetValueOrDefault("NotifyNewRequests")?.BOOL ?? false,
        item.GetValueOrDefault("NotifyNewApartmentRegistrations")?.BOOL ?? item.GetValueOrDefault("NotifyNewResidents")?.BOOL ?? false);

    public async Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct) =>
        await db.GetItemAsync(new GetItemRequest { TableName = adminsTable, Key = AdminKey(userId), ConsistentRead = true }, ct)
            is { Item.Count: > 0 } response ? ReadAdmin(response.Item) : null;

    public async Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct)
    {
        try
        {
            // IsSuperAdmin is intentionally absent. The Lambda IAM role cannot write that attribute.
            var now = DateTimeOffset.UtcNow;
            var account = new Update
            {
                TableName = adminsTable,
                Key = AdminKey(identity.UserId),
                UpdateExpression = "SET Email = :email, DisplayName = :name, IsActive = :inactive, Directory = :directory, CreatedAt = :now, #v = :version",
                ConditionExpression = "attribute_not_exists(UserId)",
                ExpressionAttributeNames = new() { ["#v"] = "Version" },
                ExpressionAttributeValues = new()
                {
                    [":email"] = S(identity.Email),
                    [":name"] = S(identity.DisplayName),
                    [":inactive"] = new() { BOOL = false },
                    [":directory"] = S("ADMINS"),
                    [":now"] = S(now.ToString("O")),
                    [":version"] = N(1)
                }
            };
            await db.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems = [new() { Update = account }, DynamoNotificationWrite.Create(dataTable, building, AdministratorNotifications.NewAdministrator(identity.UserId, identity.Email, building, now))]
            }, ct);
        }
        catch (TransactionCanceledException)
        {
            if (await GetAdminAsync(identity.UserId, ct) is { } existing) return existing;
            throw;
        }
        return await GetAdminAsync(identity.UserId, ct) ?? throw new InvalidOperationException("Account creation failed.");
    }
}
