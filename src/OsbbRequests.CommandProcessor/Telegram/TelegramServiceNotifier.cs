using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace OsbbRequests.CommandProcessor.Telegram;

public interface IServiceNotifier
{
    Task NotifyFailureAsync(string operation, Exception error, string? requestId = null, Action<string>? log = null);
}

public sealed class TelegramServiceNotifier(HttpClient http, ISecretProvider secrets, string serviceName, string stage,
    string chatId = TelegramServiceNotifier.ServiceNotificationChatId) : IServiceNotifier
{
    public const string ServiceNotificationChatId = "38627946";

    public async Task NotifyFailureAsync(string operation, Exception error, string? requestId = null, Action<string>? log = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            // Exception messages/ToString, payloads and URLs can contain secrets or residents' data.
            var diagnostics = error is ITelegramFailureDetails telegram
                ? "Telegram: " + Field(telegram.SafeDiagnostics.Trim(), 400) + "\n"
                : "";
            var text = "Помилка ОСББ\n" + $"Сервіс: {Field(serviceName)}\nСередовище: {Field(stage)}\n" +
                $"Етап: {Field(operation)}\nТип: {Field(error.GetType().Name)}\nID: {Field(requestId ?? "-")}\n" +
                diagnostics + string.Join("\n", new StackTrace(error, false).GetFrames().Take(5).Select(frame =>
                    Field(frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name))) +
                $"\nПодробиці: журнал контейнера.";
            var token = await secrets.GetAsync(timeout.Token);
            using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chatId, text, link_preview_options = new { is_disabled = true } }, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"operation=notify_service outcome=failed statusCode={(int)response.StatusCode}");
                return;
            }
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                log?.Invoke("operation=notify_service outcome=rejected");
        }
        catch (Exception failure)
        {
            log?.Invoke($"operation=notify_service outcome=failed errorType={failure.GetType().Name}");
        }
    }

    private static string Field(string value, int limit = 150) => value[..Math.Min(value.Length, limit)].Replace('\r', ' ').Replace('\n', ' ');
}

public static class ServiceNotifications
{
    // Also protect processing from a failing custom/injected notifier.
    public static async Task ReportSafelyAsync(this IServiceNotifier? notifier, string operation, Exception error,
        string? requestId = null, Action<string>? log = null)
    {
        try
        {
            if (notifier is not null) await notifier.NotifyFailureAsync(operation, error, requestId, log);
        }
        catch (Exception failure)
        {
            log?.Invoke($"operation=notify_service outcome=failed errorType={failure.GetType().Name}");
        }
    }
}
