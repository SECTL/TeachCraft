# SECTL 集控中心接入 · 契约与实现记录（1.46.0）

## 一、定位与边界

- **平台与协议**：`platform = "teachcraft"`，`protocol = 4`，默认服务地址 `https://sectl.cn`（WebSocket 路由 `/control/ws`，REST 前缀 `/control/api/*`）。
- **零新增 NuGet 依赖**：WebSocket 直接使用 .NET 10 BCL `ClientWebSocket`（经 `IControlWebSocket` 抽象支持测试替身）；凭据保护复用 `ITokenProtector`（Windows 使用 `DpapiTokenProtector`）。
- **默认关闭**：`ControlSettings.Enabled = false`、`LocalRemoteAllowed = true`、`Ipc = false`、`AutoReselect = false`。走查参数（`--*` → `isWalkthrough = true`）在 `ControlWiring.Create` 直接返回 `ControlRuntime.Disabled`，绝不构造服务、不读写 `%LOCALAPPDATA%\TeachCraft\Control\`、不建连接。
- **存储路径隔离**：
  - `%LOCALAPPDATA%\TeachCraft\Control\settings.json`
  - `%LOCALAPPDATA%\TeachCraft\Control\credential.json`（由 `.gitignore` 按文件名忽略）
  - `%LOCALAPPDATA%\TeachCraft\Control\state.json`（保存 `applied_state_revision` 与最近 512 条指令幂等回执）
  - 绝不使用 `%LOCALAPPDATA%\SECTL\`，避免与同机其他 SECTL 客户端互相干扰；若校园网教室机装有还原卡，需把上述 `TeachCraft\Control` 目录加入穿透白名单。

## 二、接入与自助接口

1. **匿名双因子接入**：`POST /control/api/enroll`（`site_id` + `code` + `platform` + `device_name` + `machine { host, guid }`），成功后立即清除输入框内的配对码。
2. **SECTL 账号接入**：当用户在「账号与云同步」完成登录后，可先调 `GET /control/api/sites` 读取地点列表，再通过 `POST /control/api/device/enroll`（`Authorization: Bearer <token>` + `siteId` + `platform` + `deviceName` + `machine`）直接接入，免填配对码。
3. **设备自助**：`POST /control/api/device/tree`（班级树）、`POST /control/api/device/assign`（归属班级）、`POST /control/api/device/seats`（本班座签几何与标签，不含姓名）、`POST /control/api/device/unenroll`（退出并清理本机凭证）。

## 三、能力与隐私红线

声明能力固定为以下 8 项：

- 控制类：`seatgrid.open`（`view?: teacher|student`）、`seatgrid.view`（`view`）、`seatgrid.project`（`on`）、`seatgrid.exam`（`on`, `columns?: 4..8`）、`classblock.show`（`class_name?: <=20`）。
- 只读摘要类：`seatgrid.read`、`classblock.read`、`gradeweave.read`。

**绝对禁止**：不声明 `roster.*`、`draw.*`、`grades*`、`meetroster.*`；不关闭/伪造命名管道；任何查询或回执都不得返回学生姓名、学号、成绩、教师单节课明细或原始文档路径。所有触碰窗口 / ViewModel 的操作经 `TeachCraftAppControlHost` 通过 `Dispatcher.UIThread.InvokeAsync` 调度回 UI 线程，并在 `DeferredReply` 下异步 `ReportAsync`，避免阻塞 WebSocket 接收循环。
