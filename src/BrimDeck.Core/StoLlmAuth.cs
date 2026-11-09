using System.Text;
using System.Text.Json;

namespace BrimDeck.Core;

public static class StoLlmAuth
{
    public const int JwtMarginSeconds = 60;

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

    // secret: SSO session JSON, or a legacy bare JWT from older builds.
    public static string? ResolveToken(string secret, DateTimeOffset? now = null)
    {
        var text = secret.Trim();
        if (text.Length == 0) return null;
        if (StoLlmSession.TryRead(text) is { } session)
            return IsJwtValid(session.StoToken, now) ? session.StoToken : null;
        return IsJwtValid(text, now) ? text : null;
    }
}
