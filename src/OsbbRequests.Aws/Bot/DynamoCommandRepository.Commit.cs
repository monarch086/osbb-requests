using OsbbRequests.Domain.Models;
using OsbbRequests.Domain;
using OsbbRequests.Shared.Residents;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.CommandProcessor.Models;

namespace OsbbRequests.Aws.Bot;

public sealed partial class DynamoCommandRepository
{
    public async Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome,
        CancellationToken cancellationToken)
    {
        var reply = new StoredReply(command.UpdateId, command.UserId, command.ChatId, outcome.Reply, false, outcome.Keyboard, outcome.Profile?.Value.PersonalDataDeleted == true || outcome.Registration?.AutomaticallyApproved == true);
        var replyItem = Key("UPDATE#" + Number(command.UpdateId), "REPLY");
        replyItem["payload"] = new AttributeValue(JsonSerializer.Serialize(reply));
        replyItem["delivered"] = new AttributeValue { BOOL = false };
        replyItem["expiresAt"] = new AttributeValue { N = Number(clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds()) };
        var writes = new List<TransactWriteItem> { Put(replyItem) };
        AddRequestWrites(writes, command, outcome);
        AddDialogWrites(writes, command, outcome);
        await AddProfileWritesAsync(writes, command, outcome, cancellationToken);
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
            return reply;
        }
        catch (TransactionCanceledException)
        {
            // A concurrent invocation may already have committed this update.
            var existing = await GetReplyAsync(command.UpdateId, cancellationToken);
            if (existing is not null)
                return existing;
            throw;
        }
    }

    private void AddRequestWrites(List<TransactWriteItem> writes, CommandEnvelope command, CommandOutcome outcome)
    {
        if (outcome.Comment is { } comment) AddCommentWrites(writes, command, comment);
        if (outcome.Pagination is { } pagination) writes.Add(CursorWrite(command, pagination));
        if (outcome.PageNavigation is { } navigation) AddPageNavigationWrite(writes, command, navigation);
        if (outcome.NewRequest is { } request) AddNewRequestWrites(writes, command, request, outcome.OwnerProfileVersion);
        if (outcome.Deletion is { } deletion) AddDeletionWrites(writes, command, deletion);
    }

    private void AddDialogWrites(List<TransactWriteItem> writes, CommandEnvelope command, CommandOutcome outcome)
    {
        if (outcome.Lookup is { } lookup) AddLookupWrite(writes, command, lookup);
        if (outcome.Draft is { } draft) AddDraftWrite(writes, command, draft);
        if (outcome.Registration is { } registration) AddRegistrationWrites(writes, command, registration);
    }

    private void AddCommentWrites(List<TransactWriteItem> writes, CommandEnvelope command, ResidentComment comment)
    {
        if (comment.OwnerId != command.UserId || comment.ComplexId != complexId || string.IsNullOrWhiteSpace(comment.Text) || comment.Text.Length > 1000)
            throw new InvalidOperationException("Invalid resident comment.");
        writes.Add(new() { ConditionCheck = MembershipCheck(command.UserId, comment.ComplexId) });
        writes.Add(new()
        {
            ConditionCheck = new()
            {
                TableName = tableName,
                Key = Key("USER#" + Number(command.UserId), RequestKey(comment.RequestId)),
                ConditionExpression = "attribute_exists(pk) AND requestBuildingId = :complex",
                ExpressionAttributeValues = new() { [":complex"] = new(comment.ComplexId) }
            }
        });
        var history = Key("REQUEST#" + Number(comment.RequestId), "EVENT#" + comment.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#UPDATE#" + Number(command.UpdateId));
        history["actorId"] = new() { N = Number(command.UserId) };
        history["action"] = new("ResidentComment");
        history["createdAt"] = new(comment.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        history["resolution"] = new(comment.Text);
        writes.Add(Put(history));
    }

    private void AddPageNavigationWrite(List<TransactWriteItem> writes, CommandEnvelope command, PageNavigationChange navigation)
    {
        if (navigation.Value.UserId != command.UserId) throw new InvalidOperationException("Page navigation owner mismatch.");
        var item = Key("USER#" + Number(command.UserId), "PAGE_NAVIGATION");
        item["payload"] = new(JsonSerializer.Serialize(navigation.Value));
        item["version"] = new() { N = Number(navigation.Value.Version) };
        item["expiresAt"] = new() { N = Number(navigation.Value.ExpiresAt) };
        writes.Add(VersionedPut(item, navigation.ExpectedVersion, "#v"));
    }

    private void AddNewRequestWrites(List<TransactWriteItem> writes, CommandEnvelope command, MaintenanceRequest request, int? ownerProfileVersion)
    {
        if (!ComplexLayout.IsValidRequest(request.Category, request.Scope, request.BuildingNumber, request.Floor, request.Entrance))
            throw new InvalidOperationException("Request scope is required.");
        if (request.Category == RequestCategories.KeyOrder)
            AddKeyOrderChecks(writes, command, request, ownerProfileVersion);
        var item = Key("USER#" + Number(request.OwnerId), RequestKey(request.Id));
        item["payload"] = new AttributeValue(JsonSerializer.Serialize(request));
        item["requestBuildingId"] = new AttributeValue(request.BuildingId);
        item["requestCreatedAt"] = new AttributeValue(request.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#" + Number(request.Id));
        item["requestPriorityCreatedAt"] = new(RequestPriorityOrder.Key(request));
        item["requestStatus"] = new AttributeValue(request.Status.ToString());
        item["requestCategory"] = new AttributeValue(request.Category);
        item["searchText"] = new AttributeValue((request.Location + " " + request.Description).ToLowerInvariant());
        item["requestTarget"] = new AttributeValue(ComplexLayout.Target(request.Scope, request.BuildingNumber));
        if (request.Floor is { } floor) item["requestFloor"] = new AttributeValue { N = Number(floor) };
        item["version"] = new AttributeValue { N = "1" };
        writes.Add(Put(item));
        var history = Key("REQUEST#" + Number(request.Id), "EVENT#CREATED");
        history["actorId"] = new AttributeValue { N = Number(request.OwnerId) };
        history["createdAt"] = new AttributeValue(request.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        history["status"] = new AttributeValue("New");
        if (request.Entrance is { } entrance) history["entrance"] = new AttributeValue { N = Number(entrance) };
        history["target"] = new AttributeValue(ComplexLayout.Target(request.Scope, request.BuildingNumber));
        if (request.Floor is { } historyFloor) history["floor"] = new AttributeValue { N = Number(historyFloor) };
        writes.Add(Put(history));
        writes.Add(NotificationWrite(AdministratorNotifications.NewRequest(request)));
    }

    private void AddKeyOrderChecks(List<TransactWriteItem> writes, CommandEnvelope command, MaintenanceRequest request, int? ownerProfileVersion)
    {
        if (request.OwnerId != command.UserId || ownerProfileVersion is not { } profileVersion ||
            request.Visibility != RequestVisibility.Private || !ValidKeyOrderItems(request))
            throw new InvalidOperationException("Invalid key order authorization.");
        writes.Add(new()
        {
            ConditionCheck = new()
            {
                TableName = residentsTable is not null && complexId is not null ? residentsTable : tableName,
                Key = residentsTable is not null && complexId is not null
                    ? new() { ["pk"] = new(OsbbRequests.Shared.Residents.ResidentRecordKey.Create(request.BuildingId, "USER#" + Number(command.UserId), "REGISTRATION").Key) }
                    : Key("USER#" + Number(command.UserId), "REGISTRATION"),
                ConditionExpression = "#v = :version",
                ExpressionAttributeNames = new() { ["#v"] = "version" },
                ExpressionAttributeValues = new() { [":version"] = new() { N = Number(profileVersion) } }
            }
        });
        writes.Add(new() { ConditionCheck = MembershipCheck(command.UserId, request.BuildingId) });
    }

    private static bool ValidKeyOrderItems(MaintenanceRequest request)
    {
        if (request.KeyQuantity is not >= 1) return false;
        if (request.KeyItems is not { } keys) return request.KeyType is "Магніт" or "Перепустка";
        return keys.Count == request.KeyQuantity && keys.Count <= 30 && keys.All(ValidKeyOrderItem) &&
            keys.DistinctBy(key => ResidentPhoneIndex.Normalize(key.Phone), StringComparer.Ordinal).Count() == keys.Count;
    }

    private static bool ValidKeyOrderItem(KeyOrderItem key) =>
        key.Type is "Магніт" or "Перепустка" && ResidentPhoneIndex.TryNormalize(key.Phone, out _);

    private void AddDeletionWrites(List<TransactWriteItem> writes, CommandEnvelope command, RequestDeletion deletion)
    {
        if (deletion.OwnerId != command.UserId) throw new InvalidOperationException("Deletion owner mismatch.");
        writes.Add(new() { ConditionCheck = MembershipCheck(command.UserId, deletion.ComplexId) });
        writes.Add(new()
        {
            Delete = new()
            {
                TableName = tableName,
                Key = Key("USER#" + Number(command.UserId), RequestKey(deletion.Id)),
                ConditionExpression = "#version = :expected AND requestStatus = :new AND requestBuildingId = :complex",
                ExpressionAttributeNames = new() { ["#version"] = "version" },
                ExpressionAttributeValues = new()
                {
                    [":expected"] = new() { N = Number(deletion.ExpectedVersion) },
                    [":new"] = new("New"),
                    [":complex"] = new(deletion.ComplexId)
                }
            }
        });
        var history = Key("REQUEST#" + Number(deletion.Id), "EVENT#DELETED");
        history["actorId"] = new() { N = Number(command.UserId) };
        history["createdAt"] = new(clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        history["action"] = new("Deleted");
        history["previousStatus"] = new("New");
        writes.Add(Put(history));
    }

    private void AddLookupWrite(List<TransactWriteItem> writes, CommandEnvelope command, RequestLookupChange lookup)
    {
        if (lookup.Value.UserId != command.UserId) throw new InvalidOperationException("Lookup owner mismatch.");
        writes.Add(VersionedPut(DialogItem(command.UserId, "REQUEST_LOOKUP", lookup.Value, lookup.Value.Version), lookup.ExpectedVersion));
    }

    private void AddDraftWrite(List<TransactWriteItem> writes, CommandEnvelope command, RequestDraftChange draft)
    {
        if (draft.Value.UserId != command.UserId) throw new InvalidOperationException("Draft owner mismatch.");
        writes.Add(VersionedPut(DialogItem(command.UserId, "REQUEST_DRAFT", draft.Value, draft.Value.Version), draft.ExpectedVersion));
    }

    private static Dictionary<string, AttributeValue> DialogItem<T>(long userId, string kind, T value, int version)
    {
        var item = Key("USER#" + Number(userId), kind);
        item["payload"] = new(JsonSerializer.Serialize(value));
        item["version"] = new() { N = Number(version) };
        return item;
    }

    private TransactWriteItem VersionedPut(Dictionary<string, AttributeValue> item, int? expectedVersion, string versionAlias = "#version")
    {
        var write = Put(item);
        if (expectedVersion is { } version)
        {
            write.Put.ConditionExpression = versionAlias + " = :expected";
            write.Put.ExpressionAttributeNames = new() { [versionAlias] = "version" };
            write.Put.ExpressionAttributeValues = new() { [":expected"] = new() { N = Number(version) } };
        }
        return write;
    }

    private void AddRegistrationWrites(List<TransactWriteItem> writes, CommandEnvelope command, RegistrationChange change)
    {
        var registration = change.Value;
        if (registration.UserId != command.UserId)
            throw new InvalidOperationException("Registration owner mismatch.");
        var item = RegistrationItem("USER#" + Number(registration.UserId), "REGISTRATION", registration);
        if (residentsTable is null || complexId is null) writes.Add(VersionedPut(item, change.ExpectedVersion));
        if (residentsTable is not null)
        {
            writes.Add(ResidentMirrorWrite(registration, change.ExpectedVersion));
            writes.Add(RelatedResidentPut(item, registration.BuildingId, change.ExpectedVersion));
        }
        if (change.AutomaticallyApproved)
            AddAutomaticApprovalWrites(writes, registration, change.ExpectedVersion);
        else if (registration.Stage == RegistrationStage.Pending)
            AddPendingRegistrationWrites(writes, registration);
    }

    private void AddPendingRegistrationWrites(List<TransactWriteItem> writes, ResidentRegistration registration)
    {
        var registrationMeta = RegistrationItem("REGISTRATION#" + registration.Id, "META", registration);
        var pendingEntry = RegistrationItem("BUILDING#" + registration.BuildingId, "REGISTRATION#" + registration.Id, registration);
        AddPendingRegistrationRecord(writes, registrationMeta, registration.BuildingId);
        AddPendingRegistrationRecord(writes, pendingEntry, registration.BuildingId);
        writes.Add(NotificationWrite(AdministratorNotifications.NewResident(registration)));
    }

    private void AddPendingRegistrationRecord(List<TransactWriteItem> writes, Dictionary<string, AttributeValue> item, string buildingId)
    {
        if (residentsTable is null || complexId is null) writes.Add(Put(item));
        if (residentsTable is not null) writes.Add(RelatedResidentPut(item, buildingId, immutable: true));
    }
}
