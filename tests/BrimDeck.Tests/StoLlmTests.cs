using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class StoLlmTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        Auth(check);
        Mapping(check);
        Catalog(check);
        await Fetch(check);
        await Sso(check);
        if (OperatingSystem.IsWindows()) await Loopback(check);
        else check("Loopback skipped off Windows", true);
    }

    private static string Jwt(long expUnix)
    {
        static string B64(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        return $"eyJhbGciOiJub25lIn0.{B64($"{{\"exp\":{expUnix}}}")}.x";
    }

    private static void Auth(Action<string, bool> check)
    {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        check("JWT with exp more than 60s ahead is valid",
            StoLlmAuth.IsJwtValid(Jwt(now.ToUnixTimeSeconds() + 120), now));
        check("JWT within 60s of expiry is invalid",
            !StoLlmAuth.IsJwtValid(Jwt(now.ToUnixTimeSeconds() + 30), now));
        check("Malformed JWT is invalid",
            !StoLlmAuth.IsJwtValid("not.a.jwt", now) && !StoLlmAuth.IsJwtValid("", now));

        var valid = Jwt(now.ToUnixTimeSeconds() + 3600);
        var session = new StoLlmSession(valid, "openid-1", "张三", now);
        var json = StoLlmSession.Serialize(session);
        check("ResolveToken reads stoToken from session JSON",
            StoLlmAuth.ResolveToken(json, now) == valid);
        check("ResolveToken rejects expired session JWT",
            StoLlmAuth.ResolveToken(StoLlmSession.Serialize(session with { StoToken = Jwt(now.ToUnixTimeSeconds() - 10) }), now) is null);
        check("ResolveToken accepts legacy bare JWT",
            StoLlmAuth.ResolveToken("  " + valid + "  ", now) == valid);
        check("ResolveToken empty is null",
            StoLlmAuth.ResolveToken("", now) is null && StoLlmAuth.ResolveToken("{}", now) is null);
        check("TryReadSession round-trips display fields",
            StoLlmSession.TryRead(json) is { OpenId: "openid-1", DisplayName: "张三" });
    }

    private static void Mapping(Action<string, bool> check)
    {
        var row = """
        {
          "userName":"王朔","userCode":"1360770","tierName":"优享版",
          "quotaPeriod":"2026/11/01 - 2026/11/30","status":"ENABLE",
          "remainingQuota":328.49,"totalQuota":600,"usedQuota":271.51,"spent":271.51,
          "callTokenCount":2958,"todayCallTokenCount":374,
          "tenantName":"总部直营","tenantCode":"ptc",
          "keyPlain":"SECRET","llmKey":"sk-********FH3A"
        }
        """;
        using var doc = JsonDocument.Parse(row);
        var result = StoLlmUsage.FromRow(doc.RootElement);
        check("Maps balance, spend and two call texts",
            result.Metrics.Count == 4
            && result.Metrics[0] is { Kind: MetricKind.Balance, Label: "可用额度", Amount: 328.49m, Total: 600m, Currency: "CNY" }
            && result.Metrics[1] is { Kind: MetricKind.Spend, Label: "本月已用", Amount: 271.51m, Currency: "CNY" }
            && result.Metrics[2] is { Kind: MetricKind.Text, Label: "本月调用", Value: "2958", Suffix: "次" }
            && result.Metrics[3] is { Kind: MetricKind.Text, Label: "今日调用", Value: "374", Suffix: "次" });
        check("Plan and details exclude secrets",
            result.Plan == "优享版"
            && result.Details.ContainsKey("配额周期")
            && result.Details["配额周期"] == "2026/11/01 - 2026/11/30"
            && !result.Details.Values.Any(v => v.Contains("SECRET"))
            && !JsonSerializer.Serialize(result).Contains("SECRET"));
        check("usedQuota falls back to spent",
            StoLlmUsage.FromRow(JsonDocument.Parse("""{"remainingQuota":1,"totalQuota":2,"spent":9,"callTokenCount":0,"todayCallTokenCount":0,"tierName":"x"}""").RootElement)
                .Metrics[1].Amount == 9m);

        static string Fail(string json)
        {
            try { StoLlmUsage.ParseResponse(JsonDocument.Parse(json).RootElement); return ""; }
            catch (InvalidDataException ex) { return ex.Message; }
        }
        check("Non-zero code and empty data fail clearly",
            Fail("""{"code":"1","msg":"失败","data":[]}""") != ""
            && Fail("""{"code":"0","data":[]}""") != "");
    }

    private static void Catalog(Action<string, bool> check)
    {
        check("StoLlm is quota-only, keyed sto-llm, value 21",
            (int)ProviderId.StoLlm == 21
            && ProviderCatalog.Sources.Contains(ProviderId.StoLlm)
            && !ProviderCatalog.IsBuiltIn(ProviderId.StoLlm)
            && !ProviderCatalog.IsUsageSource(ProviderId.StoLlm)
            && ProviderCatalog.Key(ProviderId.StoLlm) == "sto-llm"
            && ProviderCatalog.Name(ProviderId.StoLlm).Length > 0
            && PanelThemes.AppKey(ProviderId.StoLlm) == "manual"
            && AppPresets.ThemeColor(ProviderId.StoLlm) == AppPresets.ManualColor);

        check("StoLlm group is always automatic (SSO)",
            ProviderCatalog.Group(ProviderId.StoLlm) == Loc.T("自动读取", "Read automatically"));

        var saved = JsonSerializer.Deserialize<DeckSettings>(
            JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.StoLlm }] }))!;
        check("StoLlm survives settings round-trip as sto-llm",
            saved.Apps.Single().QuotaSource == ProviderId.StoLlm
            && JsonSerializer.Serialize(saved).Contains("\"QuotaSource\":\"sto-llm\""));
    }

    private static async Task Fetch(Action<string, bool> check)
    {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var token = Jwt(now.ToUnixTimeSeconds() + 3600);
        var entry = new AppEntry { QuotaSource = ProviderId.StoLlm, SecretRevision = Guid.NewGuid() };
        var secrets = new MemorySecrets();
        secrets.Write(entry.InstanceId, StoLlmSession.Serialize(new StoLlmSession(token, "oid", "u", now)));

        using var handler = new StoHandler();
        using var http = new HttpClient(handler);
        handler.Respond = request =>
        {
            if (!request.Headers.TryGetValues("auth_token", out var auth) || auth.Single() != token)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"code":"0","msg":"ok","data":[{"tierName":"优享版","remainingQuota":10,"totalQuota":100,"usedQuota":90,"callTokenCount":1,"todayCallTokenCount":2,"quotaPeriod":"2026/11/01 - 2026/11/30","status":"ENABLE","userName":"u","userCode":"1","tenantName":"t","tenantCode":"c"}]}""",
                    Encoding.UTF8, "application/json")
            };
        };

        using var providers = new ConfiguredProviders(secrets, http, () => now);
        var test = await providers.TestAsync(entry);
        check("ConfiguredProviders fetches StoLlm with SSO session",
            test.Snapshot.LiveQuota && test.Snapshot.Plan == "优享版" && test.Snapshot.Metrics[0].Amount == 10m);

        var empty = new AppEntry { QuotaSource = ProviderId.StoLlm };
        var missing = await providers.TestAsync(empty);
        check("Empty StoLlm without SSO session is not configured",
            !missing.Snapshot.LiveQuota
            && (missing.Snapshot.StatusLabel.Contains("未")
                || missing.Snapshot.StatusLabel.Contains("Not")
                || missing.Snapshot.StatusLabel.Contains("登录")));
    }

    private static async Task Sso(Action<string, bool> check)
    {
        check("LoginUrl encodes returnUrl and systemCode",
            StoSsoClient.LoginUrl("https://sso.example", "http://127.0.0.1:9/callback/")
                .Contains("systemCode=UI", StringComparison.Ordinal)
            && StoSsoClient.LoginUrl("https://sso.example", "http://127.0.0.1:9/callback/")
                .Contains(Uri.EscapeDataString("http://127.0.0.1:9/callback/"), StringComparison.Ordinal));

        var now = DateTimeOffset.Parse("2026-11-09T08:00:00Z");
        var token = Jwt(now.ToUnixTimeSeconds() + 3600);
        using var handler = new StoHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://sso.example/") };
        handler.Respond = request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("LogOnStoTokenByAuthorizationCode", StringComparison.OrdinalIgnoreCase))
            {
                var body = JsonSerializer.Serialize(new
                {
                    Status = true,
                    StatusCode = "OK",
                    StatusMessage = "成功",
                    StoToken = token,
                    UserInfo = new { OpenId = "oid-9", RealName = "李四", UserName = "u1" }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
            if (request.RequestUri.AbsolutePath.Contains("signOut", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"Status":true,"StatusCode":"200","StatusMessage":"成功"}""", Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var client = new StoSsoClient(http, "https://sso.example");
        var session = await client.ExchangeAsync("auth-code-1", now);
        check("ExchangeAsync maps StoToken and OpenId",
            session.StoToken == token && session.OpenId == "oid-9" && session.DisplayName == "李四");

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Status":false,"StatusCode":"9","StatusMessage":"code无效"}""", Encoding.UTF8, "application/json")
        };
        try
        {
            await client.ExchangeAsync("bad", now);
            check("ExchangeAsync throws on Status false", false);
        }
        catch (InvalidDataException ex)
        {
            check("ExchangeAsync throws on Status false", ex.Message.Contains("code无效") || ex.Message.Length > 0);
        }

        bool signOutOk = false;
        handler.Respond = request =>
        {
            signOutOk = request.RequestUri!.ToString().Contains("signOut", StringComparison.OrdinalIgnoreCase)
                && request.RequestUri.Query.Contains("openId=oid-9", StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Status":true}""", Encoding.UTF8, "application/json")
            };
        };
        await client.SignOutAsync("oid-9");
        check("SignOut hits signOut with openId", signOutOk);
    }

    private static async Task Loopback(Action<string, bool> check)
    {
        // Prefer an ephemeral port in tests so a stuck BrimDeck on DefaultPort does not fail CI.
        int port = Random.Shared.Next(49152, 65535);
        using var gate = new StoSsoLoopback(port);
        var url = gate.CallbackUrl;
        check("CallbackUrl is loopback http",
            url == $"http://127.0.0.1:{port}/callback/");

        var wait = gate.WaitForCodeAsync(TimeSpan.FromSeconds(5));
        using var http = new HttpClient();
        var hit = await http.GetAsync(url + "?authorizationCode=abc%20123");
        check("Callback returns success HTML", hit.IsSuccessStatusCode
            && (await hit.Content.ReadAsStringAsync()).Contains("登录", StringComparison.Ordinal));
        var code = await wait;
        check("WaitForCodeAsync returns decoded code", code == "abc 123");

        using var bare = new StoSsoLoopback(Random.Shared.Next(49152, 65535));
        var bareWait = bare.WaitForCodeAsync(TimeSpan.FromSeconds(5));
        var bareHit = await http.GetAsync(bare.CallbackUrl);
        check("Bare callback is not success", bareHit.StatusCode == HttpStatusCode.BadRequest
            && (await bareHit.Content.ReadAsStringAsync()).Contains("authorizationCode", StringComparison.Ordinal));
        try
        {
            await bareWait;
            check("Bare callback throws without code", false);
        }
        catch (InvalidDataException ex)
        {
            check("Bare callback throws without code", ex.Message.Contains("authorizationCode", StringComparison.OrdinalIgnoreCase));
        }

        using var timed = new StoSsoLoopback(Random.Shared.Next(49152, 65535));
        try
        {
            await timed.WaitForCodeAsync(TimeSpan.FromMilliseconds(200));
            check("WaitForCodeAsync times out", false);
        }
        catch (TimeoutException)
        {
            check("WaitForCodeAsync times out", true);
        }
    }

    private sealed class MemorySecrets : IProviderSecrets
    {
        private readonly Dictionary<Guid, string> _keys = [];
        public string Read(Guid id) => _keys.GetValueOrDefault(id, "");
        public void Write(Guid id, string key) => _keys[id] = key;
        public void Delete(Guid id) => _keys.Remove(id);
    }

    private sealed class StoHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond!(request));
    }
}
