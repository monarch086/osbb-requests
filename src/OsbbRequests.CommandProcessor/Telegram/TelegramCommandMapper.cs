using OsbbRequests.CommandProcessor.Commands;
using OsbbRequests.CommandProcessor.Models;

namespace OsbbRequests.CommandProcessor.Telegram;

public static class TelegramCommandMapper
{
    public static CommandEnvelope? FromUpdate(TelegramUpdate? update, string botUsername)
    {
        if (update is not
            {
                UpdateId: >= 0,
                Message: { From: { IsBot: false, Id: > 0 } from, Chat: { Type: "private" } chat } message
            } || from.Id != chat.Id)
            return null;

        var text = message.Text ?? "";
        if (!HasSupportedContent(text, message.Contact) || IsAddressedToAnotherBot(text, botUsername))
            return null;

        return new CommandEnvelope(update.UpdateId.Value, from.Id, chat.Id, text, message.Contact,
            message.ReplyToMessage?.MessageId);
    }

    private static bool HasSupportedContent(string text, TelegramContact? contact) =>
        text.Length <= 4096 && contact?.PhoneNumber is not { Length: > 64 } &&
        (!string.IsNullOrWhiteSpace(text) || contact is not null);

    private static bool IsAddressedToAnotherBot(string text, string botUsername) =>
        text.StartsWith('/') && text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].Contains('@') &&
        BotCommand.Parse(text, botUsername) is null;
}
