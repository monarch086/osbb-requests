using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Domain.Residents;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Postgres.Bot;

public sealed partial class PostgresCommandRepository
{
    public async Task<ResidentRegistration?> GetRegistrationAsync(long userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await ResidentAsync(db, userId, ct);
        if (row is null) return null;
        var phone = await db.ResidentPhones.Where(phone => phone.ResidentId == row.Id && phone.IsPrimary)
            .Select(phone => phone.Phone).SingleOrDefaultAsync(ct);
        var consent = await db.ResidentConsents.FindAsync([row.Id], ct);
        return ResidentProfileMapping.ToProfile(row, complex, phone, consent);
    }

    public async Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken ct)
    {
        if (buildingId != complex) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Memberships(db, userId).AnyAsync(row => row.Active, ct);
    }

    public async Task<bool> ResidentPhoneExistsAsync(string complexId, string phone, CancellationToken ct)
    {
        if (complexId != complex || !PhoneNumber.TryNormalize(phone, out var number)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        return await PhoneExistsAsync(db, number!, ct);
    }

    private Task<bool> PhoneExistsAsync(DataContext db, string phone, CancellationToken ct) =>
        ApprovedPhones(db).AnyAsync(row => row.Phone == phone, ct);

    private IQueryable<ResidentPhone> ApprovedPhones(DataContext db) =>
        db.ResidentPhones.Where(row => row.Resident.ComplexId == complexId &&
            row.Resident.Stage == RegistrationStage.Approved && !row.Resident.PersonalDataDeleted);

    public async Task<OwnerApartments?> GetOwnerApartmentsAsync(long userId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var owner = await ResidentAsync(db, userId, ct);
        if (!CanManageApartments(owner) || owner!.RegistrationId is not { } registration) return null;
        var rows = await ApartmentRows(db, userId, registration).OrderBy(row => row.SubmittedAt).ThenBy(row => row.ApartmentId).ToListAsync(ct);
        return rows.Count == 0 ? null : new(complex, userId, registration, rows.Count,
            rows.Select(row => new OwnerApartment(row.ApartmentId, row.BuildingNumber, row.ApartmentNumber,
                row.SubmittedAt, row.Status, row.ReviewedAt, row.ReviewedBy)).ToArray());
    }

    private async Task WriteResidentAsync(DataContext db, CommandEnvelope command, CommandOutcome outcome, CancellationToken ct)
    {
        if (outcome.Registration is { } registration)
            await WriteRegistrationAsync(db, command, registration, ct);
        if (outcome.Profile is { } profile)
            await EditProfileAsync(db, command, outcome.ProfileDialog, profile, ct);
        if (outcome.Apartment is { } submission)
            await SubmitApartmentAsync(db, command, submission, ct);
    }

    private async Task WriteRegistrationAsync(DataContext db, CommandEnvelope command, RegistrationChange registration, CancellationToken ct)
    {
        var value = registration.Value;
        CheckOwner(command, value.UserId, value.BuildingId);
        if (registration.AutomaticallyApproved) ValidateAutomaticApproval(registration);
        var row = await SaveProfileAsync(db, value, registration.ExpectedVersion, ct);
        if (registration.AutomaticallyApproved)
            await ApproveAsync(db, row, value, ct);
        if (registration.AutomaticallyApproved || value.Stage == RegistrationStage.Pending)
            Enqueue(db, AdministratorNotifications.NewResident(value));
    }

    private async Task EditProfileAsync(DataContext db, CommandEnvelope command, ProfileDialogChange? dialog, ProfileUpdate update, CancellationToken ct)
    {
        var value = update.Value;
        CheckOwner(command, value.UserId, value.BuildingId);
        if (dialog?.Value is not { Active: false } || dialog.Value.ComplexId != complex)
            throw new InvalidOperationException("Profile dialog is still open.");
        var membership = await RequireMembershipAsync(db, value.UserId, ct);
        var row = await ResidentAsync(db, value.UserId, ct);
        if (row is null || row.RegistrationId != value.Id) throw new InvalidOperationException("Resident registration changed.");
        if (value.PersonalDataDeleted && value.Stage != RegistrationStage.Deleted)
            throw new InvalidOperationException("Deleted data must revoke resident access.");
        await SaveProfileAsync(db, value, update.ExpectedVersion, ct);
        if (value.PersonalDataDeleted)
        {
            membership.Active = false;
            await db.ResidentApartments.Where(apartment => apartment.ComplexId == complexId && apartment.TelegramUserId == value.UserId)
                .ExecuteDeleteAsync(ct);
            await db.BotCommandReceipts.Where(reply => reply.ComplexId == complexId && reply.UserId == value.UserId && reply.Delivered)
                .ExecuteUpdateAsync(set => set.SetProperty(reply => reply.Text, ""), ct);
        }
    }

    private async Task<Resident> SaveProfileAsync(DataContext db, ResidentRegistration value, int? expected, CancellationToken ct)
    {
        var row = await ResidentAsync(db, value.UserId, ct);
        if (row?.Version != expected) throw new InvalidOperationException("Resident profile changed.");
        var isNew = row is null;
        if (row is null)
        {
            row = new() { ComplexId = complexId, TelegramUserId = value.UserId };
            db.Residents.Add(row);
        }
        ResidentProfileMapping.Apply(row, value);
        await SavePhonesAsync(db, row, value, isNew, ct);
        await SaveConsentAsync(db, row, value, isNew, ct);
        return row;
    }

    private static async Task SavePhonesAsync(DataContext db, Resident row, ResidentRegistration value, bool isNew, CancellationToken ct)
    {
        var phone = value.Phone is null || value.PersonalDataDeleted ? null : PhoneNumber.Normalize(value.Phone);
        if (!isNew)
            await db.ResidentPhones.Where(number => number.ResidentId == row.Id &&
                (value.PersonalDataDeleted || number.IsPrimary && number.Phone != phone)).ExecuteDeleteAsync(ct);
        if (phone is null) return;
        var primary = isNew ? null : await db.ResidentPhones.FindAsync([row.Id, phone], ct);
        if (primary is null)
            db.ResidentPhones.Add(new() { Resident = row, Phone = phone, IsPrimary = true });
        else
            primary.IsPrimary = true;
    }

    private static async Task SaveConsentAsync(DataContext db, Resident row, ResidentRegistration value, bool isNew, CancellationToken ct)
    {
        var consent = isNew ? null : await db.ResidentConsents.FindAsync([row.Id], ct);
        if (value.ConsentAt is null && value.ConsentVersion is null)
        {
            if (consent is not null) db.ResidentConsents.Remove(consent);
            return;
        }
        if (consent is null)
        {
            consent = new() { Resident = row };
            db.ResidentConsents.Add(consent);
        }
        consent.ConsentedAt = value.ConsentAt;
        consent.Version = value.ConsentVersion;
    }

    private static void ValidateAutomaticApproval(RegistrationChange registration)
    {
        var value = registration.Value;
        if (value.Stage != RegistrationStage.Approved || value.ApprovedAt is null || value.ApprovedAt != value.UpdatedAt ||
            registration.ExpectedVersion != value.Version - 1 || value.PersonalDataDeleted ||
            value.ResidentType is not (ResidentType.FamilyMember or ResidentType.Tenant) ||
            value.ConsentAt is null || string.IsNullOrWhiteSpace(value.ConsentVersion) ||
            string.IsNullOrWhiteSpace(value.Phone) || string.IsNullOrWhiteSpace(value.FirstName) || string.IsNullOrWhiteSpace(value.LastName) ||
            !ComplexLayout.BuildingNumbers.Contains(value.BuildingNumber, StringComparer.Ordinal) ||
            !int.TryParse(value.ApartmentNumber, out var apartment) || apartment is < 1 or > 999)
            throw new InvalidOperationException("Automatic approval requires a completed family member or tenant registration.");
    }

    private async Task ApproveAsync(DataContext db, Resident row, ResidentRegistration profile, CancellationToken ct)
    {
        var member = await Memberships(db, profile.UserId).SingleOrDefaultAsync(ct);
        if (member is null)
        {
            member = new()
            {
                SourceId = sourceId,
                ComplexId = complexId,
                TelegramUserId = row.TelegramUserId,
                Key = FormattableString.Invariant($"LOCAL#{complexId}#MEMBER#{row.TelegramUserId}")
            };
            db.ResidentMemberships.Add(member);
        }
        // Other registration steps have already persisted the resident row.
        member.ResidentId = row.Id;
        member.Active = true;
        member.RegistrationId = profile.Id;
        member.ReviewedBy = "system";
        member.ReviewedAt = profile.ApprovedAt;
        db.ResidentEvents.Add(new()
        {
            SourceId = sourceId,
            Key = $"LOCAL#{complexId}#{profile.Id}#DECIDED",
            RegistrationId = profile.Id,
            EventKey = "EVENT#DECIDED",
            Actor = "system",
            CreatedAt = profile.ApprovedAt,
            Data = JsonSerializer.Serialize(new { actor = "system", decision = "Approved", approvalMode = "Automatic", createdAt = profile.ApprovedAt })
        });
    }

    private async Task SubmitApartmentAsync(DataContext db, CommandEnvelope command, ApartmentSubmission submission, CancellationToken ct)
    {
        var value = submission.Value;
        CheckOwner(command, value.OwnerId, value.ComplexId);
        await RequireMembershipAsync(db, command.UserId, ct);
        var owner = await ResidentAsync(db, command.UserId, ct);
        if (!CanManageApartments(owner) || owner!.Version != submission.ProfileVersion || owner.RegistrationId != value.RegistrationId)
            throw new InvalidOperationException("Apartment owner changed.");
        var rows = ApartmentRows(db, command.UserId, value.RegistrationId);
        var count = await rows.CountAsync(ct);
        if (count != (submission.ExpectedVersion ?? 0) || value.Version != count + 1)
            throw new InvalidOperationException("Apartment requests changed.");
        var apartment = value.Items.Single(item => item.Id == submission.ApartmentId && item.Status == ApartmentReviewStatus.Pending);
        if (await rows.AnyAsync(row => row.BuildingNumber == apartment.BuildingNumber && row.ApartmentNumber == apartment.ApartmentNumber &&
            row.Status != ApartmentReviewStatus.Rejected, ct))
            throw new InvalidOperationException("Apartment has already been submitted.");
        db.ResidentApartments.Add(new()
        {
            ComplexId = complexId,
            TelegramUserId = command.UserId,
            ResidentId = owner.Id,
            ApartmentId = apartment.Id,
            RegistrationId = value.RegistrationId,
            BuildingNumber = apartment.BuildingNumber,
            ApartmentNumber = apartment.ApartmentNumber,
            Status = apartment.Status,
            SubmittedAt = apartment.SubmittedAt
        });
        Enqueue(db, AdministratorNotifications.NewApartmentRegistration(complex, apartment));
    }

    private Task<Resident?> ResidentAsync(DataContext db, long userId, CancellationToken ct) =>
        db.Residents.SingleOrDefaultAsync(row => row.ComplexId == complexId && row.TelegramUserId == userId, ct);

    private IQueryable<ResidentMembership> Memberships(DataContext db, long userId) =>
        db.ResidentMemberships.Where(row => row.SourceId == sourceId && row.ComplexId == complexId && row.TelegramUserId == userId);

    private async Task<ResidentMembership> RequireMembershipAsync(DataContext db, long userId, CancellationToken ct) =>
        await Memberships(db, userId).SingleOrDefaultAsync(row => row.Active, ct) ??
        throw new InvalidOperationException("Resident membership is unavailable.");

    private IQueryable<ResidentApartment> ApartmentRows(DataContext db, long userId, string registration) =>
        db.ResidentApartments.Where(row => row.ComplexId == complexId && row.TelegramUserId == userId && row.RegistrationId == registration);

    private static bool CanManageApartments(Resident? owner) =>
        owner is { Stage: RegistrationStage.Approved, ResidentType: ResidentType.Owner, PersonalDataDeleted: false };
}
