# 申通大模型 SSO 鉴权 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将「申通大模型」鉴权改为申通 SSO（系统浏览器 + localhost 回调换 StoToken），去掉 sto-watt / dms-proxy / 粘贴 Token；用量接口不变。

**Architecture:** Core 新增 `StoSsoClient`（Login URL / 换票 / 退出）与 `StoSsoLoopback`（HttpListener）。`StoLlmAuth` 只解析本地 DPAPI 载荷中的 JWT。设置页 StoLlm 展开行改为「登录 / 退出」。`RunStoLlmAsync` 只读本地 token，401 提示重登。

**Tech Stack:** C# / .NET 10、`HttpClient`、`HttpListener`、现有 `IProviderSecrets`（DPAPI）、`Loc`、`UI.OpenUrl`、控制台测试 runner（`tests/BrimDeck.Tests`）。

**Spec:** `docs/superpowers/specs/2026-11-09-sto-llm-sso-design.md`

---

## File map

| File | Role |
| --- | --- |
| `src/BrimDeck.Core/StoLlmSession.cs` | SSO 本地载荷 record + JSON 序列化/反序列化 |
| `src/BrimDeck.Core/StoLlmAuth.cs` | 重写：JWT 校验 + 从 secret 取 token；删除 env/bridge |
| `src/BrimDeck.Core/StoSsoClient.cs` | Login URL、换票、退出 |
| `src/BrimDeck.Core/StoSsoLoopback.cs` | 本机 HttpListener 收 authorizationCode |
| `src/BrimDeck.Core/ScriptProvider.cs` | `RunStoLlmAsync` 去掉 bridge；文案改 SSO |
| `src/BrimDeck.Core/ProviderCatalog.cs` | Group / QuotaDescription 去掉 sto-watt |
| `src/BrimDeck/SettingsProviders.cs` | StoLlm：登录/退出 UI，跳过密码框 |
| `docs/zh-CN/ai-tools.md`、`docs/en-US/ai-tools.md` | 准备条件改为设置内扫码登录 |
| `tests/BrimDeck.Tests/StoLlmTests.cs` | 改写 Auth / Catalog / Fetch；新增 SSO 换票与 loopback |

---

### Task 1: `StoLlmSession` + 重写 `StoLlmAuth`（TDD）

**Files:**
- Create: `src/BrimDeck.Core/StoLlmSession.cs`
- Modify: `src/BrimDeck.Core/StoLlmAuth.cs`（整文件替换）
- Modify: `tests/BrimDeck.Tests/StoLlmTests.cs`（`Auth` 方法）

- [ ] **Step 1: 改写失败/目标测试 `Auth`**

把 `StoLlmTests.Auth` 换成：

```csharp
private static void Auth(string root, Action<string, bool> check)
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
```

同时改 `Catalog` 里分组断言：

```csharp
check("StoLlm group is always automatic (SSO)",
    ProviderCatalog.Group(ProviderId.StoLlm) == Loc.T("自动读取", "Read automatically"));
```

删除对 `stoWattHome` / `IsAutoAvailable` 的断言。

改 `Fetch`：secret 写入 session JSON；断言文案含「登录」或 `Not signed`；删除 bridge 路径依赖。

```csharp
secrets.Write(entry.InstanceId, StoLlmSession.Serialize(new StoLlmSession(token, "oid", "u", now)));
// ...
check("Empty StoLlm without SSO session is not configured",
    !missing.Snapshot.LiveQuota
    && (missing.Snapshot.StatusLabel.Contains("未")
        || missing.Snapshot.StatusLabel.Contains("Not")
        || missing.Snapshot.StatusLabel.Contains("登录")));
```

- [ ] **Step 2: 跑测试确认 Auth 失败**

Run: `dotnet test tests/BrimDeck.Tests/BrimDeck.Tests.csproj --filter "FullyQualifiedName~Program" --no-restore 2>&1 | Select-Object -Last 40`

若项目无 filter，用仓库惯用命令：

Run: `dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj -- --sto-llm`

（以 `Program.cs` 实际入口为准；没有专用 flag 就跑全量并看 StoLlm 失败项。）

Expected: 编译失败或 Auth 断言失败（旧 API 仍在）。

