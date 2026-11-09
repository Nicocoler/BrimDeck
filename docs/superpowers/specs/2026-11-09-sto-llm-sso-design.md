# 申通大模型 SSO 鉴权设计

将「申通大模型」配额来源的鉴权从 sto-watt / dms-proxy / 手动粘贴，改为文档约定的申通单点登录（扫码 → authorizationCode → StoToken），由 BrimDeck 自行完成换票与缓存。用量接口与指标映射不变。

相关文档：`docs/superpowers/docs/单点登录接入文档.md`。本设计替代 `2026-11-06-sto-llm-quota-design.md` 中的鉴权与设置 UI 决策；HTTP 用量与指标章节仍适用。

## 目标

- 设置页一键「登录申通账号」：系统浏览器打开 SSO，扫码后本机回调拿临时 Code，再换 `StoToken`。
- 登录态本地 DPAPI 持久化；JWT 有效期内静默拉用量；过期或 401 引导重新登录。
- 完全不再依赖 sto-watt、`~/.sto-watt/.env`、dms-proxy `8765` bridge，也不再提供 DevOps Token 粘贴框。

## 非目标（本变更）

- 申请或变更单点后台的正式子系统编码流程（v1 使用 `systemCode=UI`；若线上受限再改为可配置）。
- 内嵌 WebView2 / 用户手动粘贴 authorizationCode。
- 改 `llmKeysByUser` 的 URL、headers 形态（仍用同一 JWT 填 `auth_token` + `stotoken`）或指标映射。
- 展示或持久化网关响应中的 `llmKey` / `keyPlain`。
- 多账号并行登录（每个 `AppEntry` 实例各自一份登录态即可）。

## 产品决策（已锁定）

| 议题 | 选择 |
| --- | --- |
| 登录方式 | A：系统浏览器 + 本机 `HttpListener` localhost 回调 |
| Token 来源 | 仅 SSO 换票；删除 sto-watt / bridge / 粘贴 |
| systemCode | v1 固定 `UI`；`categoryCode` 同为 `UI`（或与 systemCode 相同）；`getRoles`/`getModules` = false |
| SSO 环境 | 默认生产外网 `https://baseinfo-usercenterserviceapi.sto.cn` |
| 换票 API 基址 | 与 SSO 同域：`/WebAPIV46/API/LogOn/LogOnStoTokenByAuthorizationCode` |
| 退出 | 清本地缓存；尽力调用 `/webapiv46/api/logon/signOut`（失败不阻断） |
| 菜单分组 | StoLlm 固定归入「自动读取」（已登录）或需登录时的空态提示；不再按 sto-watt 目录探测分组 |

## 架构

```text
设置「登录」
    │  启动本机 HttpListener（127.0.0.1:随机端口/callback）
    │  Process.Start 打开 SSO Login URL（systemCode=UI, returnUrl=回调）
    ▼
用户扫码 → 浏览器跳转 returnUrl?authorizationCode=...
    │
    ▼
StoSsoClient.ExchangeAsync(code)
    POST .../LogOnStoTokenByAuthorizationCode
    → StoToken + OpenId（及展示用 RealName/UserName）
    │
    ▼
IProviderSecrets.Write(instanceId, payload)   // DPAPI，同现有 secrets.dat
AppEntry.SecretRevision = new Guid            // 标记已登录
    │
    ▼
ConfiguredProviders / RunStoLlmAsync
    token = StoLlmAuth.ResolveToken(secret)   // 只读本地缓存；校验 JWT exp
    → StoLlmUsage（llmKeysByUser）不变
```

### 组件边界

| 单元 | 职责 | 依赖 |
| --- | --- | --- |
| `StoSsoClient`（Core） | 拼 Login URL；换票；退出；解析响应 | `HttpClient`；可注入基址 |
| `StoSsoLoopback`（UI 或 Core） | 短暂 `HttpListener`；解析 query 中的 code；回写简单 HTML「可关闭窗口」 | 无 UI 框架强绑定则放 Core |
| `StoLlmAuth` | 重写：JWT 校验；从 secret 载荷读 StoToken；删除 env/bridge | — |
| `SettingsProviders` StoLlm 行 | 「登录 / 退出」按钮 + 已登录展示名；去掉密码框 | `StoSso*`、`IProviderSecrets` |
| `ScriptProvider.RunStoLlmAsync` | 只用 `ResolveToken`；401 → 清登录态提示重登（不再 bridge 重试） | — |

### 本地载荷格式

`IProviderSecrets` 对 StoLlm 存的是 **UTF-8 JSON 字符串**（仍整段 DPAPI），不是裸 JWT：

```json
{
  "stoToken": "<jwt>",
  "openId": "<OpenId>",
  "displayName": "<RealName 或 UserName>",
  "obtainedAt": "<ISO-8601>"
}
```

