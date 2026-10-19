using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Telegram;

namespace OsbbRequests.Bot.Commands;

public sealed class CommandProcessor(ICommandRepository repository, CommandHandler handler, ITelegramClient telegram)
{
    public async Task ProcessAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.UpdateId < 0 || envelope.UserId <= 0 || envelope.ChatId != envelope.UserId ||
            string.IsNullOrWhiteSpace(envelope.Text) || envelope.Text.Length > 4096)
            throw new InvalidOperationException("Invalid command envelope.");

        var reply = await repository.GetReplyAsync(envelope.UpdateId, cancellationToken);
        if (reply is null)
        {
            var outcome = await handler.HandleAsync(envelope, cancellationToken);
            reply = await repository.CommitAsync(envelope, outcome, cancellationToken);
        }

        if (reply.UserId != envelope.UserId || reply.ChatId != envelope.ChatId)
            throw new InvalidOperationException("Update identity mismatch.");
        if (reply.Delivered)
            return;

        await telegram.SendMessageAsync(reply.ChatId, reply.Text, cancellationToken);
        await repository.MarkDeliveredAsync(reply.UpdateId, cancellationToken);
    }
}