- [ ] **Step 3: 实现 `StoLlmSession` + 新 `StoLlmAuth`**

`src/BrimDeck.Core/StoLlmSession.cs`：

```csharp
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
```

`src/BrimDeck.Core/StoLlmAuth.cs`（整文件）：

```csharp
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
```

- [ ] **Step 4: 临时让 Catalog/Fetch 仍能编译**

`ProviderCatalog.Group`：StoLlm 恒返回「自动读取」（下一步正式改文案也可以现在做）：

```csharp
public static string Group(ProviderId id, string? stoWattHome = null, Func<bool>? proxyUp = null) =>
    id == ProviderId.StoLlm || IsBuiltIn(id)
        ? Loc.T("自动读取", "Read automatically")
        : Loc.T("手动填写", "Entered by hand");
```

（删除对 `StoLlmAuth.IsAutoAvailable` 的调用；保留可选参数以免其它调用方立刻炸，或同步删掉多余参数——若只有测试传入，一并删掉重载参数。）

`ScriptProvider.RunStoLlmAsync` 临时改为：

```csharp
var token = StoLlmAuth.ResolveToken(secret, nowUtc);
```

并删掉 `Bridge` / 401 bridge 重试块（正式错误文案在 Task 4 再精修也可以本步一次改完，见 Task 4 代码）。

- [ ] **Step 5: 跑通 StoLlm 相关测试**

Run: `dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj`

Expected: StoLlm Auth / Catalog / Fetch 相关 check 通过。

- [ ] **Step 6: Commit**

```bash
git add src/BrimDeck.Core/StoLlmSession.cs src/BrimDeck.Core/StoLlmAuth.cs src/BrimDeck.Core/ProviderCatalog.cs src/BrimDeck.Core/ScriptProvider.cs tests/BrimDeck.Tests/StoLlmTests.cs
git commit -m "refactor(sto-llm): replace sto-watt token chain with local SSO session"
```

---

### Task 2: `StoSsoClient` 换票 / 退出（TDD）

**Files:**
- Create: `src/BrimDeck.Core/StoSsoClient.cs`
- Modify: `tests/BrimDeck.Tests/StoLlmTests.cs`（新增 `Sso` 方法并在 `Run` 中调用）

- [ ] **Step 1: 写 SSO 换票测试**

```csharp
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
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"Status":true,"StatusCode":"OK","StatusMessage":"成功","StoToken":"{{token}}","UserInfo":{"OpenId":"oid-9","RealName":"李四","UserName":"u1"}}""",
                    Encoding.UTF8, "application/json")
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

    handler.Respond = request =>
    {
        check("SignOut hits signOut with openId",
            request.RequestUri!.ToString().Contains("signOut", StringComparison.OrdinalIgnoreCase)
            && request.RequestUri.Query.Contains("openId=oid-9", StringComparison.Ordinal));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Status":true}""", Encoding.UTF8, "application/json")
        };
    };
    await client.SignOutAsync("oid-9");
}
```

在 `Run` 末尾：`await Sso(check);`

- [ ] **Step 2: 跑测试确认失败（缺类型）**

Expected: 编译失败 `StoSsoClient` 不存在。

- [ ] **Step 3: 实现 `StoSsoClient`**

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace BrimDeck.Core;

public sealed class StoSsoClient(HttpClient http, string? baseUrl = null)
{
    public const string DefaultBaseUrl = "https://baseinfo-usercenterserviceapi.sto.cn";
    public const string SystemCode = "UI";

    private readonly string _base = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

    public static string LoginUrl(string baseUrl, string returnUrl)
    {
        var root = baseUrl.TrimEnd('/');
        return root + "/Authenticate/Login?autoLogin=false&systemCode=" + Uri.EscapeDataString(SystemCode)
            + "&returnUrl=" + Uri.EscapeDataString(returnUrl);
    }

    public string LoginUrl(string returnUrl) => LoginUrl(_base, returnUrl);

