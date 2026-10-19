namespace OsbbRequests.Bot.Telegram;

public interface IServiceNotifier
{
    Task NotifyFailureAsync(string messageId);
}

public sealed class ServiceNotifier(ITelegramClient telegram, long chatId) : IServiceNotifier
{
    public async Task NotifyFailureAsync(string messageId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await telegram.SendMessageAsync(chatId,
                $"ОСББ: помилка обробки команди. Message ID: {messageId}. Перевірте CloudWatch та DLQ.", timeout.Token);
        }
        catch (Exception)
        {
            // Operational notifications must never mask the processing failure.
        }
    }
}
