using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrimDeck.Core;

public sealed record StoLlmSession(string StoToken, string OpenId, string DisplayName, DateTimeOffset ObtainedAt)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(StoLlmSession session) => JsonSerializer.Serialize(session, Json);

    public static StoLlmSession? TryRead(string secret)
    {
        var text = secret.Trim();
        if (text.Length == 0 || text[0] != '{') return null;
        try
        {
            var session = JsonSerializer.Deserialize<StoLlmSession>(text, Json);
            if (session is null || string.IsNullOrWhiteSpace(session.StoToken)) return null;
            return session with
            {
                StoToken = session.StoToken.Trim(),
                OpenId = session.OpenId?.Trim() ?? "",
                DisplayName = session.DisplayName?.Trim() ?? ""
            };
        }
        catch (JsonException) { return null; }
    }
}