    public async Task<StoLlmSession> ExchangeAsync(string authorizationCode, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var code = authorizationCode.Trim();
        if (code.Length == 0) throw new InvalidDataException(Loc.T("缺少授权码。", "Authorization code is missing."));

        using var response = await http.PostAsJsonAsync(
            _base + "/WebAPIV46/API/LogOn/LogOnStoTokenByAuthorizationCode",
            new
            {
                systemCode = SystemCode,
                categoryCode = SystemCode,
                authorizationCode = code,
                getRoles = false,
                getModules = false
            }, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        bool ok = root.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.True
            || root.TryGetProperty("StatusCode", out var sc) && sc.ValueKind == JsonValueKind.String
                && string.Equals(sc.GetString(), "OK", StringComparison.OrdinalIgnoreCase);
        var message = root.TryGetProperty("StatusMessage", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString() : null;
        if (!ok)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(message)
                ? Loc.T("单点登录换票失败。", "SSO token exchange failed.")
                : message!);
        var token = root.TryGetProperty("StoToken", out var st) ? st.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidDataException(Loc.T("单点登录未返回 StoToken。", "SSO did not return a StoToken."));
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
        var url = _base + "/webapiv46/api/logon/signOut?systemCode=" + Uri.EscapeDataString(SystemCode)
            + "&openId=" + Uri.EscapeDataString(id);
        try
        {
            using var response = await http.PostAsync(url, content: null, ct);
            // Best-effort: ignore non-success; local cache is cleared by the caller either way.
            _ = response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }
}
```

- [ ] **Step 4: 跑测试通过**

Expected: `Sso` 相关 check 通过。

- [ ] **Step 5: Commit**

```bash
git add src/BrimDeck.Core/StoSsoClient.cs tests/BrimDeck.Tests/StoLlmTests.cs
git commit -m "feat(sto-llm): add SSO client for StoToken exchange"
```

---

### Task 3: `StoSsoLoopback`（TDD）

**Files:**
- Create: `src/BrimDeck.Core/StoSsoLoopback.cs`
- Modify: `tests/BrimDeck.Tests/StoLlmTests.cs`（`Loopback`）

- [ ] **Step 1: 写 loopback 测试**

```csharp
private static async Task Loopback(Action<string, bool> check)
{
    using var gate = new StoSsoLoopback();
    var url = gate.CallbackUrl;
    check("CallbackUrl is loopback http",
        url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
        && url.Contains("/callback", StringComparison.OrdinalIgnoreCase));

    var wait = gate.WaitForCodeAsync(TimeSpan.FromSeconds(5));
    using var http = new HttpClient();
    var hit = await http.GetAsync(url.TrimEnd('/') + "/?authorizationCode=abc%20123");
    check("Callback returns success HTML", hit.IsSuccessStatusCode
        && (await hit.Content.ReadAsStringAsync()).Contains("登录", StringComparison.Ordinal));
    var code = await wait;
    check("WaitForCodeAsync returns decoded code", code == "abc 123");

    using var timed = new StoSsoLoopback();
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
```

- [ ] **Step 2: 实现 `StoSsoLoopback`**

```csharp
using System.Net;
using System.Text;

namespace BrimDeck.Core;

public sealed class StoSsoLoopback : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    public string CallbackUrl { get; }

    public StoSsoLoopback()
    {
        // Prefer an ephemeral port via :0 — HttpListener needs an explicit port; probe one.
        for (int i = 0; i < 20; i++)
        {
            int port = Random.Shared.Next(49152, 65535);
            var prefix = $"http://127.0.0.1:{port}/callback/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
                CallbackUrl = prefix;
                return;
            }
            catch (HttpListenerException) { }
        }
        throw new InvalidOperationException(Loc.T("无法启动本地登录回调。", "Could not start the local sign-in callback."));
    }

    public async Task<string> WaitForCodeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout);
        while (!linked.IsCancellationRequested)
        {
            var contextTask = _listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, linked.Token));
            if (completed != contextTask)
            {
                try { await contextTask; } catch { /* abandoned */ }
                throw new TimeoutException(Loc.T("登录超时，请重试。", "Sign-in timed out. Try again."));
            }
            var context = await contextTask;
            var code = context.Request.QueryString["authorizationCode"]
                ?? context.Request.QueryString["code"];
            var html = "<!doctype html><meta charset=utf-8><title>BrimDeck</title>"
                + "<p>登录成功，可以关闭此页面。</p><p>Signed in. You can close this window.</p>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, linked.Token);
            context.Response.Close();
            if (!string.IsNullOrWhiteSpace(code)) return code.Trim();
        }
        throw new OperationCanceledException(linked.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        _cts.Dispose();
    }
}
```

注意：若 `Task.Delay(Timeout.Infinite, canceledToken)` 在已取消时立刻抛，改用 `Task.WhenAny(contextTask, Task.Delay(timeout, linked.Token))` 更稳：

```csharp
var contextTask = _listener.GetContextAsync();
var delay = Task.Delay(timeout, linked.Token);
var done = await Task.WhenAny(contextTask, delay);
if (done != contextTask)
{
    throw new TimeoutException(...);
}
```

只收一次有效 code 即可（测例只打一次）。

- [ ] **Step 3: 跑测试通过并 Commit**

```bash
git add src/BrimDeck.Core/StoSsoLoopback.cs tests/BrimDeck.Tests/StoLlmTests.cs
git commit -m "feat(sto-llm): add localhost SSO callback listener"
```

---

### Task 4: `RunStoLlmAsync` 错误文案与 401（若 Task 1 未做完则本任务收尾）

**Files:**
- Modify: `src/BrimDeck.Core/ScriptProvider.cs`（`RunStoLlmAsync`）

- [ ] **Step 1: 替换 `RunStoLlmAsync` 为**

```csharp
private async Task<ProviderResult> RunStoLlmAsync()
{
    string t(string zh, string en) => Loc.T(zh, en);
    var nowUtc = now.ToUniversalTime();
    var token = StoLlmAuth.ResolveToken(secret, nowUtc);
    if (token is null)
        throw new ProviderReadException(
            t("未登录", "Not signed in"),
            t("请在设置中登录申通账号。", "Sign in with your STO account in Settings."));

    async Task<(int Status, string Body)> Post(string jwt)
    {
        var headers = StoLlmUsage.RequestHeaders(jwt);
        using var reply = JsonDocument.Parse(await SendAsync("POST", JsonSerializer.Serialize(new
        {
            url = StoLlmUsage.ApiUrl,
            options = new { headers, body = JsonDocument.Parse(StoLlmUsage.RequestBody).RootElement }
        })));
        if (reply.RootElement.Get("error").Text() is { Length: > 0 } error) throw new InvalidDataException(error);
        return ((int)(reply.RootElement.Get("status").Number() ?? 0), reply.RootElement.Get("body").Text());
    }

    var response = await Post(token);
    if (response.Status == 401)
        throw new ProviderReadException(t("登录过期", "Sign-in expired"),
            t("请在设置中重新登录申通账号。", "Sign in again with your STO account in Settings."));
    if (response.Status is < 200 or >= 300) throw new HttpRequestException("HTTP " + response.Status);
    using var doc = JsonDocument.Parse(response.Body);
    return StoLlmUsage.ParseResponse(doc.RootElement);
}
```

确认空 secret 仍允许进入 `RunStoLlmAsync`（现有 `ConfiguredProviders` 对 StoLlm 的特例保留）。

- [ ] **Step 2: 跑 StoLlmTests Fetch；Commit**

```bash
git add src/BrimDeck.Core/ScriptProvider.cs
git commit -m "fix(sto-llm): point quota errors at SSO sign-in"
```

---

### Task 5: Catalog 文案 + 用户文档

**Files:**
- Modify: `src/BrimDeck.Core/ProviderCatalog.cs`（`QuotaDescription`）
- Modify: `docs/zh-CN/ai-tools.md`
- Modify: `docs/en-US/ai-tools.md`

- [ ] **Step 1: 更新说明**

`QuotaDescription(StoLlm)`：

```csharp
ProviderId.StoLlm => Loc.T(
    "读取申通大模型网关的月度配额与调用次数；在设置中扫码登录申通账号。不含本机 Token 用量统计",
    "Monthly quota and call counts from the STO LLM gateway; sign in with your STO account in Settings. No local token usage stats"),
