# 申通大模型配额 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 BrimDeck 配额来源中新增「申通大模型」，通过 sto-watt DevOps token 链（或可选粘贴 JWT）拉取 `llmKeysByUser` 并展示额度与调用次数。

**Architecture:** `ProviderId.StoLlm` 走 `ConfiguredProviders`（非 BuiltIn）。`ScriptSession` 像 `KimiApi` / `OpenCodeGo` 一样走 C# 专用路径：`StoLlmAuth` 解析 token，`StoLlmUsage` 请求网关并映射为 `ProviderResult`。不写回 `.env`，不做扫码。

**Tech Stack:** C# / .NET、`HttpClient`、现有 `IProviderSecrets`（DPAPI）、`Loc`、控制台测试 runner（`tests/BrimDeck.Tests`）。

**Spec:** `docs/superpowers/specs/2026-11-06-sto-llm-quota-design.md`

---

## File map

| File | Role |
| --- | --- |
| `src/BrimDeck.Core/StoLlmAuth.cs` | JWT 校验、读 `.env`、dms-proxy bridge、`ResolveToken`、自动分组探测 |
| `src/BrimDeck.Core/StoLlmUsage.cs` | 调 `llmKeysByUser`，映射 metrics / plan / details |
| `src/BrimDeck.Core/Settings.cs` | `ProviderId.StoLlm = 21`；`AppPresets.ThemeColor` 归入 ManualColor |
| `src/BrimDeck.Core/ProviderCatalog.cs` | Sources / Name / Key / Group / QuotaDescription |
| `src/BrimDeck.Core/PanelThemes.cs` | `AppKey(StoLlm) => "manual"` |
| `src/BrimDeck.Core/ScriptProvider.cs` | `RunStoLlmAsync`；允许空 secret；入口分支 |
| `src/BrimDeck/SettingsProviders.cs` | 可选 DevOps Token 空提示文案 |
| `docs/zh-CN/ai-tools.md`、`docs/en-US/ai-tools.md` | 工具表增加一行 |
| `tests/BrimDeck.Tests/StoLlmTests.cs` | Auth + 映射 + catalog 单测 |
| `tests/BrimDeck.Tests/Program.cs` | 注册套件 |

---

### Task 1: `StoLlmAuth` + 失败单测

**Files:**
- Create: `src/BrimDeck.Core/StoLlmAuth.cs`
- Create: `tests/BrimDeck.Tests/StoLlmTests.cs`
- Modify: `tests/BrimDeck.Tests/Program.cs`

- [ ] **Step 1: 写失败测试（Auth / JWT / env）**

