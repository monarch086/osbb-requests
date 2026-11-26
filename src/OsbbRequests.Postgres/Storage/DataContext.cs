using Microsoft.EntityFrameworkCore;

namespace OsbbRequests.DataImport.Storage;

public sealed class DataContext(DbContextOptions<DataContext> options) : DbContext(options)
{
    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<ImportSource> ImportSources => Set<ImportSource>();
    public DbSet<Complex> Complexes => Set<Complex>();
    public DbSet<ResidentApartment> ResidentApartments => Set<ResidentApartment>();
    public DbSet<Resident> Residents => Set<Resident>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Administrator> Administrators => Set<Administrator>();
    public DbSet<RequestKeyItem> RequestKeyItems => Set<RequestKeyItem>();
    public DbSet<RequestEvent> RequestEvents => Set<RequestEvent>();
    public DbSet<ResidentEvent> ResidentEvents => Set<ResidentEvent>();
    public DbSet<ResidentPhone> ResidentPhones => Set<ResidentPhone>();
    public DbSet<ResidentConsent> ResidentConsents => Set<ResidentConsent>();
    public DbSet<ResidentMembership> ResidentMemberships => Set<ResidentMembership>();
    public DbSet<BroadcastMessage> BroadcastMessages => Set<BroadcastMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AdministratorEvent> AdministratorEvents => Set<AdministratorEvent>();
    public DbSet<AdministratorPermission> AdministratorPermissions => Set<AdministratorPermission>();
    public DbSet<AdministratorNotificationSetting> AdministratorNotificationSettings => Set<AdministratorNotificationSetting>();
    public DbSet<ApiDailyStatistic> ApiDailyStatistics => Set<ApiDailyStatistic>();
    public DbSet<BotConversation> BotConversations => Set<BotConversation>();
    public DbSet<BotCommandReceipt> BotCommandReceipts => Set<BotCommandReceipt>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<BotConversation>(e =>
        {
            e.ToTable("bot_conversations");
            e.HasKey(x => new { x.ComplexId, x.Bot, x.UserId, x.Kind });
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasOne<Complex>().WithMany().HasForeignKey(x => x.ComplexId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<BotCommandReceipt>(e =>
        {
            e.ToTable("bot_command_receipts");
            e.HasKey(x => new { x.ComplexId, x.Bot, x.UpdateId });
            e.HasOne<Complex>().WithMany().HasForeignKey(x => x.ComplexId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SourceRecord>(e => { e.ToTable("source_records"); e.HasKey(x => new { x.SourceId, x.Table, x.Key }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<ImportRun>(e => { e.ToTable("import_runs"); e.HasKey(x => x.Id); });
        model.Entity<ResidentApartment>(e =>
        {
            e.ToTable("resident_apartments", table =>
            {
                table.HasCheckConstraint("CK_resident_apartments_number", "\"ApartmentNumber\" BETWEEN 1 AND 999");
                table.HasCheckConstraint("CK_resident_apartments_status", "\"Status\" IN ('Pending', 'Approved', 'Rejected')");
            });
            e.HasKey(x => new { x.ComplexId, x.TelegramUserId, x.ApartmentId });
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.ComplexId, x.BuildingNumber, x.ApartmentNumber });
            e.HasOne<Complex>().WithMany().HasForeignKey(x => x.ComplexId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Resident>().WithMany().HasForeignKey(x => x.ResidentId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<Resident>(e =>
        {
            e.ToTable("residents");
            e.HasIndex(x => new { x.ComplexId, x.TelegramUserId }).IsUnique();
            e.HasOne<Complex>().WithMany().HasForeignKey(x => x.ComplexId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.ResumeAfterConsent).HasConversion<string>();
            e.Property(x => x.ResidentType).HasConversion<string>();
        });
        model.Entity<Request>(e =>
        {
            e.ToTable("requests");
            e.HasKey(x => new { x.SourceId, x.OwnerId, x.RequestId });
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Visibility).HasConversion<string>();
            e.Property(x => x.Scope).HasConversion<string>();
            e.HasIndex(x => new { x.SourceId, x.ComplexId, x.Status, x.CreatedAt });
            e.HasOne<Complex>().WithMany().HasForeignKey(x => new { x.SourceId, x.ComplexId })
                .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Administrator>(e => { e.ToTable("administrators"); e.HasKey(x => new { x.SourceId, x.UserId }); });
        model.Entity<RequestKeyItem>(e => { e.ToTable("request_key_items"); e.HasKey(x => new { x.SourceId, x.OwnerId, x.RequestId, x.Position }); e.HasIndex(x => x.Phone); e.HasOne<Request>().WithMany().HasForeignKey(x => new { x.SourceId, x.OwnerId, x.RequestId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<RequestEvent>(e => { e.ToTable("request_events"); e.HasKey(x => new { x.SourceId, x.Key }); e.HasIndex(x => new { x.SourceId, x.RequestId, x.CreatedAt }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<ResidentEvent>(e => { e.ToTable("resident_events"); e.HasKey(x => new { x.SourceId, x.Key }); e.HasIndex(x => new { x.SourceId, x.RegistrationId }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<ResidentPhone>(e =>
        {
            e.ToTable("resident_phones");
            e.HasKey(x => new { x.ResidentId, x.Phone });
            e.HasIndex(x => x.Phone);
            e.HasIndex(x => x.ResidentId).IsUnique().HasFilter("\"IsPrimary\"");
            e.HasOne(x => x.Resident).WithMany().HasForeignKey(x => x.ResidentId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ResidentConsent>(e => { e.ToTable("resident_consents"); e.HasKey(x => x.ResidentId); e.HasOne(x => x.Resident).WithOne().HasForeignKey<ResidentConsent>(x => x.ResidentId).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ResidentMembership>(e =>
        {
            e.ToTable("resident_memberships");
            e.HasKey(x => new { x.SourceId, x.Key });
            e.HasOne<Complex>().WithMany().HasForeignKey(x => new { x.SourceId, x.ComplexId })
                .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Resident>().WithMany().HasForeignKey(x => x.ResidentId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<BroadcastMessage>(e =>
        {
            e.ToTable("broadcast_messages");
            e.HasKey(x => new { x.SourceId, x.Key });
            e.HasIndex(x => new { x.SourceId, x.ComplexId, x.CreatedAt });
            e.HasOne<Complex>().WithMany().HasForeignKey(x => new { x.SourceId, x.ComplexId })
                .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => new { x.SourceId, x.Key });
            e.HasIndex(x => new { x.ComplexId, x.DueAt }).HasFilter("\"Kind\" = 'LOCAL_PENDING'");
            e.HasIndex(x => new { x.ComplexId, x.ExpiresAt }).HasFilter("\"ExpiresAt\" IS NOT NULL");
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasOne<Complex>().WithMany().HasForeignKey(x => new { x.SourceId, x.ComplexId })
                .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AdministratorEvent>(e => { e.ToTable("administrator_events"); e.HasKey(x => new { x.SourceId, x.Key }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<AdministratorPermission>(e => { e.ToTable("administrator_permissions"); e.HasKey(x => new { x.SourceId, x.UserId }); e.HasOne<Administrator>().WithMany().HasForeignKey(x => new { x.SourceId, x.UserId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<AdministratorNotificationSetting>(e => { e.ToTable("administrator_notification_settings"); e.HasKey(x => new { x.SourceId, x.UserId }); e.HasOne<Administrator>().WithMany().HasForeignKey(x => new { x.SourceId, x.UserId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ApiDailyStatistic>(e => { e.ToTable("api_daily_statistics"); e.HasKey(x => new { x.SourceId, x.Date, x.Endpoint }); });
        model.Entity<ImportSource>(e =>
        {
            e.ToTable("import_sources");
            e.HasIndex(x => new { x.AwsAccountId, x.Region, x.Stage }).IsUnique().AreNullsDistinct(false);
        });
        model.Entity<Complex>(e =>
        {
            e.ToTable("complexes");
            e.HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique();
        });
        foreach (var entity in model.Model.GetEntityTypes().Where(type => type.FindProperty(nameof(ImportRun.SourceId)) is not null))
        {
            model.Entity(entity.ClrType).HasOne(typeof(ImportSource), null).WithMany()
                .HasForeignKey(nameof(ImportRun.SourceId)).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
