# SECTL 账号与共享名单 · 契约与决策记录

本文件记录本轮已验证的契约与已冻结的决策，不含任何密钥。平台 ID 与 Client ID 是 public client 的公开标识，可以随代码分发；本平台**不签发 client_secret**。文件中不得出现 access token、refresh token、service token、offline grant，也不得出现真实师生姓名或学校名。

## 一、已确认（带来源）

| 项目 | 值 / 结论 | 来源 |
| :--- | :--- | :--- |
| 平台 ID | `platform_605fbfaf048d21b9` | 用户提供 · SECTL 平台控制台凭证页 2026-11-24 |
| Client ID | `6aefdc780032bb88a5c4`（32 位 hex，**不是** `pf_` 前缀；代码只判非空，不判前缀） | 同上 |
| 客户端形态 | public client，不签发 client_secret；换 token 必须带 `code_challenge` / `code_verifier`（S256 PKCE） | 同上 |
| 登录方式 | OAuth 2.0（未切 OIDC；本轮用 userinfo 回查用户资料，不依赖 id_token） | 同上 |
| 已启用 scope | `user:read`（必选）、`cloud:read`、`cloud:write` | 同上 |
| 未启用 scope | `offline_access`、`cloud:peer_read`、`device:read` | 同上 |
| 回调地址 | 用户已在平台后台配置；**仍待联通烟测确认**精确匹配策略。本机侧使用 `http://localhost:53140/callback`（固定端口，`TcpListener` 只绑 `IPAddress.Loopback`） | 用户确认 + 本轮实现决策 |
| Base URL | `https://appwrite.sectl.cn`，端点直接接 `/api/...`，**不拼 `/v1`** | SECTL/sectl-auth 文档（commit 53730ef…）；`.top` 仅见于 Python SDK 示例，待实测 |
| 授权端点 | 浏览器打开 `https://sectl.cn/login?redirect=<urlencode(/oauth/authorize?...)>`，登录后由 SPA 跳到 `/oauth/authorize`；参数 `client_id` / `redirect_uri` / `response_type=code` / `code_challenge` / `code_challenge_method=S256` / `scope` / `state` | 同上 + 2026-11-24 实测 |
| **浏览器 / API 主机分离（已实测）** | **浏览器授权页在 sectl.cn（/login?redirect=/oauth/authorize?…），API 在 appwrite.sectl.cn；appwrite.sectl.cn/oauth/authorize 是监控事件端点（405），不能用于浏览器授权。** | 2026-11-24 `curl -i`：`appwrite.sectl.cn/oauth/authorize` → 405 `method_not_allowed`（提示改用 `POST /api/events` 上报监控事件）；`sectl.cn/oauth/authorize` → 200 SPA index.html；SecScore 同平台走 `sectl.cn/login?redirect=...` |
| 换 token | `POST {Base}/api/oauth/token`，form-urlencoded，字段 `grant_type` / `code` / `client_id` / `redirect_uri` / `code_verifier` / `device_uuid` | 同上 |
| 其它端点 | `POST /api/oauth/refresh`、`GET /api/oauth/userinfo`、`POST /api/oauth/introspect`、`POST /api/oauth/logout` | 同上 |
| 云 KV | `POST/GET/PATCH/DELETE /api/cloud/kv[/{key}]`，Bearer token，body `{client_id, key, value}`；数据按 (平台, 用户) 隔离 | 同上 |
| KV 限制 | key ≤ 255 字符；value ≤ 64 KB；每用户每平台 ≤ 1000 个 KV；超限 429 + `retry_after_ms`；平台未开通返回 `{"error":"cloud_service_disabled"}` | 同上 |
| **云服务开通开关（2026-11-24 实测）** | 平台详情页 →「基本设置」→「启用云服务」（字段名 `cloud_enabled`，说明"允许该平台使用 SECTL 云服务功能"）。**未开启时**任何 KV 请求都返回 `403 {"error":"cloud_service_disabled","error_description":"该平台未启用云服务"}`；此时 scope 即使含 `cloud:read/write` 也无用。用真实 token 实测：`GET /api/cloud/kv` → 403 disabled，`GET /api/oauth/userinfo` → 200（说明 token 有效、platform_id 正确） | 2026-11-24 用本机保存的 token 直调 + 平台前端 `BasicSettingsTab` JS（开关绑定 `cloud_enabled`） |
| 平台存储管理 API | 控制台显示「未配置」。本轮不配置：知微用 SECTL 自带云 KV，不走平台自托管 bucket（那是 M2M / 后端服务路径） | 控制台 + 本轮范围决策 |
| 后端云服务凭据 | 控制台中该 tab 不配置；本轮不引入 M2M service token | 本轮范围决策 |

