using System.Globalization;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Shared.Residents;

public static class ResidentDirectoryWrites
{
    private const string All = "ALL";
    private const string Building = "BUILDING";

    public static string BuildingRowKey(ResidentRegistration profile) => RowKey(profile, Building);

    private static string RowKey(ResidentRegistration profile, string kind) =>
        FormattableString.Invariant($"COMPLEX#{profile.BuildingId}#DIRECTORY_ROW#{kind}#{profile.UserId}");

    // The building row also tracks which counters already include this resident.
    public static void Add(List<TransactWriteItem> writes, string table, ResidentRegistration next,
        IReadOnlyDictionary<string, AttributeValue>? previousRow = null)
    {
        var eligible = next.Stage == RegistrationStage.Approved && !next.PersonalDataDeleted;
        if (!eligible && previousRow is null)
        {
            // A backfill may start tracking this profile after our read.
            writes.Add(new()
            {
                ConditionCheck = new()
                {
                    TableName = table,
                    Key = new() { ["pk"] = new(BuildingRowKey(next)) },
                    ConditionExpression = "attribute_not_exists(pk)"
                }
            });
            return;
        }

        var building = string.IsNullOrEmpty(next.BuildingNumber) ? "Unspecified" : next.BuildingNumber;
        var type = next.ResidentType.ToString();
        var groups = $"COMPLEX#{next.BuildingId}#APPROVED#";
        var previousVersion = previousRow?["sourceVersion"].N;
        var nameOrder = eligible ? ResidentNameOrder.Create(next.Id, next.FirstName, next.LastName) : default;
        var oversizedNameOrder = nameOrder.Ascending?.Length > ResidentNameOrder.MaxKeyBytes;
        if (eligible)
        {
            var ticks = (next.ApprovedAt ?? next.UpdatedAt).UtcTicks;
            foreach (var kind in new[] { All, Building })
            {
                var row = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new(RowKey(next, kind)),
                    ["listGroup"] = new(groups + (kind == All ? All : Building + "#" + building)),
                    ["listOrder"] = new(FormattableString.Invariant($"{ticks:D19}#{next.Id}")),
                    ["listOrderDescending"] = new(FormattableString.Invariant($"{DateTime.MaxValue.Ticks - ticks:D19}#{next.Id}")),
                    ["registrationId"] = new(next.Id),
                    ["residentType"] = new(type),
                    ["buildingNumber"] = new(building),
                    ["sourceVersion"] = Number(next.Version)
                };
                if (!oversizedNameOrder)
                {
                    row["listNameOrder"] = new() { B = new MemoryStream(nameOrder.Ascending!, writable: false) };
                    row["listNameOrderDescending"] = new() { B = new MemoryStream(nameOrder.Descending!, writable: false) };
                }
                if (next.FirstName is not null) row["firstName"] = new(next.FirstName);
                if (next.LastName is not null) row["lastName"] = new(next.LastName);
                if (next.ApartmentNumber is not null) row["apartmentNumber"] = new(next.ApartmentNumber);
                var put = new Put { TableName = table, Item = row, ConditionExpression = "attribute_not_exists(pk)" };
                if (previousVersion is not null)
                {
                    put.ConditionExpression = "sourceVersion = :previous";
                    put.ExpressionAttributeValues = new() { [":previous"] = new() { N = previousVersion } };
                }
                writes.Add(new() { Put = put });
            }
        }
        else
        {
            foreach (var kind in new[] { All, Building })
            {
                writes.Add(new()
                {
                    Delete = new()
                    {
                        TableName = table,
                        Key = new() { ["pk"] = new(RowKey(next, kind)) },
                        ConditionExpression = "sourceVersion = :previous",
                        ExpressionAttributeValues = new() { [":previous"] = new() { N = previousVersion } }
                    }
                });
            }
        }

        var deltas = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        if (previousRow is not null)
        {
            var oldType = previousRow["residentType"].S;
            AddDelta(All, oldType, -1);
            AddDelta(Building + "#" + previousRow["buildingNumber"].S, oldType, -1);
        }
        if (eligible)
        {
            AddDelta(All, type, 1);
            AddDelta(Building + "#" + building, type, 1);
        }
        foreach (var (scope, changes) in deltas)
        {
            var names = new Dictionary<string, string>();
            var values = new Dictionary<string, AttributeValue>();
            var terms = new List<string>();
            foreach (var (residentType, change) in changes.Where(pair => pair.Value != 0))
            {
                var index = terms.Count.ToString(CultureInfo.InvariantCulture);
                names["#type" + index] = residentType;
                values[":delta" + index] = Number(change);
                terms.Add($"#type{index} :delta{index}");
            }
            var set = "";
            if (oversizedNameOrder && (scope == All || scope == Building + "#" + building))
            {
                names["#blocked"] = "nameOrderUnsupported";
                values[":true"] = new() { BOOL = true };
                set = "SET #blocked = :true";
            }
            if (terms.Count == 0 && set.Length == 0) continue;
            var updateExpression = terms.Count == 0 ? set : (set.Length == 0 ? "" : set + " ") + "ADD " + string.Join(", ", terms);
            writes.Add(new()
            {
                Update = new()
                {
                    TableName = table,
                    Key = new() { ["pk"] = new($"COMPLEX#{next.BuildingId}#DIRECTORY_COUNT#{scope}") },
                    UpdateExpression = updateExpression,
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values
                }
            });
        }

        void AddDelta(string scope, string residentType, int delta)
        {
            if (!deltas.TryGetValue(scope, out var changes)) deltas[scope] = changes = new(StringComparer.Ordinal);
            changes[residentType] = changes.GetValueOrDefault(residentType) + delta;
        }
    }

    private static AttributeValue Number(int value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
}
