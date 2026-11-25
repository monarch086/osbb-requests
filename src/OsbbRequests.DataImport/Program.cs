using System.Text.RegularExpressions;
using Amazon;
using Amazon.DynamoDBv2;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.DataImport;
using OsbbRequests.DataImport.Storage;

if (args.Length is not (1 or 5) || args[0] is not ("migrate" or "import") ||
    (args[0] == "migrate" && args.Length != 1) ||
    (args[0] == "import" && (args.Length != 5 || args[1] != "--stage" || args[3] != "--region" ||
        !Regex.IsMatch(args[2], "^[a-z0-9-]+$") || !Regex.IsMatch(args[4], "^[a-z0-9-]+$"))))
{
    Console.Error.WriteLine("Використання: osbb-data migrate | osbb-data import --stage STAGE --region REGION");
    return 2;
}
var connection = Environment.GetEnvironmentVariable("OSBB_IMPORT_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("Необхідно встановити OSBB_IMPORT_CONNECTION_STRING.");
    return 2;
}
try
{
    await using var context = new DataContext(new DbContextOptionsBuilder<DataContext>().UseNpgsql(connection).Options);
    await context.Database.MigrateAsync();
    if (args[0] == "import")
    {
        using var dynamo = new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(args[4]));
        await new Importer(context, dynamo, args[2], args[4]).ImportAsync(CancellationToken.None);
    }
    else Console.WriteLine("Міграції бази даних застосовано.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Операцію не виконано: {error.GetType().Name}. Облікові дані та записи джерела не виводяться.");
    return 1;
}