## 二、未确认与 fail-closed（按优先级）

| # | 未确认项 | 影响 | fail-closed 行为 |
| :-- | :--- | :--- | :--- |
| 1 | **`offline_access` 未启用：桌面 public client 在标准授权码交换中是否返回 `refresh_token`？** 控制台说明该 scope 给的是「一次性 offline grant（180 天），后端凭 service token 换取 5 分钟 delegated token 并每次轮换」——那是后端服务路径，桌面端未必适用 | 决定 token 生命周期：有 refresh token → 单飞刷新 + 先落盘后使用；没有 → 到期即登出 | **不臆造 token、不静默续用过期 token**。若响应无 `refresh_token`，按「access token 到期后需重新登录」处理，给出中文提示；先按标准 OAuth 假设实现，联通烟测时实测确认 |
| 2 | token 端点 Content-Type 是否确为 form-urlencoded | 换 token / 刷新可能整体失败 | 按 form 实现；请求形状由假 handler 钉死；实测失败时报可读错误而非自动改 JSON |
| 3 | KV list 返回 keys 还是 keys+values | 索引丢失后的重建方式 | `ListAsync` 两种形状都兼容；读不到 value 时逐 key 再 GET；仍失败则保留索引原样并告警 |
| 4 | 64 KB 的计量口径（原始 JSON 字节 vs 序列化后 vs base64 后） | 边界处误判 | 取最保守口径：压缩后 envelope 的 UTF-8 字节 > 60 KB 即拒绝，提示精简字段 |
| 5 | `PUT` 是否区分创建 / 覆盖；`PATCH` 语义 | 冲突检测 | 不依赖服务端语义：覆盖前先 GET 比较 revision，不符即 `CloudConflictException`，无合并 |
| 6 | `POST /api/oauth/logout` 的请求体 | 退出登录的远端撤销 | best-effort，5 秒超时；无论成败都清本地凭据；失败时提示用户去 SECTL 控制台撤销 |
| 7 | userinfo 返回字段（`sub` / `name` / `preferred_username` / `email`） | 显示名 | 全部容错解析，取不到显示「已登录」；不要求 email |
| 8 | 不做 heartbeat 是否会导致 token 失效 | 长时间运行的会话 | 本轮**不实现心跳**（与本地优先冲突）；401 路径统一处理重新登录 |
| 9 | `.cn` vs `.top` 域名 | 请求打到错误主机 | 单一常量 `https://appwrite.sectl.cn`，不提供用户可改的 base URL 开关；实测失败即报可读错误 |

## 三、共享名单 schema 决策（冻结 v1）

- 文档类型：`teachcraft.roster.v1`（共享名单）、`seatgrid.daily`（座位表文档）。
- 存储布局：每种类型一份索引 KV + 一份文档一 KV。索引 key `teachcraft.roster.v1.index` / `teachcraft.seatgrid.v1.index`；文档 key `...doc.<id>`，id 为 32 位 hex。
- 名单版本 = 文档存储 revision（1 基），每次保存递增；座位表文档记 `RosterId` + `RosterVersion`。
- 名单字段：必需 `姓名` / `性别` / `学号`；可选且**默认关闭** `身高` / `视力` / `成绩` / `备注` / `原小组`，以 `RosterFieldPresence` 记录勾选状态。未勾选字段在投影时置空、不写入云端 JSON；有哨兵测试证明不泄漏。
- 座位表文档只存模块自有数据（设置、座位分配、约束、画布）+ 一份**只含已勾选字段的兜底名单快照**。名单被删时可用快照精确还原；否则按最新名单 + 序号/姓名重映射约束并告警。
- 云 KV value > 40 KB 时 gzip+base64 压缩；压缩后 envelope > 60 KB 拒绝上传。每命名空间软上限 200 份。

