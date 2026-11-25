using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OsbbRequests.DataImport.Storage;

public sealed class DataContextFactory : IDesignTimeDbContextFactory<DataContext>
{
    public DataContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("OSBB_IMPORT_HOST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Необхідно встановити OSBB_IMPORT_HOST_CONNECTION_STRING.");
        return new DataContext(new DbContextOptionsBuilder<DataContext>().UseNpgsql(connection).Options);
    }
}
