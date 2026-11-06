# 申通大模型网关配额设计

将公司内部大模型网关（`devops-llmgatewayapi.sto.cn`）作为 BrimDeck 的一等公民**配额来源**，鉴权复用 sto-watt 的 DevOps token 链。

## 目标

- 展示月度配额（剩余 / 已用 / 总额）、套餐名、周期，以及 `llmKeysByUser` 返回的调用次数。
- 本机已配置 sto-watt / dms-proxy 时尽量零配置；同时允许粘贴 DevOps JWT 作为可选兜底。
- 接入现有配额 UI（`UsageMetric`、`ConfiguredProviders`、设置展开行），不另起一套面板。

## 非目标（v1）

- 今日 / 七日 Token 用量统计（`UsageSources`）
- 模型价目表 / `modelDetails`
- 在网关上切换限流 / 降级开关
- 展示或持久化响应中的 `llmKey`、`keyPlain` 或明文 API Key
- 回写 `~/.sto-watt/.env`
- 在 BrimDeck 内做扫码 / 密码登录
- 调用 sto-watt CLI 或 Python skill

## 产品决策（已锁定）

| 议题 | 选择 |
| --- | --- |
| 范围 | 仅配额来源 |
| 鉴权 | 手动 DPAPI 密钥 **或** sto-watt 链（填了密钥则优先用密钥） |
| 菜单 | 始终出现在列表；检测到 sto-watt 目录或 dms-proxy 时 `Group` =「自动读取」，否则「手动填写」 |
| 指标 | 第 1 页：剩余 Balance + 本月 Spend；第 2 页：本月 / 今日调用次数（Text） |
| 实现 | 内置 `ProviderId` + C# 客户端（不用 Custom JS，不 shell 外部进程） |

## 架构

```text
AppEntry (QuotaSource=StoLlm)
        │
        ▼
ConfiguredProviders.FetchAsync
        │  secret = IProviderSecrets（可选）
        ▼
StoLlmAuth.ResolveToken(secret)
   ① 手动密钥
   ② ~/.sto-watt/.env 的 DEVOPS_STOTOKEN（JWT exp > 60s）
   ③ POST http://127.0.0.1:8765/api/devops-token
        │
        ▼
StoLlmUsage.FetchAsync(token)
   POST .../api/llmgateway/user/llmKeysByUser
        │
        ▼
ProviderResult → ProviderSnapshot（plan、details、metrics）
```

### 提供方身份

- 新增枚举：`ProviderId.StoLlm = 21`（追加，不改已有编号）。
- Catalog key：`sto-llm`；显示名：「申通大模型」/ `STO LLM`。
- 放入 `Sources`；**不**放入 `BuiltIns`、`AutomaticSources`、`UsageSources`。
- `IsBuiltIn(StoLlm) == false`，刷新走 `ConfiguredProviders`（可测连接、last-good 缓存、约 10s 超时）。
- `ConfiguredProviders` 特例：`StoLlm` 允许 secret 为空（与其它非 Custom 来源不同）。

### 鉴权（`StoLlmAuth`）

可注入路径与 HTTP 的纯解析辅助，便于单测。

1. 手动密钥非空则 trim 后使用。
2. 读取 `%USERPROFILE%\.sto-watt\.env` 的 `DEVOPS_STOTOKEN`；按 JWT `exp` 校验，留 60s 余量（与 sto-watt `token_cache.is_jwt_valid` 一致）。
3. `POST http://127.0.0.1:{port}/api/devops-token`（默认端口 `8765`），头 `X-Sto-Agent: 1`，空 JSON body；取响应 JSON 的 `token`。
4. 仍无则失败，状态为「未登录 / 未配置」。

当 token 来自 env 缓存且遇到 HTTP **401**（或网关鉴权类失败）时：同一次刷新内**仅**再试一次 bridge；仍不写 `.env`。不做 `QR_STOTOKEN` 扫码兜底。

