using System.Globalization;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;

namespace OsbbRequests.AdminApi;

public sealed partial class ApiHandler
{
    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleResidentsAsync(APIGatewayHttpApiV2ProxyRequest request, AdminUser actor,
        string? method, string path, string[] segments, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var cursor = query.GetValueOrDefault("cursor");
        if (method == "GET" && path == "/admin/residents/keys")
            return Reply(200, await service.ListResidentsWithKeysAsync(actor, query.GetValueOrDefault("page"), ct));
        if (method == "PATCH" && segments is ["admin", "residents", "keys", var keyOwner, var keyRequest, var keyPosition])
        {
            AdminService.RequireKeyEdit(actor);
            if (!long.TryParse(keyOwner, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) || ownerId <= 0 ||
                !long.TryParse(keyRequest, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId) || requestId < 0 ||
                !int.TryParse(keyPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                throw new ApiError(400, "Некоректний ключ.");
            await service.UpdateResidentKeyAsync(actor, ownerId, requestId, position, Body<ResidentKeyUpdate>(request), ct);
            return Ok();
        }
        if (method == "GET" && path is "/admin/residents/export" or "/admin/registrations/export")
        {
            var pending = path == "/admin/registrations/export";
            AdminService.RequireResidentDirectoryAccess(actor, pending);
            RequireDedicatedExportRoute(request, path);
            return Workbook(await service.ExportResidentsAsync(actor, pending, DirectoryFilters(query), ct));
        }
        if (method == "GET" && path is "/admin/residents/directory" or "/admin/registrations/directory")
        {
            var pending = path == "/admin/registrations/directory";
            return Reply(200, await service.ListResidentDirectoryAsync(actor, pending, DirectoryFilters(query),
                query.GetValueOrDefault("page"), cursor, query.GetValueOrDefault("compact") == "true", ct));
        }
        if (method == "GET" && path == "/admin/residents")
            return Reply(200, await service.ListResidentsAsync(actor, cursor, ct));
        if (method == "GET" && path == "/admin/registrations/count")
            return Reply(200, new { count = await service.CountRegistrationsAsync(actor, ct) });
        if (method == "GET" && path == "/admin/registrations")
            return Reply(200, await service.ListRegistrationsAsync(actor, cursor, ct));
        if (method == "PATCH" && segments is ["admin", "registrations", var residentId, "data"])
        {
            AdminService.RequireRegistrationReview(actor);
            AdminService.ValidateRegistrationReference(residentId);
            await service.UpdateResidentDataAsync(actor, residentId, Body<ResidentDataChangeRequest>(request), ct);
            return Ok();
        }
        if (method == "PATCH" && segments is ["admin", "registrations", var registrationId, "type"])
        {
            AdminService.RequireRegistrationReview(actor);
            AdminService.ValidateRegistrationReference(registrationId);
            await service.ChangeResidentTypeAsync(actor, registrationId, Body<ResidentTypeChangeRequest>(request), ct);
            return Ok();
        }
        if (segments is ["admin", "apartment-reviews", ..])
        {
            AdminService.RequireApartmentReview(actor);
            if (method == "GET" && segments is ["admin", "apartment-reviews", "count"])
                return Reply(200, new { count = await service.CountApartmentReviewsAsync(actor, ct) });
            if (method == "GET" && segments.Length == 2)
                return Reply(200, await service.ListApartmentReviewsAsync(actor, cursor, ct));
            if (method == "POST" && segments is ["admin", "apartment-reviews", var ownerId, var apartmentId])
            {
                AdminService.ValidateRegistrationReference(ownerId);
                AdminService.ValidateRegistrationReference(apartmentId);
                await service.DecideApartmentAsync(actor, ownerId, apartmentId, Body<ApartmentReviewDecision>(request), ct);
                return Ok();
            }
        }
        if (method == "GET" && segments is ["admin", "residents", var detailId])
            return Reply(200, await service.GetRegisteredResidentAsync(actor, detailId, ct));
        if (method == "GET" && segments is ["admin", "registrations", var approvalId, "approval"])
            return Reply(200, await service.GetRegistrationApprovalAsync(actor, approvalId, ct));
        if (method == "GET" && segments is ["admin", "registrations", var historyId, "history"])
            return Reply(200, await service.GetRegistrationHistoryAsync(actor, historyId, cursor, ct));
        if (method == "POST" && segments is ["admin", "registrations", var reference, "decision"])
        {
            AdminService.RequireRegistrationReview(actor);
            AdminService.ValidateRegistrationReference(reference);
            await service.DecideRegistrationAsync(actor, reference, Body<RegistrationDecision>(request), ct);
            return Ok();
        }
        throw RouteNotFound();
    }

    private static ResidentDirectoryFilters DirectoryFilters(IReadOnlyDictionary<string, string> query) =>
        new(query.GetValueOrDefault("building"), query.GetValueOrDefault("residentType"),
            query.GetValueOrDefault("sort"), query.GetValueOrDefault("direction"));
}