```

`docs/zh-CN/ai-tools.md` 行改为：`在设置中扫码登录申通账号`

`docs/en-US/ai-tools.md` 行改为：`Sign in with your STO account in Settings`

- [ ] **Step 2: Commit**

```bash
git add src/BrimDeck.Core/ProviderCatalog.cs docs/zh-CN/ai-tools.md docs/en-US/ai-tools.md
git commit -m "docs(sto-llm): describe SSO sign-in instead of sto-watt"
```

---

### Task 6: 设置页登录 / 退出 UI

**Files:**
- Modify: `src/BrimDeck/SettingsProviders.cs`（`SourceDetails`）

- [ ] **Step 1: StoLlm 跳过密码框**

在创建站点地址」块之后、创建创建 `password` PasswordBox 之前：

```csharp
if (entry.QuotaSource == ProviderId.StoLlm)
{
    StoLlmSignInDetails(entry, Line, InvalidateTest);
    // jump to test button: extract the shared test/result tail, or early-build test UI here then return after off-peak is attached by caller
}
else
{
    // existing password / visible / SaveSecret block unchanged
}
```

`SourceDetails` 结构较紧：最干净的做法是把「测试连接」一段留在后面共用；对 StoLlm 用 `goto` 式分支——**不要**创建 password 控件，定义空的 `SaveSecret`：

```csharp
Action SaveSecret = () => { };
if (entry.QuotaSource == ProviderId.StoLlm)
{
    StoLlmSignInDetails(entry, Line, () => { result.Visibility = Visibility.Collapsed; _sourceTestCancellation?.Cancel(); });
}
else
{
    // ... existing password UI; SaveSecret = real saver ...
}
```

确保后面 `test.Click` 里的 `SaveSecret()` 对 StoLlm 为 no-op。

- [ ] **Step 2: 实现 `StoLlmSignInDetails` 私有方法**

放在 `SettingsWindow` partial（同文件）中：

```csharp
private void StoLlmSignInDetails(AppEntry entry, Func<string, FrameworkElement, int, TextBlock?> line, Action invalidate)
{
    // Adapt Line signature to whatever SourceDetails uses (default span). Prefer a local helper that matches Line's overload.
    var status = TextLine("", 12.5, TextPrimary);
    var action = new Button { Style = (Style)FindResource("SettingsSecondaryButton"), Height = 30, Padding = new Thickness(12, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left };
    AutomationProperties.SetAutomationId(action, "sto-llm-auth-" + entry.InstanceId.ToString("N"));

    void Refresh()
    {
        var current = S.Entry(entry.InstanceId);
        bool signedIn = current?.SecretRevision != Guid.Empty;
        string display = "";
        if (signedIn)
        {
            try
            {
                var raw = _app.Secrets.Read(entry.InstanceId);
                display = StoLlmSession.TryRead(raw)?.DisplayName ?? "";
            }
            catch { /* ignore */ }
        }
        status.Text = signedIn
            ? (display.Length > 0 ? Loc.T("已登录：" + display, "Signed in: " + display) : Loc.T("已登录", "Signed in"))
            : Loc.T("扫码登录后读取大模型配额", "Scan to sign in and read LLM quota");
        action.Content = signedIn ? Loc.T("退出登录", "Sign out") : Loc.T("登录申通账号", "Sign in with STO");
        AutomationProperties.SetName(action, (string)action.Content);
    }

    StoSsoLoopback? active = null;
    action.Click += async (_, _) =>
    {
        var current = S.Entry(entry.InstanceId);
        if (current is null) return;
        if (current.SecretRevision != Guid.Empty)
        {
            string openId = "";
            try { openId = StoLlmSession.TryRead(_app.Secrets.Read(entry.InstanceId))?.OpenId ?? ""; }
            catch { }
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                await new StoSsoClient(http).SignOutAsync(openId);
            }
            catch { }
            _app.Secrets.Delete(entry.InstanceId);
            Change(s => s.Entry(entry.InstanceId)!.SecretRevision = Guid.Empty);
            Refresh();
            invalidate();
            return;
        }

        active?.Dispose();
        try
        {
            active = new StoSsoLoopback();
            action.IsEnabled = false;
            action.Content = Loc.T("等待扫码…", "Waiting for scan…");
            var login = StoSsoClient.LoginUrl(StoSsoClient.DefaultBaseUrl, active.CallbackUrl);
            UI.OpenUrl(login);
            var code = await active.WaitForCodeAsync(TimeSpan.FromMinutes(3));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var session = await new StoSsoClient(http).ExchangeAsync(code);
            _app.Secrets.Write(entry.InstanceId, StoLlmSession.Serialize(session));
            Change(s => s.Entry(entry.InstanceId)!.SecretRevision = Guid.NewGuid());
            SaveStatus(Loc.T("申通账号已登录", "Signed in to STO"), false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidDataException or HttpRequestException or HttpListenerException or OperationCanceledException or IOException or CryptographicException)
        {
            SaveStatus(ex.Message, true);
        }
        finally
        {
            active?.Dispose();
            active = null;
            action.IsEnabled = true;
            Refresh();
            invalidate();
        }
    };

    Refresh();
    line(Loc.T("申通账号", "STO account"), status, 5);
    line("", action, 5);
}
```

按 `SourceDetails` 里 `Line` 的实际委托签名微调（若 `Line` 是本地函数且 span 有默认值，传入时保持一致）。需要 `using System.Net;`、`System.Security.Cryptography`。

按钮样式：若无 `SettingsSecondaryButton`，改用现有测试按钮同款 `Style`（搜 `测试连接` 按钮创建处复用）。

- [ ] **Step 3: 手工检查清单（实现者勾选）**

1. 设置 → 添加/展开申通大模型 → 只有「登录」无 Token 框。  
2. 点击登录 → 浏览器打开 SSO；本机回调后显示「已登录：…」。  
3. 「测试连接」成功出配额。  
4. 退出后配额变为未登录；再测失败文案指向设置登录。

- [ ] **Step 4: Commit**

```bash
git add src/BrimDeck/SettingsProviders.cs
git commit -m "feat(sto-llm): settings sign-in and sign-out via SSO"
```

---

### Task 7: 清扫与全量测试

**Files:**
- Grep 清理残留：`sto-watt`、`DEVOPS_STOTOKEN`、`FetchBridgeToken`、`IsAutoAvailable`、`DefaultProxyPort`、`ReadEnvToken`
- Modify: 任何仍引用旧 API 的测试 / 文档

- [ ] **Step 1: Grep 并删除残留**

Run: `rg -n "sto-watt|DEVOPS_STOTOKEN|FetchBridgeToken|IsAutoAvailable|DefaultStoWattHome|ReadEnvToken|DevOps Token（可选）" src tests docs`

按结果删改；`2026-11-06` 旧设计/计划可保留作历史，不必改。

- [ ] **Step 2: 全量测试**

```bash
dotnet test BrimDeck.sln -c Debug
```

Expected: 全绿（或仅与本改动无关的既有失败——若有，记下不在本计划范围）。

- [ ] **Step 3: 最终 Commit（若还有清扫）**

```bash
git add -u
git commit -m "chore(sto-llm): remove leftover sto-watt auth references"
```

---

## Spec coverage check

| Spec 项 | Task |
| --- | --- |
| SSO 换票 API / systemCode=UI | Task 2 |
| localhost 回调 | Task 3 + Task 6 |
| DPAPI session JSON | Task 1 + Task 6 |
| 删除 sto-watt / 粘贴 | Task 1, 4, 5, 6, 7 |
| 用量 llmKeysByUser 不变 | Task 4（仅鉴权） |
| 退出 signOut + 清本地 | Task 2 + Task 6 |
| 文档 ai-tools | Task 5 |
| 单测不访问真实 SSO | Task 2–3 fixtures / loopback |

## Self-review notes

- 无 TBD；`Line` 委托签名在 Task 6 需对照 `SourceDetails` 实地微调（计划已说明）。
- `HttpListener` 在部分环境需 URL ACL；仅绑 `127.0.0.1` 一般不需管理员。若 CI 无 http 前缀权限，Task 3 测试仅在 Windows 跑（与现有 Windows 测试策略一致）；`BrimDeck.Tests` 若在非 Windows CI 跑，给 Loopback 测试加 `OperatingSystem.IsWindows()` 守卫并 `check(..., true)` skip。
