using System.Net;
using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

public static class StoLlmAuth
{
    public const int JwtMarginSeconds = 60;
    public const int DefaultProxyPort = 8765;
    public static string DefaultStoWattHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sto-watt");

    public static bool IsJwtValid(string? token, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var payload = parts[1];
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/')));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds)) return false;
            var clock = now ?? DateTimeOffset.UtcNow;
            return clock.ToUnixTimeSeconds() < seconds - JwtMarginSeconds;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { return false; }
    }

    public static string? ReadEnvToken(string stoWattHome, DateTimeOffset? now = null)
    {
        var path = Path.Combine(stoWattHome, ".env");
        if (!File.Exists(path)) return null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                var i = trimmed.IndexOf('=');
                if (trimmed[..i].Trim() != "DEVOPS_STOTOKEN") continue;
                var value = trimmed[(i + 1)..].Trim();
                return IsJwtValid(value, now) ? value : null;
            }
        }
        catch (IOException) { }
        return null;
    }

    public static async Task<string?> FetchBridgeTokenAsync(HttpClient http, int port = DefaultProxyPort, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/devops-token");
            request.Headers.TryAddWithoutValidation("X-Sto-Agent", "1");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString()?.Trim() : null;
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        { return null; }
    }

    public static async Task<string?> ResolveToken(string manualSecret, string stoWattHome,
        Func<int, Task<string?>> bridge, DateTimeOffset? now = null)
    {
        var manual = manualSecret.Trim();
        if (manual.Length > 0) return manual;
        var env = ReadEnvToken(stoWattHome, now);
        if (env is not null) return env;
        var fromBridge = await bridge(DefaultProxyPort);
        return string.IsNullOrWhiteSpace(fromBridge) ? null : fromBridge.Trim();
    }

    public static bool IsAutoAvailable(string? stoWattHome = null, Func<bool>? proxyUp = null)
    {
        var home = stoWattHome ?? DefaultStoWattHome;
        if (Directory.Exists(home)) return true;
        return proxyUp?.Invoke() ?? ProbeProxy(DefaultProxyPort);
    }

    public static bool ProbeProxy(int port = DefaultProxyPort)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            return task.Wait(TimeSpan.FromMilliseconds(150)) && client.Connected;
        }
        catch { return false; }
    }
}
