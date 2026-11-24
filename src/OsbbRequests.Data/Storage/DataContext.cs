using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OsbbRequests.Data.Storage;

public sealed class DataContext(DbContextOptions<DataContext> options) : DbContext(options)
{
    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
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
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SourceRecord>(e => { e.ToTable("source_records"); e.HasKey(x => new { x.Source, x.Table, x.Key }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<ImportRun>(e => { e.ToTable("import_runs"); e.HasKey(x => x.Id); });
        model.Entity<ResidentApartment>(e =>
        {
            e.ToTable("resident_apartments", table =>
            {
                table.HasCheckConstraint("CK_resident_apartments_number", "\"ApartmentNumber\" BETWEEN 1 AND 999");
                table.HasCheckConstraint("CK_resident_apartments_status", "\"Status\" IN ('Pending', 'Approved', 'Rejected')");
            });
            e.HasKey(x => new { x.Source, x.ComplexId, x.TelegramUserId, x.ApartmentId });
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.Source, x.ComplexId, x.BuildingNumber, x.ApartmentNumber });
        });
        model.Entity<Resident>(e =>
        {
            e.ToTable("residents");
            e.HasKey(x => new { x.Source, x.ComplexId, x.TelegramUserId });
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.ResumeAfterConsent).HasConversion<string>();
            e.Property(x => x.ResidentType).HasConversion<string>();
        });
        model.Entity<Request>(e => { e.ToTable("requests"); e.HasKey(x => new { x.Source, x.OwnerId, x.RequestId }); e.Property(x => x.Payload).HasColumnType("jsonb"); e.HasIndex(x => new { x.Source, x.ComplexId, x.Status, x.CreatedAt }); });
        model.Entity<Administrator>(e => { e.ToTable("administrators"); e.HasKey(x => new { x.Source, x.UserId }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<RequestKeyItem>(e => { e.ToTable("request_key_items"); e.HasKey(x => new { x.Source, x.OwnerId, x.RequestId, x.Position }); e.HasIndex(x => x.Phone); e.HasOne<Request>().WithMany().HasForeignKey(x => new { x.Source, x.OwnerId, x.RequestId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<RequestEvent>(e => { e.ToTable("request_events"); e.HasKey(x => new { x.Source, x.Key }); e.HasIndex(x => new { x.Source, x.RequestId, x.CreatedAt }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<ResidentEvent>(e => { e.ToTable("resident_events"); e.HasKey(x => new { x.Source, x.Key }); e.HasIndex(x => new { x.Source, x.RegistrationId }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<ResidentPhone>(e => { e.ToTable("resident_phones"); e.HasKey(x => new { x.Source, x.ComplexId, x.TelegramUserId, x.Phone }); e.HasIndex(x => x.Phone); e.HasOne<Resident>().WithMany().HasForeignKey(x => new { x.Source, x.ComplexId, x.TelegramUserId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ResidentConsent>(e => { e.ToTable("resident_consents"); e.HasKey(x => new { x.Source, x.ComplexId, x.TelegramUserId }); e.HasOne<Resident>().WithMany().HasForeignKey(x => new { x.Source, x.ComplexId, x.TelegramUserId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ResidentMembership>(e => { e.ToTable("resident_memberships"); e.HasKey(x => new { x.Source, x.Key }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<BroadcastMessage>(e => { e.ToTable("broadcast_messages"); e.HasKey(x => new { x.Source, x.Key }); e.HasIndex(x => new { x.Source, x.ComplexId, x.CreatedAt }); });
        model.Entity<Notification>(e => { e.ToTable("notifications"); e.HasKey(x => new { x.Source, x.Key }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<AdministratorEvent>(e => { e.ToTable("administrator_events"); e.HasKey(x => new { x.Source, x.Key }); e.Property(x => x.Data).HasColumnType("jsonb"); });
        model.Entity<AdministratorPermission>(e => { e.ToTable("administrator_permissions"); e.HasKey(x => new { x.Source, x.UserId }); e.HasOne<Administrator>().WithMany().HasForeignKey(x => new { x.Source, x.UserId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<AdministratorNotificationSetting>(e => { e.ToTable("administrator_notification_settings"); e.HasKey(x => new { x.Source, x.UserId }); e.HasOne<Administrator>().WithMany().HasForeignKey(x => new { x.Source, x.UserId }).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ApiDailyStatistic>(e => { e.ToTable("api_daily_statistics"); e.HasKey(x => new { x.Source, x.Key }); });
    }
}
public sealed class DataContextFactory : IDesignTimeDbContextFactory<DataContext>
{
    public DataContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DataContext>().UseNpgsql("Host=localhost;Database=osbb;Username=osbb;Password=design-time").Options);
}
