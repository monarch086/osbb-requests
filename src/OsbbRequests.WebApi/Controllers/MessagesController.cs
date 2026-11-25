using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/messages")]
public sealed class MessagesController(MessageService messages) : AdminControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.Messages)]
    public async Task<Page<BroadcastMessage>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await messages.ListMessagesAsync(Actor, cursor, ct);

    [HttpPost]
    [RequireAdminAccess(AdminPermission.Messages)]
    public async Task<BroadcastMessage> Send([FromBody] MessageChange change, CancellationToken ct) =>
        await messages.SendMessageAsync(Actor, change, ct);
}
