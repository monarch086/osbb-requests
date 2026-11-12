using System.Globalization;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<ResidentDirectoryPage> ListResidentDirectoryAsync(bool pending, int page, string? buildingNumber,
        string? residentType, string? sort, string? direction, CancellationToken ct)
    {
        if (page < 1 || page > 1000000) throw new ApiError(400, "Некоректна сторінка.");
        var items = await ListResidentExportAsync(pending, buildingNumber, residentType, sort, direction, ct);
        const int pageSize = 25;
        var actualPage = Math.Min(page, Math.Max(1, (items.Count + pageSize - 1) / pageSize));
        return new(items.Skip((actualPage - 1) * pageSize).Take(pageSize).ToList(), actualPage, pageSize, items.Count);
    }

    public async Task<IReadOnlyList<ResidentRegistration>> ListResidentExportAsync(bool pending, string? buildingNumber,
        string? residentType, string? sort, string? direction, CancellationToken ct)
    {
        if ((buildingNumber is not (null or "" or "Unspecified" or "4Б" or "4В" or "4Г" or "4Д")) ||
            (residentType is not (null or "" or "Owner" or "FamilyMember" or "Tenant" or "Employee")) ||
            (sort is not (null or "" or "registeredAt" or "lastName")) || (direction is not (null or "" or "asc" or "desc")))
            throw new ApiError(400, "Некоректні параметри списку мешканців.");
        if (string.IsNullOrEmpty(residentsTable)) throw new InvalidOperationException("Residents table is not configured.");

        // The index must be read in full to return an exact filtered count. Reuse those
        // records for locale-aware ordering; the existing name index is not ICU ordered.
        var items = new List<ResidentRegistration>();
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var response = await db.QueryAsync(new QueryRequest
            {
                TableName = residentsTable,
                IndexName = "ResidentsByDate",
                KeyConditionExpression = "directory = :directory",
                ExpressionAttributeValues = new() { [":directory"] = S("COMPLEX#" + building + "#" + (pending ? "Pending" : "Approved")) },
                ExclusiveStartKey = cursor
            }, ct);
            foreach (var item in response.Items ?? [])
            {
                var resident = Payload<ResidentRegistration>(item);
                if (resident.BuildingId != building || resident.Stage != (pending ? RegistrationStage.Pending : RegistrationStage.Approved) || resident.PersonalDataDeleted) continue;
                if (buildingNumber == "Unspecified" ? !string.IsNullOrEmpty(resident.BuildingNumber) :
                    !string.IsNullOrEmpty(buildingNumber) && resident.BuildingNumber != buildingNumber) continue;
                if (!string.IsNullOrEmpty(residentType) && resident.ResidentType.ToString() != residentType) continue;
                items.Add(resident);
                if (items.Count > 1_048_575) throw new ApiError(413, "Забагато мешканців. Звузьте фільтри та спробуйте ще раз.");
            }
            cursor = response.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });

        var compare = CultureInfo.GetCultureInfo("uk-UA").CompareInfo;
        var sign = direction == "asc" ? 1 : -1;
        items.Sort((a, b) =>
        {
            int result;
            if (pending) result = string.CompareOrdinal(a.Id, b.Id);
            else if (sort == "lastName")
            {
                var left = a.LastName?.Trim() ?? "";
                var right = b.LastName?.Trim() ?? "";
                if ((left.Length == 0) != (right.Length == 0)) return left.Length == 0 ? 1 : -1;
                result = compare.Compare(left, right, CompareOptions.IgnoreCase);
                if (result == 0) result = compare.Compare(a.FirstName ?? "", b.FirstName ?? "", CompareOptions.IgnoreCase);
                result *= sign;
            }
            else result = DateTimeOffset.Compare(a.ApprovedAt ?? a.UpdatedAt, b.ApprovedAt ?? b.UpdatedAt) * sign;
            return result != 0 ? result : string.CompareOrdinal(a.Id, b.Id);
        });
        return items;
    }
}
