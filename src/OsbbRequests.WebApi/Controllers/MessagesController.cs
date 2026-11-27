using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/messages")]
[RequireAdminAccess]
public sealed class MessagesController(MessageService messages) : ControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.Messages)]
    public async Task<Page<BroadcastMessage>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await messages.ListMessagesAsync(HttpContext.GetAdminActor(), cursor, ct);

    [HttpPost]
    [RequireAdminAccess(AdminPermission.Messages)]
    public async Task<BroadcastMessage> Send([FromBody] MessageChange change, CancellationToken ct) =>
        await messages.SendMessageAsync(HttpContext.GetAdminActor(), change, ct);
}
