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
            var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
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
        var userParts = new List<string>();
        if (Str("userName") is { Length: > 0 } name) userParts.Add(name);
        if (Str("userCode") is { Length: > 0 } code) userParts.Add($"({code})");
        if (userParts.Count > 0) details[Loc.T("用户", "User")] = string.Join(" ", userParts);
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
        ["auth_token"] = token,
        ["stotoken"] = token,
        ["Origin"] = PortalOrigin,
        ["Referer"] = PortalOrigin + "/",
    };
}