### HTTP（`StoLlmUsage`）

- URL：`https://devops-llmgatewayapi.sto.cn/api/llmgateway/user/llmKeysByUser`
- 方法：POST
- Headers：`Accept: application/json`、`Content-Type: application/json`、`auth_token` + `stotoken`（同一 JWT）、`Origin` / `Referer` = `https://devops-new.sto.cn/`
- Body：`{"pageIndex":1,"pageSize":1,"requestModel":{}}`
- 成功：顶层 `code == "0"` 且 `data` 至少一项；取第一项。
- 永不记录或保存 `keyPlain` / 完整 `llmKey`。

### 指标映射

| 顺序 | Kind | 标签 | 映射 |
| --- | --- | --- | --- |
| 1 | Balance | 可用额度 | `amount = remainingQuota`，`total = totalQuota`，`currency = CNY` |
| 2 | Spend | 本月已用 | `amount = usedQuota`（缺省回退 `spent`），`currency = CNY` |
| 3 | Text | 本月调用 | `value` = `callTokenCount`，`suffix` = 次 |
| 4 | Text | 今日调用 | `value` = `todayCallTokenCount`，`suffix` = 次 |

- `plan` = `tierName`
- `details`：配额周期（`quotaPeriod`）、用户（`userName` / `userCode`）、租户（`tenantName` / `tenantCode`）、状态（`status`）
- `Source` = catalog 名称（如有需要可附租户简称）

调用次数用 **Text**，避免为 Count 伪造 `total`。

### 设置 UI

- `QuotaSource == StoLlm` 时展开行：可选「DevOps Token」字段（与其它密钥相同的 DPAPI / `SecretRevision`）。
- 空状态提示：说明可依赖 sto-watt / 浏览器登录，也可粘贴 token。
- Catalog 配额说明：数据来自申通大模型网关；需 sto-watt 的 DevOps 登录或粘贴 token；无本机用量统计。

### 菜单分组

`ProviderCatalog.Group(StoLlm)`：

- 若存在 `%USERPROFILE%\.sto-watt`，或低成本探测到 dms-proxy 可用（TCP 连通，或本进程曾成功解析过 token）→「自动读取」；否则「手动填写」。
- 无论哪一组，列表中始终可选。

### 错误与陈旧数据

复用 `ConfiguredProviders` 的 last-good 与状态文案：

| 情况 | StatusLabel | 指引 |
| --- | --- | --- |
| 无 token | 未配置 / 未登录 | 打开 `https://devops-new.sto.cn/`，或安装/启动 sto-watt，或粘贴 token |
| 重试后仍 401 | 密钥无效 / 登录过期 | 浏览器重新登录；若用了粘贴 token 则更新 |
| `code != "0"` | 读取失败 | 展示网关 `msg`（脱敏 / 截断） |
| 超时 / 网络 | 读取超时 / 读取失败 | 有 last-good 则继续显示 |

## 测试

- `StoLlmAuth`：JWT 有效 / 过期 / 畸形；env 解析；解析顺序（手动 > env > bridge）；401 重试路径用假依赖。
- `StoLlmUsage`：fixture JSON → 四条指标 + plan/details；空 `data`；非 `"0"` 的 `code`；不触及敏感字段。
- `ConfiguredProviders` / catalog：`StoLlm` 允许空 secret；不是 usage source；分组辅助用假文件系统 / 探测。

CI 不访问申通内网真实接口。

## 文档

- 在 `docs/zh-CN/ai-tools.md`（及若有的英文对应页）增加一行：配额 ✓，用量留空，准备条件 = sto-watt DevOps 登录或粘贴 token。

## 安全说明

- 不要把 curl 示例里的真实 JWT 提交进仓库。
- 手动 token 只经现有 `IProviderSecrets`（DPAPI）保存。
- 响应中的密钥类字段（如 `keyPlain`）永不落盘、不进 UI。
