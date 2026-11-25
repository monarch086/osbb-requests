using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/apartment-reviews")]
public sealed class ApartmentReviewsController(ResidentService residents) : AdminControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.ReviewApartments)]
    public async Task<Page<ApartmentReviewEntry>> List(CancellationToken ct) =>
        await residents.ListApartmentReviewsAsync(Actor, Query("cursor"), ct);

    [HttpGet("count")]
    [RequireAdminAccess(AdminPermission.ReviewApartments)]
    public async Task<IActionResult> Count(CancellationToken ct) =>
        Ok(new { count = await residents.CountApartmentReviewsAsync(Actor, ct) });

    [HttpPost("{owner}/{apartment}")]
    [RequireAdminAccess(AdminPermission.ReviewApartments)]
    public async Task<IActionResult> Decide(string owner, string apartment, CancellationToken ct)
    {
        ResidentService.ValidateRegistrationReference(owner);
        ResidentService.ValidateRegistrationReference(apartment);
        await residents.DecideApartmentAsync(Actor, owner, apartment, await ReadBodyAsync<ApartmentReviewDecision>(ct), ct);
        return Saved();
    }
}
