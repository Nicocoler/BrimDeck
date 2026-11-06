# STO LLM Gateway Quota Design

Add the company’s internal LLM gateway (`devops-llmgatewayapi.sto.cn`) as a first-class **quota source** in BrimDeck, reusing sto-watt’s DevOps token chain for auth.

## Goals

- Show monthly quota (remaining / used / total), plan name, period, and call counts from `llmKeysByUser`.
- Prefer zero-config auth when sto-watt / dms-proxy is already set up; allow an optional pasted DevOps JWT.
- Fit existing quota UI (`UsageMetric`, `ConfiguredProviders`, settings expand row) without inventing a parallel panel.

## Non-goals (v1)

- Today / 7-day Token usage statistics (`UsageSources`)
- Model price tables / `modelDetails`
- Toggling rate-limit / fallback switches on the gateway
- Displaying or persisting `llmKey`, `keyPlain`, or plaintext API keys from the response
- Writing back to `~/.sto-watt/.env`
- QR / password login flows inside BrimDeck
- Calling sto-watt CLI or Python skills

## Product decisions (locked)

| Topic | Choice |
| --- | --- |
| Scope | Quota source only |
| Auth | Manual DPAPI secret **or** sto-watt chain (prefer secret when set) |
| Menu | Always listed; `Group` = “自动读取” when sto-watt home or dms-proxy is detected, else “手动填写” |
| Metrics | Page 1: remaining Balance + monthly Spend; page 2: month / today call counts as Text |
| Approach | Built-in `ProviderId` + C# client (not Custom JS, not shelling out) |

## Architecture

```text
AppEntry (QuotaSource=StoLlm)
        │
        ▼
ConfiguredProviders.FetchAsync
        │  secret = IProviderSecrets (optional)
        ▼
StoLlmAuth.ResolveToken(secret)
   ① manual secret
   ② ~/.sto-watt/.env DEVOPS_STOTOKEN (JWT exp > 60s)
   ③ POST http://127.0.0.1:8765/api/devops-token
        │
        ▼
StoLlmUsage.FetchAsync(token)
   POST .../api/llmgateway/user/llmKeysByUser
        │
        ▼
ProviderResult → ProviderSnapshot (plan, details, metrics)
```

### Provider identity

- New enum member: `ProviderId.StoLlm = 21` (append; do not renumber).
- Catalog key: `sto-llm`; display name:「申通大模型」/ `STO LLM`.
- In `Sources`; **not** in `BuiltIns`, `AutomaticSources`, or `UsageSources`.
- `IsBuiltIn(StoLlm) == false` so refresh goes through `ConfiguredProviders` (test button, last-good cache, 10s deadline).
- `ConfiguredProviders` special-case: empty secret is allowed for `StoLlm` (unlike other non-Custom sources).

### Auth (`StoLlmAuth`)

Pure/resolvable helpers, injectable paths and HTTP for tests.

1. Trimmed manual secret if non-empty.
2. Read `%USERPROFILE%\.sto-watt\.env` key `DEVOPS_STOTOKEN`; validate JWT `exp` with 60s margin (same rule as sto-watt `token_cache.is_jwt_valid`).
3. `POST http://127.0.0.1:{port}/api/devops-token` (default port `8765`) with `X-Sto-Agent: 1`, empty JSON body; take `token` from JSON.
4. Otherwise fail with a clear “未登录 / 未配置” label.

On HTTP **401** (or gateway auth-shaped failure) when the token came from env cache: **one** in-refresh retry via bridge only; still do not write `.env`. No `QR_STOTOKEN` fallback.

### HTTP (`StoLlmUsage`)

- URL: `https://devops-llmgatewayapi.sto.cn/api/llmgateway/user/llmKeysByUser`
- Method: POST  
- Headers: `Accept: application/json`, `Content-Type: application/json`, `auth_token` + `stotoken` (same JWT), `Origin` / `Referer` = `https://devops-new.sto.cn/`
- Body: `{"pageIndex":1,"pageSize":1,"requestModel":{}}`
- Success: top-level `code == "0"` and `data` array with at least one object; use first row.
- Never log or store `keyPlain` / full `llmKey`.

### Metric mapping

| Order | Kind | Label | Mapping |
| --- | --- | --- | --- |
| 1 | Balance | 可用额度 | `amount = remainingQuota`, `total = totalQuota`, `currency = CNY` |
| 2 | Spend | 本月已用 | `amount = usedQuota` (fallback `spent`), `currency = CNY` |
| 3 | Text | 本月调用 | `value` = `callTokenCount`, `suffix` = 次 |
| 4 | Text | 今日调用 | `value` = `todayCallTokenCount`, `suffix` = 次 |

- `plan` = `tierName`
- `details`: 配额周期 (`quotaPeriod`), 用户 (`userName`/`userCode`), 租户 (`tenantName`/`tenantCode`), 状态 (`status`)
- `Source` = catalog name (optionally append tenant short name if useful)

Call counts use **Text** so we do not invent a fake `total` for Count metrics.

### Settings UI

- Expand row when `QuotaSource == StoLlm`: optional DevOps Token field (same DPAPI / `SecretRevision` pattern as other keys).
- Empty hint: explain sto-watt / browser login / optional paste.
- Quota description in catalog: quota comes from STO LLM Gateway; needs DevOps login via sto-watt or a pasted token; no local usage stats.

### Menu grouping

`ProviderCatalog.Group(StoLlm)`:

- “自动读取” if `%USERPROFILE%\.sto-watt` exists **or** a cheap probe indicates dms-proxy is up (TCP connect or cached successful token resolve this process); else “手动填写”.
- Source remains selectable either way.

### Error / stale behavior

Reuse `ConfiguredProviders` last-good + status labels:

| Case | StatusLabel | Guidance |
| --- | --- | --- |
| No token | 未配置 / 未登录 | Open `https://devops-new.sto.cn/`, or install/start sto-watt, or paste token |
| 401 after retry | 密钥无效 / 登录过期 | Re-login in browser; update pasted token if any |
| `code != "0"` | 读取失败 | Show gateway `msg` (redacted/truncated) |
| Timeout / network | 读取超时 / 读取失败 | Keep last good if any |

## Tests

- `StoLlmAuth`: JWT valid/expired/malformed; env parse; resolve order (manual > env > bridge); 401 retry path with fakes.
- `StoLlmUsage`: fixture JSON → four metrics + plan/details; empty `data`; non-zero `code`; ignore sensitive fields.
- `ConfiguredProviders` / catalog: empty secret allowed for StoLlm; not a usage source; grouping helper with fake filesystem/probe.

No live calls to STO intranet in CI.

## Docs

- Add one row to `docs/zh-CN/ai-tools.md` (and English counterpart if present): quota ✓, usage blank, prep = sto-watt DevOps login or pasted token.

## Security notes

- Do not commit real JWTs from curl samples.
- Manual token stored only via existing `IProviderSecrets` (DPAPI).
- Response secrets (`keyPlain`) never written to disk or UI.
