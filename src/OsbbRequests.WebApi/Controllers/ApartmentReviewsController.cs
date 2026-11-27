using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/apartment-reviews")]
[RequireAdminAccess]
public sealed class ApartmentReviewsController(ResidentService residents) : ControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.ReviewApartments)]
    public async Task<Page<ApartmentReviewEntry>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await residents.ListApartmentReviewsAsync(HttpContext.GetAdminActor(), cursor, ct);

    [HttpGet("count")]
    [RequireAdminAccess(AdminPermission.ReviewApartments)]
    public async Task<IActionResult> Count(CancellationToken ct) =>
        Ok(new { count = await residents.CountApartmentReviewsAsync(HttpContext.GetAdminActor(), ct) });

    [HttpPost("{owner}/{apartment}")]
    [RequireAdminAccess(AdminPermission.ReviewApartments)]
    public async Task<IActionResult> Decide([FromRoute] string owner, [FromRoute] string apartment,
        [FromBody] ApartmentReviewDecision change, CancellationToken ct)
    {
        await residents.DecideApartmentAsync(HttpContext.GetAdminActor(), owner, apartment, change, ct);
        return AdminResponses.Saved();
    }
}
