using System.Text.Json;

namespace BrimDeck.Core;

public sealed class StoSsoClient(HttpClient http, string? baseUrl = null)
{
    // Production external SSO (llmKeysByUser is also production). Test/daily:
    // https://baseinfo-usercenterserviceapi-hq-test.sto.cn
    public const string DefaultBaseUrl = "https://baseinfo-usercenterserviceapi.sto.cn";
    // Registered subsystem: login systemCode and LogOnStoTokenByAuthorizationCode?systemCode=.
    public const string CategoryCode = "STO-BrimDeck";

    private readonly string _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

    public static string LoginUrl(string baseUrl, string returnUrl)
    {
        var root = baseUrl.TrimEnd('/');
        return root + "/Authenticate/Login?autoLogin=true&systemCode=" + Uri.EscapeDataString(CategoryCode)
            + "&returnUrl=" + Uri.EscapeDataString(returnUrl);
    }

    public string LoginUrl(string returnUrl) => LoginUrl(_base, returnUrl);

    public async Task<StoLlmSession> ExchangeAsync(string authorizationCode, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var code = authorizationCode.Trim();
        if (code.Length == 0) throw new InvalidDataException(Loc.T("缺少授权码。", "Authorization code is missing."));

        // Working Apifox flow: query systemCode=<subsystem>&authorizationCode=<Code> (not form UI/categoryCode).
        var url = _base + "/WebAPIV46/API/LogOn/LogOnStoTokenByAuthorizationCode"
            + "?systemCode=" + Uri.EscapeDataString(CategoryCode)
            + "&authorizationCode=" + Uri.EscapeDataString(code);
        using var response = await http.PostAsync(url, content: null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException(Loc.T(
                $"换票失败 HTTP {(int)response.StatusCode}：{TrimBody(body)}",
                $"Token exchange failed HTTP {(int)response.StatusCode}: {TrimBody(body)}"));

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        bool ok = (root.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.True)
            || (root.TryGetProperty("StatusCode", out var sc) && sc.ValueKind == JsonValueKind.String
                && (string.Equals(sc.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sc.GetString(), "200", StringComparison.OrdinalIgnoreCase)));
        var message = root.TryGetProperty("StatusMessage", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString() : null;
        if (!ok)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message)
                ? Loc.T("单点登录换票失败。", "SSO token exchange failed.") + " " + TrimBody(body)
                : message!);
        var token = root.TryGetProperty("StoToken", out var st) ? st.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidDataException(Loc.T("单点登录未返回 StoToken。", "SSO did not return a StoToken.") + " " + TrimBody(body));
        string openId = "", display = "";
        if (root.TryGetProperty("UserInfo", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            openId = user.TryGetProperty("OpenId", out var oid) ? oid.GetString()?.Trim() ?? "" : "";
            display = user.TryGetProperty("RealName", out var rn) ? rn.GetString()?.Trim() ?? "" : "";
            if (display.Length == 0)
                display = user.TryGetProperty("UserName", out var un) ? un.GetString()?.Trim() ?? "" : "";
        }
        return new StoLlmSession(token, openId, display, now ?? DateTimeOffset.UtcNow);
    }

    public async Task SignOutAsync(string openId, CancellationToken ct = default)
    {
        var id = openId.Trim();
        if (id.Length == 0) return;
        var url = _base + "/webapiv46/api/logon/signOut?systemCode=" + Uri.EscapeDataString(CategoryCode)
            + "&openId=" + Uri.EscapeDataString(id);
        try
        {
            using var response = await http.PostAsync(url, content: null, ct);
            _ = response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    private static string TrimBody(string body)
    {
        var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 240 ? text : text[..240] + "…";
    }
}
