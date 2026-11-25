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
    public async Task<Page<BroadcastMessage>> List(CancellationToken ct) =>
        await messages.ListMessagesAsync(Actor, Query("cursor"), ct);

    [HttpPost]
    [RequireAdminAccess(AdminPermission.Messages)]
    public async Task<BroadcastMessage> Send(CancellationToken ct)
    {
        return await messages.SendMessageAsync(Actor, await ReadBodyAsync<MessageChange>(ct), ct);
    }
}
