using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Telegram;

namespace OsbbRequests.CommandListener;

public sealed class CommandPollingLoop(TelegramClient telegram,
    Func<CommandEnvelope, CancellationToken, Task> processCommand, string botUsername)
{
    public async Task RunAsync(Action<string> log, CancellationToken cancellationToken)
    {
        var offset = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var updates = await PollAsync(offset, log, cancellationToken);
                if (updates is null)
                    continue;
                if (updates.Count == 0)
                    offset = 0; // Telegram already received the previous offset. Allow a new ID sequence after a long idle period.

                foreach (var update in updates.OrderBy(update => update.UpdateId))
                {
                    if (!await ProcessAsync(update, log, cancellationToken))
                        break;
                    offset = update.UpdateId!.Value + 1;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown leaves any unfinished command pending in Telegram.
        }
    }

    private async Task<IReadOnlyList<TelegramUpdate>?> PollAsync(long offset, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await telegram.GetUpdatesAsync(offset, ct);
        }
        catch (TelegramApiException error) when (error.StatusCode is 401 or 403 or 409)
        {
            // Telegram rejects polling if a webhook or another polling subscriber is active.
            throw;
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            log("operation=poll_commands errorType=" + error.GetType().Name + TelegramFailureDiagnostics.For(error));
            await WaitToRetryAsync(error, ct);
            return null;
        }
    }

    private async Task<bool> ProcessAsync(TelegramUpdate update, Action<string> log, CancellationToken ct)
    {
        if (TelegramCommandMapper.FromUpdate(update, botUsername) is not { } command)
            return true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await processCommand(command, timeout.Token);
            return true;
        }
        catch (TelegramRecipientUnavailableException error) when (error.Response?.Reason is
            TelegramRejectionReason.BotBlocked or TelegramRejectionReason.UserDeactivated or TelegramRejectionReason.ChatNotFound)
        {
            log($"operation=process_command updateId={command.UpdateId} outcome=undeliverable" + TelegramFailureDiagnostics.For(error));
            return true;
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            log($"operation=process_command updateId={command.UpdateId} errorType={error.GetType().Name}" + TelegramFailureDiagnostics.For(error));
            await WaitToRetryAsync(error, ct);
            return false;
        }
    }

    private static Task WaitToRetryAsync(Exception error, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(error is TelegramRateLimitException limit ? Math.Max(1, limit.RetryAfterSeconds) : 5), ct);
}