## 四、模块矩阵（文档与 UI 必须如实写明）

| 模块 | 1.45.0 状态 | 接线点 |
| :--- | :--- | :--- |
| 归位 · SeatGrid | **已实现**：发布 / 拉取共享名单；保存 / 拉取关联座位表；逐字段同意 | `ISeatGridCloudBridge` |
| 辑册 · WorkFolio | **已实现（仅拉取）**：多选班级名单，合并进批次名单 | `IHomeworkRosterBridge`；其名单模型无性别，故不能发布 |
| 分晓 · GradeStrip | **未接线**：它导入成绩表而非名单；未来适配器须检查 `Fields.Score` | `ISharedRosterSource` 预留 |
| 其它模块 | **未接线** | 同一 source 接口；UI 不得声称已接入 |

文档与界面使用固定表述：`归位可发布、拉取共享名单；辑册可拉取；分晓等其他应用后续接入。`

## 五、明确非目标

本轮不实现集控（不建 `Control/`、不写 `device_secret`、不长连接）；不做心跳、定时刷新、重试循环；不做定时器驱动的自动同步（归位座位表的可选自动同步是事件触发、无定时器，且默认关闭）；不上传原始 Excel / CSV / PDF / 图片与文件路径；不上传考场快照与其它模块数据；不自建账号密码体系；不提供用户可改的 base URL / client id 开关；不引入新项目、不引入 DI 容器。

## 六、验证清单（实现完成后跑）

1. 授权 URL 能在浏览器打开；回调被平台接受（验证回调地址精确匹配策略）。
2. 换 token 成功；记录响应**是否含 `refresh_token`**、`expires_in` 实际值——这是未确认项 #1 的最终证据。
3. userinfo 能返回可用于显示的用户资料（记录实际字段名）。
4. 云 KV：put → get → list → delete 全通；索引丢失后 list 能重建。
5. 64 KB 边界：39 KB / 41 KB / 压缩后 61 KB 三档分别验证。
6. 退出登录：本地凭据清空；云端数据保留（文案须说明）。
7. 平台未开通云存储 / 429 / 断网三种失败各有可读中文提示且不挂起。

每步都存截图或输出作为证据；联通烟测在 client id 与回调地址确认后执行，之前所有测试用假 handler。

## 七、实施完成（1.45.0，2026-11-24）

已落地（T0–T9b）：

