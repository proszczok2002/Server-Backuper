using System.Net.Http.Json;
using System.Text.Json;

namespace ServerBackuper.Core;

public sealed class TelegramNotifier(HttpClient httpClient, TextLog log)
{
    public async Task<(bool Success, string Message)> SendAsync(
        TelegramSettings settings, string message, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return (true, "Powiadomienia Telegram są wyłączone.");
        if (string.IsNullOrWhiteSpace(settings.ChatId)) return (false, "Nie wpisano Chat ID.");

        string token;
        try { token = SecretProtector.Unprotect(settings.BotTokenProtected); }
        catch (Exception ex) { return (false, "Nie można odszyfrować tokenu: " + ex.Message); }
        if (string.IsNullOrWhiteSpace(token)) return (false, "Nie wpisano tokenu bota.");

        try
        {
            var endpoint = $"https://api.telegram.org/bot{token}/sendMessage";
            using var response = await httpClient.PostAsJsonAsync(endpoint,
                new { chat_id = settings.ChatId, text = message }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                    return (true, "Wiadomość Telegram została wysłana.");
            }
            log.Write($"Telegram HTTP {(int)response.StatusCode}: {body}");
            return (false, $"Telegram zwrócił HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Write("Błąd Telegram: " + ex.Message);
            return (false, "Nie udało się połączyć z Telegramem: " + ex.Message);
        }
    }
}