创建 `tests/BrimDeck.Tests/StoLlmTests.cs`：

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class StoLlmTests
{
    public static async Task Run(string root, Action<string, bool> check)
    {
        Auth(root, check);
        Mapping(check);
        Catalog(root, check);
        await Fetch(root, check);
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

    private static void Auth(string root, Action<string, bool> check)
    {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        check("JWT with exp more than 60s ahead is valid",
            StoLlmAuth.IsJwtValid(Jwt(now.ToUnixTimeSeconds() + 120), now));
        check("JWT within 60s of expiry is invalid",
            !StoLlmAuth.IsJwtValid(Jwt(now.ToUnixTimeSeconds() + 30), now));
        check("Malformed JWT is invalid",
            !StoLlmAuth.IsJwtValid("not.a.jwt", now) && !StoLlmAuth.IsJwtValid("", now));

        var home = Path.Combine(root, "sto-watt-home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, ".env"), "# c\nDEVOPS_STOTOKEN=" + Jwt(now.ToUnixTimeSeconds() + 3600) + "\nOTHER=1\n");
        check("Reads DEVOPS_STOTOKEN from sto-watt .env",
            StoLlmAuth.ReadEnvToken(home, now) is { Length: > 10 });
        File.WriteAllText(Path.Combine(home, ".env"), "DEVOPS_STOTOKEN=" + Jwt(now.ToUnixTimeSeconds() - 10) + "\n");
        check("Expired env token is ignored", StoLlmAuth.ReadEnvToken(home, now) is null);

        check("Manual secret wins over env and bridge",
            StoLlmAuth.ResolveToken("  pasted-token  ", home, _ => Task.FromResult<string?>(null), now).Result == "pasted-token");
        check("Env is used when manual secret is empty",
            File.WriteAllText(Path.Combine(home, ".env"), "DEVOPS_STOTOKEN=" + Jwt(now.ToUnixTimeSeconds() + 3600) + "\n") is var _ ||
            StoLlmAuth.ResolveToken("", home, _ => Task.FromResult<string?>("bridge"), now).Result == StoLlmAuth.ReadEnvToken(home, now));
        check("Bridge is used when env missing",
            StoLlmAuth.ResolveToken("", Path.Combine(root, "missing-home"), _ => Task.FromResult<string?>(" from-bridge "), now).Result == "from-bridge");
        check("No sources yields null",
            StoLlmAuth.ResolveToken("", Path.Combine(root, "missing-home"), _ => Task.FromResult<string?>(null), now).Result is null);

        check("Auto available when .sto-watt folder exists",
            StoLlmAuth.IsAutoAvailable(home, () => false));
        check("Auto available when probe says proxy is up",
            StoLlmAuth.IsAutoAvailable(Path.Combine(root, "no-watt"), () => true));
        check("Auto unavailable when neither present",
            !StoLlmAuth.IsAutoAvailable(Path.Combine(root, "no-watt"), () => false));
    }

    private static void Mapping(Action<string, bool> check) { /* Task 2 fills */ }
    private static void Catalog(string root, Action<string, bool> check) { /* Task 3 fills */ }
    private static async Task Fetch(string root, Action<string, bool> check) { await Task.CompletedTask; /* Task 4 fills */ }
}
```

在 `tests/BrimDeck.Tests/Program.cs` 的 `ConfiguredProviderTests` 附近加入：

```csharp
await StoLlmTests.Run(root, Check);
```

- [ ] **Step 2: 跑测确认失败**

```powershell
dotnet test tests/BrimDeck.Tests/BrimDeck.Tests.csproj --filter "FullyQualifiedName~StoLlm" 2>&1
```

若项目是自定义 `Program` runner 而非 xUnit，则：

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj
```

Expected: 编译失败（缺少 `StoLlmAuth`）或检查项失败。

- [ ] **Step 3: 实现 `StoLlmAuth`**

创建 `src/BrimDeck.Core/StoLlmAuth.cs`：

```csharp
using System.Net.Http.Headers;
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

    // bridge: injectable for tests; production passes port => FetchBridgeTokenAsync(http, port).
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
```

注意：文件顶部补 `using System.Net;`（`IPAddress`）。`ResolveToken` 测试里第二段 `File.WriteAllText ... is var _ ||` 写法别扭——实现后把该 check 改成两行清晰断言（先写 env，再 `ResolveToken` 比较等于 `ReadEnvToken`）。

- [ ] **Step 4: 跑 Auth 相关检查至通过**

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj
```

Expected: `StoLlmTests` 中 Auth 段全部通过；Mapping/Catalog/Fetch 可先空通过或跳过。

- [ ] **Step 5: Commit**

```powershell
git add src/BrimDeck.Core/StoLlmAuth.cs tests/BrimDeck.Tests/StoLlmTests.cs tests/BrimDeck.Tests/Program.cs
git commit -m "feat(sto-llm): add DevOps token resolver"
```

---

### Task 2: `StoLlmUsage` 映射

**Files:**
- Create: `src/BrimDeck.Core/StoLlmUsage.cs`
- Modify: `tests/BrimDeck.Tests/StoLlmTests.cs`

- [ ] **Step 1: 补 Mapping 失败测试**

在 `StoLlmTests.Mapping`：

```csharp
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
```

- [ ] **Step 2: 跑测确认 Mapping 失败**

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj
```

Expected: 缺少 `StoLlmUsage` 或断言失败。

- [ ] **Step 3: 实现 `StoLlmUsage`**

```csharp
using System.Globalization;
using System.Text.Json;

namespace BrimDeck.Core;

public static class StoLlmUsage
{
    public const string ApiUrl = "https://devops-llmgatewayapi.sto.cn/api/llmgateway/user/llmKeysByUser";
    public const string PortalOrigin = "https://devops-new.sto.cn";
    public static string RequestBody { get; } = """{"pageIndex":1,"pageSize":1,"requestModel":{}}""";

    public static ProviderResult ParseResponse(JsonElement root)
    {
        var code = root.TryGetProperty("code", out var c) ? c.ToString() : "";
        if (code != "0")
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
            throw new InvalidDataException(string.IsNullOrWhiteSpace(msg)
                ? Loc.T("申通大模型返回失败。", "STO LLM gateway returned an error.")
                : msg!);
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidDataException(Loc.T("未找到大模型令牌。", "No LLM key was returned."));
        return FromRow(data[0]);
    }

    public static ProviderResult FromRow(JsonElement row)
    {
        decimal Num(string name)
        {
            if (!row.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var n)) return 0;
            return n;
        }
        string Str(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

        var remaining = Num("remainingQuota");
        var total = Num("totalQuota");
        var used = row.TryGetProperty("usedQuota", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetDecimal(out var usedQuota)
            ? usedQuota : Num("spent");
        var monthCalls = Num("callTokenCount");
        var todayCalls = Num("todayCallTokenCount");

        var metrics = new List<UsageMetric>
        {
            new(MetricKind.Balance, "可用额度") { Amount = remaining, Total = total, Currency = "CNY", Id = "remaining" },
            new(MetricKind.Spend, "本月已用") { Amount = used, Currency = "CNY", Id = "used" },
            new(MetricKind.Text, "本月调用") { Value = ((long)monthCalls).ToString(CultureInfo.InvariantCulture), Suffix = Loc.T("次", "calls"), Id = "month-calls" },
            new(MetricKind.Text, "今日调用") { Value = ((long)todayCalls).ToString(CultureInfo.InvariantCulture), Suffix = Loc.T("次", "calls"), Id = "today-calls" },
        };
        var details = new Dictionary<string, string>();
        if (Str("quotaPeriod") is { Length: > 0 } period) details[Loc.T("配额周期", "Quota period")] = period;
        var user = string.Join(" ", new[] { Str("userName"), Str("userCode") is { Length: > 0 } code ? $"({code})" : "" }.Where(s => s.Length > 0));
        if (user.Length > 0) details[Loc.T("用户", "User")] = user;
        var tenant = string.Join("/", new[] { Str("tenantName"), Str("tenantCode") }.Where(s => s.Length > 0));
        if (tenant.Length > 0) details[Loc.T("租户", "Tenant")] = tenant;
        if (Str("status") is { Length: > 0 } status) details[Loc.T("状态", "Status")] = status;

        return new ProviderResult(metrics, Str("tierName"), "plan", details, [])
        {
            Source = ProviderCatalog.Name(ProviderId.StoLlm)
        };
    }

    public static Dictionary<string, string> RequestHeaders(string token) => new()
    {
        ["Accept"] = "application/json",
        ["Content-Type"] = "application/json",
        ["auth_token"] = token,
        ["stotoken"] = token,
        ["Origin"] = PortalOrigin,
        ["Referer"] = PortalOrigin + "/",
    };
}
```

注意：`FromRow` 引用 `ProviderId.StoLlm`——若枚举尚未添加，本 Task 可暂时 `Source = "STO LLM"`，Task 3 再改回 `ProviderCatalog.Name`。推荐 **先做 Task 3 的枚举一行**，再完成本 Task，避免临时字符串。

- [ ] **Step 4: 跑 Mapping 通过**

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj
```

- [ ] **Step 5: Commit**

```powershell
git add src/BrimDeck.Core/StoLlmUsage.cs tests/BrimDeck.Tests/StoLlmTests.cs
git commit -m "feat(sto-llm): map llmKeysByUser to quota metrics"
```

---

### Task 3: Catalog / 枚举 / 主题色 / 分组

**Files:**
- Modify: `src/BrimDeck.Core/Settings.cs`（`ProviderId`、`AppPresets.ThemeColor`）
- Modify: `src/BrimDeck.Core/ProviderCatalog.cs`
- Modify: `src/BrimDeck.Core/PanelThemes.cs`
- Modify: `tests/BrimDeck.Tests/StoLlmTests.cs`

- [ ] **Step 1: Catalog 测试**

```csharp
private static void Catalog(string root, Action<string, bool> check)
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

    var home = Path.Combine(root, "watt-for-group");
    Directory.CreateDirectory(home);
    check("Group is automatic when sto-watt home exists",
        ProviderCatalog.Group(ProviderId.StoLlm, home, () => false) == Loc.T("自动读取", "Read automatically"));
    check("Group is manual when neither watt nor proxy",
        ProviderCatalog.Group(ProviderId.StoLlm, Path.Combine(root, "no"), () => false) == Loc.T("手动填写", "Entered by hand"));

    var saved = JsonSerializer.Deserialize<DeckSettings>(
        JsonSerializer.Serialize(new DeckSettings { Apps = [new AppEntry { QuotaSource = ProviderId.StoLlm }] }))!;
    check("StoLlm survives settings round-trip as sto-llm",
        saved.Apps.Single().QuotaSource == ProviderId.StoLlm
        && JsonSerializer.Serialize(saved).Contains("\"QuotaSource\":\"sto-llm\""));
}
```

- [ ] **Step 2: 改枚举与 Catalog**

`Settings.cs`：

```csharp
public enum ProviderId { Claude, Codex, Antigravity, Cursor, GlmChina, NewApi, Sub2Api, Custom, ZCode = 10, GlmGlobal = 11, Dsh = 12, DeepSeek = 13, GrokBot = 14, Kimi = 15, KimiApi = 16, OpenCode = 17, OpenCodeGo = 18, OpenRouter = 19, Pi = 20, StoLlm = 21 }
```

`AppPresets.ThemeColor` 的 manual 分支加入 `ProviderId.StoLlm`。

`ProviderCatalog.cs`：

```csharp
public static readonly ProviderId[] Sources = [.. AutomaticSources, ProviderId.GlmChina, ProviderId.GlmGlobal, ProviderId.DeepSeek, ProviderId.KimiApi, ProviderId.OpenCodeGo, ProviderId.OpenRouter, ProviderId.NewApi, ProviderId.Sub2Api, ProviderId.StoLlm, ProviderId.Custom];

public static string Group(ProviderId id) => Group(id, null, null);
public static string Group(ProviderId id, string? stoWattHome, Func<bool>? proxyUp) =>
    id == ProviderId.StoLlm
        ? (StoLlmAuth.IsAutoAvailable(stoWattHome, proxyUp) ? Loc.T("自动读取", "Read automatically") : Loc.T("手动填写", "Entered by hand"))
        : IsBuiltIn(id) ? Loc.T("自动读取", "Read automatically") : Loc.T("手动填写", "Entered by hand");

// Name:
ProviderId.StoLlm => Loc.T("申通大模型", "STO LLM"),

// Key:
ProviderId.StoLlm => "sto-llm",

// QuotaDescription:
ProviderId.StoLlm => Loc.T(
    "读取申通大模型网关的月度配额与调用次数；优先使用本机 sto-watt / dms-proxy 的 DevOps 登录，也可粘贴 token。不含本机 Token 用量统计",
    "Monthly quota and call counts from the STO LLM gateway; uses sto-watt / dms-proxy DevOps sign-in, or a pasted token. No local token usage stats"),
```

`PanelThemes.AppKey`：在 manual 分支加入 `ProviderId.StoLlm`。

设置菜单若调用 `ProviderCatalog.Group(id)`，保持无参重载即可（内部用默认 home / probe）。

- [ ] **Step 3: 跑测通过并 Commit**

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj
git add src/BrimDeck.Core/Settings.cs src/BrimDeck.Core/ProviderCatalog.cs src/BrimDeck.Core/PanelThemes.cs tests/BrimDeck.Tests/StoLlmTests.cs
git commit -m "feat(sto-llm): register quota source in catalog"
```

---

### Task 4: `ScriptSession` + `ConfiguredProviders` 接线

**Files:**
- Modify: `src/BrimDeck.Core/ScriptProvider.cs`
- Modify: `tests/BrimDeck.Tests/StoLlmTests.cs`

- [ ] **Step 1: 端到端 Fetch 测试**

```csharp
private static async Task Fetch(string root, Action<string, bool> check)
{
    var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
    var token = "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{now.ToUnixTimeSeconds() + 3600}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".x";
    var entry = new AppEntry { QuotaSource = ProviderId.StoLlm, SecretRevision = Guid.NewGuid() };
    var secrets = new MemorySecrets(); // 与 ConfiguredProviderTests 同文件或同程序集已有的 MemorySecrets
    secrets.Write(entry.InstanceId, token);

    using var handler = new StoHandler();
    using var http = new HttpClient(handler);
    handler.Respond = request =>
    {
        if (request.RequestUri!.AbsolutePath.Contains("devops-token"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token":"bridge-only"}""", Encoding.UTF8, "application/json") };
        if (!request.Headers.TryGetValues("auth_token", out var auth) || auth.Single() != token)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":"0","msg":"ok","data":[{"tierName":"优享版","remainingQuota":10,"totalQuota":100,"usedQuota":90,"callTokenCount":1,"todayCallTokenCount":2,"quotaPeriod":"2026/11/01 - 2026/11/30","status":"ENABLE","userName":"u","userCode":"1","tenantName":"t","tenantCode":"c"}]}""", Encoding.UTF8, "application/json")
        };
    };

    using var providers = new ConfiguredProviders(secrets, http, () => now);
    var test = await providers.TestAsync(entry);
    check("ConfiguredProviders fetches StoLlm with pasted token",
        test.Snapshot.LiveQuota && test.Snapshot.Plan == "优享版" && test.Snapshot.Metrics[0].Amount == 10m);

    var empty = new AppEntry { QuotaSource = ProviderId.StoLlm };
    var home = Path.Combine(root, "watt-fetch"); Directory.CreateDirectory(home);
    // 空 secret + 无 env：应未配置，且不抛未处理异常
    var missing = await providers.TestAsync(empty);
    check("Empty StoLlm without sto-watt token is not configured",
        !missing.Snapshot.LiveQuota && (missing.Snapshot.StatusLabel.Contains("未") || missing.Snapshot.StatusLabel.Contains("Not") || missing.Snapshot.StatusLabel.Contains("配置") || missing.Snapshot.StatusLabel.Contains("登录")));
}

// 若 Handler 已在 ConfiguredProviderTests 为 internal，可复用；否则在 StoLlmTests 内嵌同构 Handler。
sealed class StoHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
    public List<HttpRequestMessage> Calls { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        return Task.FromResult(Respond!(request));
    }
}
```

说明：`MemorySecrets` / `Handler` 若是 `ConfiguredProviderTests` 的 `private`/`file-local`，把它们提成 `internal` 到共享位置，或在 `StoLlmTests` 复制最小实现（本计划允许复制最小 `MemorySecrets`）。

- [ ] **Step 2: 接线 `ScriptSession` + 空 secret**

在 `RunAsync` 顶部：

```csharp
if (entry.QuotaSource == ProviderId.StoLlm) return await RunStoLlmAsync();
```

新增：

```csharp
private async Task<ProviderResult> RunStoLlmAsync()
{
    string t(string zh, string en) => Loc.T(zh, en);
    var nowUtc = now.ToUniversalTime();
    async Task<string?> Bridge(int port) => await StoLlmAuth.FetchBridgeTokenAsync(http, port, cancellation);

    var token = await StoLlmAuth.ResolveToken(secret, StoLlmAuth.DefaultStoWattHome, Bridge, nowUtc);
    if (token is null)
        throw new ProviderReadException(
            t("未登录", "Not signed in"),
            t("请打开 https://devops-new.sto.cn/ 登录，或安装并启动 sto-watt，也可在设置中粘贴 DevOps Token。",
              "Open https://devops-new.sto.cn/ and sign in, install and start sto-watt, or paste a DevOps token in settings."));

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
    // Env 缓存过期：同刷新内只 bridge 再试一次（手动 token 不重试 bridge）
    if (response.Status == 401 && secret.Trim().Length == 0)
    {
        var bridged = await Bridge(StoLlmAuth.DefaultProxyPort);
        if (!string.IsNullOrWhiteSpace(bridged) && !string.Equals(bridged.Trim(), token, StringComparison.Ordinal))
        {
            _statuses.Clear();
            response = await Post(bridged.Trim());
        }
    }
    if (response.Status == 401)
        throw new ProviderReadException(t("登录过期", "Sign-in expired"),
            t("请重新打开 https://devops-new.sto.cn/ 登录，或更新设置中的 DevOps Token。",
              "Sign in again at https://devops-new.sto.cn/, or update the DevOps token in settings."));
    if (response.Status is < 200 or >= 300) throw new HttpRequestException("HTTP " + response.Status);
    using var doc = JsonDocument.Parse(response.Body);
    return StoLlmUsage.ParseResponse(doc.RootElement);
}
```

`ConfiguredProviders.FetchAsync` 中：

```csharp
if (secret.Length == 0 && entry.QuotaSource is not (ProviderId.Custom or ProviderId.StoLlm))
    throw new ProviderReadException(...);
```

- [ ] **Step 3: 跑全套 StoLlm + ConfiguredProvider 相关测试**

```powershell
dotnet run --project tests/BrimDeck.Tests/BrimDeck.Tests.csproj
```

Expected: StoLlm 全绿；既有 ConfiguredProvider 不回归。

- [ ] **Step 4: Commit**

```powershell
git add src/BrimDeck.Core/ScriptProvider.cs tests/BrimDeck.Tests/StoLlmTests.cs
git commit -m "feat(sto-llm): wire gateway fetch into ConfiguredProviders"
```

---

### Task 5: 设置 UI 文案 + 文档

**Files:**
- Modify: `src/BrimDeck/SettingsProviders.cs`
- Modify: `docs/zh-CN/ai-tools.md`
- Modify: `docs/en-US/ai-tools.md`

- [ ] **Step 1: 密钥空提示**

在 `emptyHint` 的 `switch` 增加：

```csharp
ProviderId.StoLlm => Loc.T("可选；留空则使用 sto-watt / dms-proxy 的 DevOps 登录", "Optional; leave empty to use sto-watt / dms-proxy DevOps sign-in"),
```

将密钥行标签在 `StoLlm` 时改为与 Custom 类似的可选语义（若现有对 Custom 有「API 密钥（可选）」分支，对 StoLlm 同样处理）：

```csharp
Line(entry.QuotaSource is ProviderId.Custom or ProviderId.StoLlm
    ? Loc.T("DevOps Token（可选）", "DevOps token (optional)")
    : Loc.T("API 密钥", "API key"), Field(inputs, keyHint, reveal));
```

注意：仅改 `StoLlm` / `Custom` 的标题；其它来源保持「API 密钥」。若当前 `Line(...)` 对 Custom 已是「API 密钥（可选）」，则 StoLlm 用「DevOps Token（可选）」，Custom 保持原样——用显式 `entry.QuotaSource switch`。

- [ ] **Step 2: 文档表增行**

`docs/zh-CN/ai-tools.md` 表格（手动来源附近）增加：

```markdown
| 申通大模型 | ✓ | | 本机已装 sto-watt 并登录 devops-new.sto.cn，或在设置中粘贴 DevOps Token |
```

`docs/en-US/ai-tools.md`：

```markdown
| STO LLM | ✓ | | sto-watt signed in to devops-new.sto.cn, or a DevOps token pasted in settings |
```

可在「手动填写的来源」或单独短节补一句：无本机 Token 统计，统计应用需另选。

- [ ] **Step 3: 手动冒烟（本机有 sto-watt 时）**

1. 启动 BrimDeck，设置 → 配额与统计 → 新增列 → 配额来源选「申通大模型」。
2. 不填 token，点测试 / 等刷新：应显示可用额度与套餐。
3. 断网或停 dms-proxy 且清掉有效 `DEVOPS_STOTOKEN`：应出现未登录指引。

- [ ] **Step 4: Commit**

```powershell
git add src/BrimDeck/SettingsProviders.cs docs/zh-CN/ai-tools.md docs/en-US/ai-tools.md
git commit -m "docs(sto-llm): settings hints and ai-tools row"
```

---

## Spec coverage（自检）

| Spec 项 | Task |
| --- | --- |
| 仅配额、不进 UsageSources | Task 3 |
| 手动 > env > bridge，不写 .env，无 QR | Task 1、4 |
| 401 时 env→bridge 重试一次 | Task 4 |
| Balance + Spend + 两次 Text | Task 2 |
| 不落盘 keyPlain | Task 2 |
| 分组自动/手动 | Task 1、3 |
| ConfiguredProviders 允许空 secret | Task 4 |
| 设置可选 token + 文档 | Task 5 |
| 单测不打内网 | Task 1–4（HttpMessageHandler） |

## 执行交接

计划已保存到 `docs/superpowers/plans/2026-11-06-sto-llm-quota.md`。两种执行方式：

1. **Subagent-Driven（推荐）** — 每任务新开子代理，任务间复审  
2. **Inline Execution** — 本会话按 executing-plans 连续做完

选哪一种？
