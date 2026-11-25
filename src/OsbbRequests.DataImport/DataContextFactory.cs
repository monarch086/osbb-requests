using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OsbbRequests.DataImport.Storage;

public sealed class DataContextFactory : IDesignTimeDbContextFactory<DataContext>
{
    public DataContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DataContext>().UseNpgsql("Host=localhost;Database=osbb;Username=osbb;Password=design-time").Options);
}
