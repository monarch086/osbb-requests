using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Persistence;

public sealed partial class DynamoCommandRepository
{
    private void AddAutomaticApprovalWrites(List<TransactWriteItem> writes, ResidentRegistration profile, int? expectedVersion)
    {
        if (profile.Stage != RegistrationStage.Approved || profile.ApprovedAt != profile.UpdatedAt || profile.ApprovedAt is null ||
            expectedVersion != profile.Version - 1 || profile.PersonalDataDeleted ||
            string.IsNullOrWhiteSpace(profile.Phone) || string.IsNullOrWhiteSpace(profile.FirstName) || string.IsNullOrWhiteSpace(profile.LastName) ||
            !ComplexLayout.BuildingNumbers.Contains(profile.BuildingNumber, StringComparer.Ordinal) ||
            !int.TryParse(profile.ApartmentNumber, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var apartment) || apartment <= 0)
            throw new InvalidOperationException("Automatic approval requires a completed resident registration.");
        writes.Add(Put(RegistrationItem("REGISTRATION#" + profile.Id, "META", profile)));
        writes.Add(new()
        {
            Put = new()
            {
                TableName = tableName,
                Item = RegistrationItem("BUILDING#" + profile.BuildingId, "RESIDENT#" + profile.UserId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), profile)
            }
        });
        var membership = Key("USER#" + Number(profile.UserId), "MEMBERSHIP");
        membership["buildingId"] = new(profile.BuildingId);
        membership["active"] = new() { BOOL = true };
        membership["reviewedBy"] = new("system");
        membership["reviewedAt"] = new(profile.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writes.Add(new()
        {
            Put = new()
            {
                TableName = tableName,
                Item = membership,
                ConditionExpression = "attribute_not_exists(pk) OR buildingId = :complex",
                ExpressionAttributeValues = new() { [":complex"] = new(profile.BuildingId) }
            }
        });
        var audit = Key("REGISTRATION#" + profile.Id, "EVENT#DECIDED");
        audit["actor"] = new("system");
        audit["decision"] = new("Approved");
        audit["approvalMode"] = new("Automatic");
        audit["createdAt"] = new(profile.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writes.Add(Put(audit));
        writes.Add(NotificationWrite(AdministratorNotifications.NewResident(profile)));
    }
}