- T0 平台与 client 常量：`SectlPlatformDefaults` 写入公开标识 `platform_605fbfaf048d21b9` / `6aefdc780032bb88a5c4`，public client + S256 PKCE，无 client_secret。
- T1 OAuth 客户端与 loopback 回调：`SectlOAuthClient` + `LoopbackOAuthReceiver`（`TcpListener` 只绑 `IPAddress.Loopback`，固定端口 53140），state 校验，授权 / 换 token / userinfo / 刷新 / 退出全链路注入 `HttpMessageHandler`。
- T2 凭据存储：`SectlTokenStore` 写 `%LOCALAPPDATA%\TeachCraft\Account\`，Windows DPAPI、其它平台明文，`ToString()` 脱敏，`.gitignore` 按文件名忽略；刷新单飞 + 先落盘后使用 + 失败 fail-closed。
- T3 云 KV 与文档存储：`SectlCloudKvClient` + `SectlCloudDocumentStore`，索引 + 一文档一 KV，revision 即版本，>40 KB 压缩、压缩后 >60 KB 拒绝，软上限 200 份。
- T4 共享名单模型与投影：`teachcraft.roster.v1` / `seatgrid.daily`，`RosterFieldPresence` 字段级同意，未勾选字段置 null 且不进云端 JSON，哨兵测试锁住。
- T5 宿主桥接与账号面板：`src/TeachCraft.App/Cloud` + `AccountPanelView` / `AccountWindow`，侧栏「账号与云同步」入口，四面板互斥，OOBE 第 4 页扩为四个动作。
- T6 归位适配器：`src/SeatGrid.Core/Cloud` + `src/SeatGrid.App/Cloud`，发布 / 拉取共享名单与关联座位表，版本 / 冲突 / 兜底快照 / 考场拒绝。
- T7 辑册适配器：`SharedRosterImport.cs` + `src/Homework.App/Cloud`，多班拉取、同名班级提示、性别缺口如实标注、不上传。
- T8 走查与测试：`--account-shot` / `--account-panel` / `--seatgrid-cloud-shot` / `--homework-roster-shot`，假 handler 与 Noop 凭据存储，不联网、不写真实账号状态。
- T9b 文档与版本：本文件、`AGENTS.md`、`.memory` 各篇、`README.md`、`.gitignore` 同步，版本升至 `1.45.0`（Minor）。

仍开放（待联通烟测 / 平台侧确认，不阻塞本版本发布）：

- **`offline_access` 是否返回 `refresh_token`**：第二节未确认项 #1 的最终答案只能从真实授权响应拿到；当前按「没有就到期重新登录」fail-closed 实现，烟测后据实调整生命周期文案。
- **`.cn` vs `.top` 域名**：当前单一常量 `https://appwrite.sectl.cn`；`.top` 仅见于 Python SDK 示例，待真实请求确认，不提供用户可改的 base URL 开关。
- **平台注册状态**：平台 ID / Client ID 已由用户提供并写入公开常量，回调地址已在平台后台配置；精确匹配策略与云 KV 是否已为该平台开通仍待联通烟测确认。
- **集控**：本版本未实现，仍按三处联网中的「预留」表述，不建 `Control/`、不长连接。

## 2026-11-25 审计剩余 16 项修复

- Core：座位表版本恢复读 `v`（回退 `version`）；40–64KB 不可压缩文档改发原文而非拒绝；404 按 HTTP 状态判定；account.json 缺失时清空孤立 token（fail-closed）；KV 分页按原始页大小推进。
- 设置：`ICloudSyncSettingsStore.TrySave` 默认接口成员返回失败，账号面板显示「设置未能保存到本机」；损坏文件恢复默认并提示。
- 归位：覆盖名单预填取所选行而非已关联；删除当前关联名单即解除本地关联；考场态提示「考场快照不能上传云端」；删除死代码；`LastRoster*` 接线（`LastRosterFields` / `RememberRosterFields`）。
- 辑册：跳过行分列空姓名/重复/名单缺失；重名班级按同一归一化键判重与合并；拉取页登录入口可达；刷新失败清空可选行；缺失名单计入跳过数。
- 实测：构建 0 警告 0 错误；非单实例 1986 项全过（TeachCraft.Tests 1710 + GradeStrip 108 + GradeWeave 168）；三个走查 exit=0。

## 座位表自动云同步（1.45.0 增补）

- 可选开关 `AutoSyncSeatDocument`，默认关闭；开启且已登录、非考场、已关联共享名单时，`RecordChange` 后的数据改动经 600ms 防抖上传一次座位表文档。
- 无改动不联网；不建常驻连接、无心跳、无定时器（防抖是单次用户操作触发的任务，不是周期定时器）。
- 失败只写状态提示，不改本地数据、不弹错误框；文档身份 `LastDocumentId` / `LastDocumentRevision` 走 `TrySave`。
- 这是唯一允许的自动同步；其余模块仍是手动上传 / 拉取。`LocalFirstCopyTests` 锁住默认关闭与设置可见。
