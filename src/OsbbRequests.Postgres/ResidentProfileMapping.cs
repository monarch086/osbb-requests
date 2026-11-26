using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Postgres;

internal static class ResidentProfileMapping
{
    public static ResidentRegistration ToProfile(Resident row, string complex, string? phone, ResidentConsent? consent) =>
        new(row.RegistrationId ?? "", row.TelegramUserId, complex, row.Stage, row.Version,
            row.CreatedAt ?? DateTimeOffset.UnixEpoch, row.UpdatedAt ?? row.CreatedAt ?? DateTimeOffset.UnixEpoch,
            phone, row.FirstName, row.LastName, row.DecisionReason, row.BuildingNumber, row.ApartmentNumber,
            row.PersonalDataDeleted, row.PersonalDataDeletedAt, row.ApprovedAt, row.Email, consent?.ConsentedAt, consent?.Version,
            row.ResumeAfterConsent, row.ResidentType, row.TypeBeforeAddress, row.TypeChangeCount);

    public static void Apply(Resident row, ResidentRegistration profile)
    {
        row.RegistrationId = profile.Id;
        row.Stage = profile.Stage;
        row.Version = profile.Version;
        row.CreatedAt = profile.CreatedAt;
        row.UpdatedAt = profile.UpdatedAt;
        row.FirstName = profile.FirstName;
        row.LastName = profile.LastName;
        row.DecisionReason = profile.DecisionReason;
        row.BuildingNumber = profile.BuildingNumber;
        row.ApartmentNumber = profile.ApartmentNumber;
        row.PersonalDataDeleted = profile.PersonalDataDeleted;
        row.PersonalDataDeletedAt = profile.PersonalDataDeletedAt;
        row.ApprovedAt = profile.ApprovedAt;
        row.Email = profile.Email;
        row.ResumeAfterConsent = profile.ResumeAfterConsent;
        row.ResidentType = profile.ResidentType;
        row.TypeBeforeAddress = profile.TypeBeforeAddress;
        row.TypeChangeCount = profile.TypeChangeCount;
    }
}