- `ResolveToken`：反序列化后取 `stoToken`，`IsJwtValid` 通过才返回。
- 兼容：若旧数据是裸 JWT 字符串（用户曾粘贴），可读一次并在下次登录时覆盖为 JSON；不要求迁移工具。
- `SecretRevision != Empty` 表示「已登录或曾保存过密钥」；退出时 `Delete` + `SecretRevision = Empty`。

### 回调约定

- `returnUrl`：`http://127.0.0.1:<port>/callback/`（端口在监听前选取空闲端口）。
- 单点后台需登记该回调形态；v1 使用 loopback，**不**使用自定义 URL 协议。
- 超时：监听默认 3 分钟无 code 则取消登录，关闭 listener。
- 回调页：返回极简 HTML（中英一句「登录成功，可关闭此页」），避免空白。
- 同时只允许一次进行中的登录（再点登录先取消上一次 listener）。

### Login URL

```
{base}/Authenticate/Login?autoLogin=false&systemCode=UI&returnUrl={UrlEncode(callback)}
```

默认 `base` = `https://baseinfo-usercenterserviceapi.sto.cn`。

### 换票请求

- Method：POST JSON（与常见 WebAPI 一致；实现时以实际接口 Content-Type 为准，单测用 fixture 固定）。
- Body 字段：`systemCode=UI`，`categoryCode=UI`，`authorizationCode=<code>`，`getRoles=false`，`getModules=false`。
- 成功：`Status == true`（或 `StatusCode == "OK"`）且非空 `StoToken`；保存 `UserInfo.OpenId`、展示名。
- 失败：把 `StatusMessage` 展示给用户；不写 secrets。

### 退出

1. 若有 `openId`：GET/POST `{base}/webapiv46/api/logon/signOut?systemCode=UI&openId=...`（文档两种动词都写了；实现优先 POST，失败再记日志）。
2. 无论远端是否成功：本地 `Delete` secret、`SecretRevision = Empty`、刷新 UI。

## 设置 UI

- 去掉 StoLlm 的 DevOps Token 密码框与「可选；留空则 sto-watt…」提示。
- 展开行：
  - 未登录：主按钮「登录申通账号」；说明「扫码登录后读取大模型配额」。
  - 已登录：展示 `displayName`（或工号）；按钮「退出登录」。
- Catalog 说明文案改为：经申通单点登录读取网关月度配额；无本机 Token 用量统计。
- `ProviderCatalog.Group(StoLlm)`：不再探测 sto-watt；已登录可显示在「自动读取」，未登录仍可选（点进设置去登录）。

## 错误与陈旧数据

| 情况 | StatusLabel | 指引 |
| --- | --- | --- |
| 无有效 StoToken | 未登录 | 在设置中登录申通账号 |
| 换票失败 | 登录失败 | 展示 StatusMessage；可重试 |
| 回调超时 / 取消 | （设置内提示） | 未写入登录态 |
| 用量 HTTP 401 | 登录过期 | 清除无效 token 提示；引导重新登录 |
| `code != "0"` / 网络 | 与现网关逻辑相同 | last-good 仍可用 |

## 测试

- `StoSsoClient`：换票成功 fixture → 得到 token/openId；Status false；缺 StoToken。
- `StoLlmAuth`：JSON 载荷有效 / 过期 JWT；裸 JWT 兼容；不再覆盖 env/bridge 用例（删除或改写为「已移除」）。
- Loopback：用本地 listener + 伪造 redirect 解析 code（不访问真实 SSO）。
- UI 逻辑可测部分：已登录 / 未登录时 `SecretRevision` 与按钮态（若现有设置测试模式允许）。

CI 不访问申通真实 SSO 或网关。

## 文档

- 更新 `docs/zh-CN/ai-tools.md`、`docs/en-US/ai-tools.md`：准备条件改为「在 BrimDeck 设置中扫码登录申通账号」。
- 在本 spec 旁可链到 `单点登录接入文档.md`；注明回调需在单点侧登记 loopback（若运维有额外要求，实现前再确认一次）。

## 安全说明

- 不把真实 StoToken / authorizationCode 提交进仓库或写入日志正文。
- Token 仅经现有 `ProviderSecrets`（DPAPI CurrentUser）保存。
- Loopback 只绑 `127.0.0.1`，不对外网卡监听。
- 响应中的密钥类字段永不落盘、不进 UI。

## 相对旧设计的删除清单

- `StoLlmAuth.ReadEnvToken` / `FetchBridgeTokenAsync` / `IsAutoAvailable` / `ProbeProxy` / `DefaultStoWattHome` / `DefaultProxyPort`
- `ResolveToken` 的 manual > env > bridge 顺序 → 改为只解析本地 SSO 载荷
- 设置中的 StoLlm 密钥输入与 sto-watt 相关文案
- 测试与 ai-tools 文档中的 sto-watt / 粘贴路径描述
