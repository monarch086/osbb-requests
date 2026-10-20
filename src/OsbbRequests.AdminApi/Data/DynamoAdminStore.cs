using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.AdminApi.Data;

public sealed class DynamoAdminStore(IAmazonDynamoDB db, string adminsTable, string dataTable, string building) : IAdminStore
{
    private static AttributeValue S(string value) => new(value);
    private static AttributeValue N(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new() { ["pk"] = S(pk), ["sk"] = S(sk) };
    private static Dictionary<string, AttributeValue> AdminKey(string id) => new() { ["UserId"] = S(id) };
    private static string RequestKey(long id) => "REQUEST#" + id.ToString("D20", CultureInfo.InvariantCulture);
    private static AdminUser Admin(Dictionary<string, AttributeValue> item) => new(item["UserId"].S,
        item["Email"].S, item.GetValueOrDefault("DisplayName")?.S ?? item["Email"].S,
        item.GetValueOrDefault("IsActive")?.BOOL == true, item.GetValueOrDefault("IsSuperAdmin")?.BOOL == true,
        int.Parse(item.GetValueOrDefault("Version")?.N ?? "0", CultureInfo.InvariantCulture));
    private static T Payload<T>(Dictionary<string, AttributeValue> item) => JsonSerializer.Deserialize<T>(item["payload"].S)
        ?? throw new InvalidOperationException("Invalid stored payload.");
    private static ManagedRequest Managed(Dictionary<string, AttributeValue> item) => new(Payload<MaintenanceRequest>(item),
        int.Parse(item.GetValueOrDefault("version")?.N ?? "0", CultureInfo.InvariantCulture));

    private async Task<Dictionary<string, AttributeValue>?> Get(string table, Dictionary<string, AttributeValue> key, CancellationToken ct)
    {
        var response = await db.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true }, ct);
        return response.Item is { Count: > 0 } ? response.Item : null;
    }
    public async Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct) =>
        await Get(adminsTable, AdminKey(userId), ct) is { } item ? Admin(item) : null;

    public async Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct)
    {
        try
        {
            // IsSuperAdmin is intentionally absent. The Lambda IAM role cannot write that attribute.
            await db.UpdateItemAsync(new UpdateItemRequest
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
                    [":now"] = S(DateTimeOffset.UtcNow.ToString("O")),
                    [":version"] = N(1)
                }
            }, ct);
        }
        catch (ConditionalCheckFailedException) { }
        return await GetAdminAsync(identity.UserId, ct) ?? throw new InvalidOperationException("Account creation failed.");
    }

    // Cursors contain keys only, never user payload. Validate ownership/scope when using a cursor.
    private static Dictionary<string, AttributeValue>? Decode(string? cursor, string partitionName, string partition)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            if (cursor.Length > 3000) throw new FormatException();
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))!;
            if (keys.GetValueOrDefault(partitionName) != partition) throw new FormatException();
            return keys.ToDictionary(p => p.Key, p => S(p.Value));
        }
        catch (Exception e) when (e is FormatException or JsonException or NullReferenceException)
        { throw new ApiError(400, "Некоректна сторінка."); }
    }
    private static string? Encode(Dictionary<string, AttributeValue>? key) => key is { Count: > 0 }
        ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key.ToDictionary(p => p.Key, p => p.Value.S)))) : null;

    public async Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct)
    {
        var response = await db.QueryAsync(new QueryRequest
        {
            TableName = adminsTable,
            IndexName = "DirectoryIndex",
            KeyConditionExpression = "Directory = :directory",
            ExpressionAttributeValues = new() { [":directory"] = S("ADMINS") },
            Limit = 50,
            ExclusiveStartKey = Decode(cursor, "Directory", "ADMINS")
        }, ct);
        return new Page<AdminUser>((response.Items ?? []).Select(Admin).ToList(), Encode(response.LastEvaluatedKey));
    }

    private TransactWriteItem ActorCheck(AdminUser actor, bool super = false) => new()
    {
        ConditionCheck = new()
        {
            TableName = adminsTable,
            Key = AdminKey(actor.UserId),
            ConditionExpression = super ? "IsActive = :yes AND IsSuperAdmin = :yes" : "IsActive = :yes",
            ExpressionAttributeValues = new() { [":yes"] = new() { BOOL = true } }
        }
    };
    private static TransactWriteItem Put(string table, Dictionary<string, AttributeValue> item) => new()
    { Put = new() { TableName = table, Item = item, ConditionExpression = "attribute_not_exists(pk)" } };

    public async Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct)
    {
        if (target.IsSuperAdmin || target.UserId == actor.UserId) throw new ApiError(403, "Цей обліковий запис керується через AWS.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var audit = Key("ADMIN#" + target.UserId, "EVENT#" + now + "#" + Guid.NewGuid().ToString("N"));
        audit["actor"] = S(actor.Email); audit["action"] = S(change.IsActive ? "AdminAccessGranted" : "AdminAccessRevoked");
        audit["createdAt"] = S(now);
        await Transact([
            ActorCheck(actor, super: true),
            new() { Update = new() {
                TableName = adminsTable, Key = AdminKey(target.UserId),
                UpdateExpression = "SET IsActive = :active, UpdatedAt = :now, #v = :next",
                ConditionExpression = "#v = :expected",
                ExpressionAttributeNames = new() { ["#v"] = "Version" },
                ExpressionAttributeValues = new() { [":active"] = new() { BOOL = change.IsActive }, [":now"] = S(now),
                    [":next"] = N(change.Version + 1), [":expected"] = N(change.Version) }
            } }, Put(dataTable, audit)
        ], ct);
    }

    public async Task<Page<ResidentRegistration>> ListRegistrationsAsync(string? cursor, CancellationToken ct)
    {
        var pk = "BUILDING#" + building;
        var result = await db.QueryAsync(new QueryRequest
        {
            TableName = dataTable,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = new() { [":pk"] = S(pk), [":prefix"] = S("REGISTRATION#") },
            ExclusiveStartKey = Decode(cursor, "pk", pk),
            Limit = 50
        }, ct);
        return new Page<ResidentRegistration>((result.Items ?? []).Select(Payload<ResidentRegistration>).ToList(), Encode(result.LastEvaluatedKey));
    }

    public async Task<Page<RegisteredResident>> ListResidentsAsync(string? cursor, CancellationToken ct)
    {
        var pk = "BUILDING#" + building;
        var start = Decode(cursor, "pk", pk);
        if (start is not null && start.GetValueOrDefault("sk")?.S?.StartsWith("RESIDENT#", StringComparison.Ordinal) != true)
            throw new ApiError(400, "Некоректна сторінка.");
        var result = await db.QueryAsync(new QueryRequest
        {
            TableName = dataTable,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = new() { [":pk"] = S(pk), [":prefix"] = S("RESIDENT#") },
            ExclusiveStartKey = start,
            Limit = 50
        }, ct);
        var residents = (result.Items ?? []).Select(Payload<ResidentRegistration>)
            .Where(r => r.BuildingId == building && r.Stage == RegistrationStage.Approved)
            .Select(r => new RegisteredResident(r.Id, r.FirstName, r.LastName, r.Phone, r.UpdatedAt)).ToList();
        return new Page<RegisteredResident>(residents, Encode(result.LastEvaluatedKey));
    }

    public async Task DecideAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct)
    {
        var snapshot = await Get(dataTable, Key("REGISTRATION#" + reference, "META"), ct) ?? throw new ApiError(404, "Реєстрацію не знайдено.");
        var registration = Payload<ResidentRegistration>(snapshot);
        if (registration.BuildingId != building) throw new ApiError(404, "Реєстрацію не знайдено.");
        if (registration.Stage != RegistrationStage.Pending || registration.Version != decision.Version)
            throw new ApiError(409, "Рішення вже змінено. Оновіть список.");
        if (!decision.Approved && string.IsNullOrWhiteSpace(decision.Reason)) throw new ApiError(400, "Вкажіть причину відхилення.");
        if (decision.Reason?.Length > 500) throw new ApiError(400, "Пояснення: до 500 символів.");
        var now = DateTimeOffset.UtcNow;
        var next = registration with
        {
            Stage = decision.Approved ? RegistrationStage.Approved : RegistrationStage.Rejected,
            DecisionReason = decision.Reason?.Trim(),
            Version = registration.Version + 1,
            UpdatedAt = now
        };
        var user = "USER#" + registration.UserId.ToString(CultureInfo.InvariantCulture);
        TransactWriteItem Update(Dictionary<string, AttributeValue> key) => new()
        {
            Update = new()
            {
                TableName = dataTable,
                Key = key,
                UpdateExpression = "SET #payload = :next, #v = :nextVersion",
                ConditionExpression = "#payload = :previous AND #v = :expected",
                ExpressionAttributeNames = new() { ["#payload"] = "payload", ["#v"] = "version" },
                ExpressionAttributeValues = new()
                {
                    [":next"] = S(JsonSerializer.Serialize(next)),
                    [":previous"] = snapshot["payload"],
                    [":nextVersion"] = N(next.Version),
                    [":expected"] = N(decision.Version)
                }
            }
        };
        var member = Key(user, "MEMBERSHIP");
        member["buildingId"] = S(building); member["active"] = new() { BOOL = decision.Approved };
        member["reviewedBy"] = S(actor.Email); member["reviewedAt"] = S(now.ToString("O"));
        var audit = Key("REGISTRATION#" + reference, "EVENT#DECIDED");
        audit["actor"] = S(actor.Email); audit["createdAt"] = S(now.ToString("O")); audit["decision"] = S(next.Stage.ToString());
        audit["reason"] = S(next.DecisionReason ?? "");
        var writes = new List<TransactWriteItem> { ActorCheck(actor), Update(Key(user, "REGISTRATION")), Update(Key("REGISTRATION#" + reference, "META")),
            new() { Delete = new() { TableName = dataTable, Key = Key("BUILDING#" + building, "REGISTRATION#" + reference),
                ConditionExpression = "#v = :version", ExpressionAttributeNames = new() { ["#v"] = "version" },
                ExpressionAttributeValues = new() { [":version"] = N(decision.Version) } } },
            new() { Put = new() { TableName = dataTable, Item = member,
                ConditionExpression = "attribute_not_exists(pk) OR buildingId = :building",
                ExpressionAttributeValues = new() { [":building"] = S(building) } } }, Put(dataTable, audit) };
        if (decision.Approved)
        {
            var resident = Key("BUILDING#" + building, "RESIDENT#" + registration.UserId.ToString("D20", CultureInfo.InvariantCulture));
            resident["payload"] = S(JsonSerializer.Serialize(next));
            resident["version"] = N(next.Version);
            writes.Add(new() { Put = new() { TableName = dataTable, Item = resident } });
        }
        await Transact(writes, ct);
    }

    public async Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct)
    {
        var values = new Dictionary<string, AttributeValue> { [":building"] = S(building) };
        var condition = "requestBuildingId = :building";
        if (filters.From is not null || filters.To is not null)
        {
            values[":from"] = S(filters.From ?? "0000"); values[":to"] = S((filters.To ?? "9999") + "~");
            condition += " AND requestCreatedAt BETWEEN :from AND :to";
        }
        var parts = new List<string>();
        foreach (var (field, value) in new[] { ("requestStatus", filters.Status), ("requestCategory", filters.Category) })
            if (!string.IsNullOrEmpty(value)) { values[":" + field] = S(value); parts.Add(field + " = :" + field); }
        if (!string.IsNullOrWhiteSpace(filters.Search)) { values[":search"] = S(filters.Search.ToLowerInvariant()); parts.Add("contains(searchText, :search)"); }
        if (filters.Target == "Unspecified") parts.Add("attribute_not_exists(requestTarget)");
        else if (!string.IsNullOrEmpty(filters.Target)) { values[":target"] = S(filters.Target); parts.Add("requestTarget = :target"); }
        if (filters.Floor is { } floor) { values[":floor"] = N(floor); parts.Add("requestFloor = :floor"); }
        var result = await db.QueryAsync(new QueryRequest
        {
            TableName = dataTable,
            IndexName = "BuildingRequestsIndex",
            KeyConditionExpression = condition,
            ExpressionAttributeValues = values,
            FilterExpression = parts.Count > 0 ? string.Join(" AND ", parts) : null,
            ExclusiveStartKey = Decode(cursor, "requestBuildingId", building),
            ScanIndexForward = false,
            Limit = 50
        }, ct);
        return new Page<ManagedRequest>((result.Items ?? []).Select(Managed).ToList(), Encode(result.LastEvaluatedKey));
    }
    public async Task<ManagedRequest?> GetRequestAsync(long ownerId, long requestId, CancellationToken ct)
    {
        var item = await Get(dataTable, Key("USER#" + ownerId.ToString(CultureInfo.InvariantCulture), RequestKey(requestId)), ct);
        if (item is null) return null;
        var result = Managed(item);
        return result.Request.BuildingId == building ? result : null;
    }
    public async Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct)
    {
        if (!ComplexLayout.IsValid(next.Scope, next.BuildingNumber, next.Floor)) throw new ApiError(400, "Некоректна прив’язка заявки.");
        var timestamp = DateTimeOffset.UtcNow;
        var now = timestamp.ToString("O");
        var audit = Key("REQUEST#" + next.Id.ToString(CultureInfo.InvariantCulture), "EVENT#" + now + "#" + Guid.NewGuid().ToString("N"));
        audit["actor"] = S(actor.Email); audit["createdAt"] = S(now); audit["status"] = S(next.Status.ToString());
        audit["previousStatus"] = S(current.Request.Status.ToString()); audit["resolution"] = S(next.Resolution ?? "");
        audit["changes"] = S(JsonSerializer.Serialize(new { next.AssignedTo, next.Priority, next.PlannedDate, next.Visibility, next.Scope, next.BuildingNumber, next.Floor }));
        var writes = new List<TransactWriteItem> { ActorCheck(actor), new() { Update = new() {
            TableName = dataTable, Key = Key("USER#" + next.OwnerId.ToString(CultureInfo.InvariantCulture), RequestKey(next.Id)),
            UpdateExpression = "SET #payload = :payload, #v = :next, requestStatus = :status, requestTarget = :target, requestFloor = :floor",
            ConditionExpression = "#v = :expected",
            ExpressionAttributeNames = new() { ["#payload"] = "payload", ["#v"] = "version" },
            ExpressionAttributeValues = new() { [":payload"] = S(JsonSerializer.Serialize(next)), [":next"] = N(current.Version + 1),
                [":expected"] = N(current.Version), [":status"] = S(next.Status.ToString()),
                [":target"] = S(ComplexLayout.Target(next.Scope, next.BuildingNumber)),
                [":floor"] = next.Floor is { } floor ? N(floor) : new() { NULL = true } }
        } }, Put(dataTable, audit) };
        if (RequestNotification.Create(current.Request, next, current.Version + 1, timestamp) is { } notification)
        {
            var item = Key(RequestNotification.Partition(building), RequestNotification.DueKey(timestamp, notification.Id));
            item["payload"] = S(JsonSerializer.Serialize(notification));
            item["attempts"] = N(0);
            var write = Put(dataTable, item);
            write.Put.ConditionExpression = "attribute_not_exists(pk)";
            writes.Add(write);
        }
        await Transact(writes, ct);
    }
    public async Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(long requestId, CancellationToken ct)
    {
        var response = await db.QueryAsync(new QueryRequest
        {
            TableName = dataTable,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = new() { [":pk"] = S("REQUEST#" + requestId.ToString(CultureInfo.InvariantCulture)), [":prefix"] = S("EVENT#") },
            ScanIndexForward = false,
            Limit = 100
        }, ct);
        return (response.Items ?? []).Select(i => i.Where(p => p.Key is not ("pk" or "sk"))
            .ToDictionary(p => p.Key, p => p.Value.S ?? p.Value.N ?? "")).OrderByDescending(i => i.GetValueOrDefault("createdAt")).ToList();
    }
    private async Task Transact(List<TransactWriteItem> writes, CancellationToken ct)
    {
        try { await db.TransactWriteItemsAsync(new() { TransactItems = writes }, ct); }
        catch (TransactionCanceledException) { throw new ApiError(409, "Дані або права доступу змінилися. Оновіть сторінку."); }
    }
}
