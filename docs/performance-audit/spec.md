# 知微 · TeachCraft 性能调查与治理 Spec

> 基线：`1.24.0`（2026-11-09） · 调查日期：2026-11-10
> 体裁沿用 `.trae/specs/gradestrip-large-data-perf/spec.md`（`Why / What Changes / Impact`）。
> 本文是**调查与治理**的契约，不是某一轮修复的实施记录。

---

## Why

### 1. 已有结论已过期，不能照抄

仓库里有一份 [性能调研报告-2026-10-27.md](性能调研报告-2026-10-27.md)。本次逐条回源核对后，
它的 8 条 P0 **至少 5 条已修或属误报**：

| 旧结论 | 现状 | 证据 |
| --- | --- | --- |
| P0-1「发布配置零调优，缺 `runtimeconfig.template.json`」 | **已修** | `src/TeachCraft.App/runtimeconfig.template.json` 已含 `System.GC.ConserveMemory = 9` |
| P0-4「归位预览临时 PNG 永不删除」 | **已修** | `src/SeatGrid.Core/Rendering/SeatChartRenderer.cs:61-86` 的 `RenderPngToTemp` 已带 `CleanStalePreviews` |
| P0-5「归位预览全尺寸位图在 UI 线程解码」 | **已修** | `src/SeatGrid.App/SeatPreviewWindow.axaml.cs:46` 已用 `Bitmap.DecodeToWidth` |
| P0-6「时序预览 2.0 倍渲染缩到 1 倍显示」 | **已修** | `src/ClassBlock.App/ClassBlockViewModel.cs:249,283` 已改 1.25 倍并带 `CancellationToken` |
| 第五节「织序自动保存每 2 秒在 UI 线程序列化整份 JSON」 | **误报**（旧报告自己也撤销了） | `src/GradeWeave.App/GradeWeaveWindow.axaml.cs:320-322` 序列化已在 `Task.Run` 内 |

因此**任何直接照搬旧报告的修复清单都是重复投入**。本次改为「核实 + 更新 + 补齐旧报告没覆盖的模块」。

### 2. 旧报告完全没覆盖后来新增的两个大模块

旧报告写于 2026-10-27，当时**竞名尚未接入秩序册导出（1.20.0，2026-11-09）**、
导出预览与赛程卡版式（1.21.0）、原表高亮与长任务遮罩（1.22.0）都不存在。本次在这两个模块里
发现了本次调查中**最严重**的问题（见下）。

### 3. 本次经回源核实的真实问题（25 条，按影响排序的前 8 条）

| # | 问题 | 位置 | 机制 |
| --- | --- | --- | --- |
| F-01 | 竞名导出预览在 **UI 线程同步**渲染整份文档，无防抖、无后台 | `MeetExportOptionsViewModel.cs:146-153` | 每次改设置（勾选包含内容 / 切 Excel↔PDF / 改版式）都同步跑完整个渲染，期间输入被阻塞 |
| F-03 | 为拿第 1 页预览，先 `BuildPages` **分页完所有班级所有页** | `MeetPdfExporter.cs:80-88` | `.Take(pageCount)` 排在 `BuildPages(...)` 之后；60 班秩序册要分完 83 页只为一页缩略图 |
| F-04 | 预览页按 1.8 倍渲染（1800×2484 ≈ 17.9MB）再缩到 760px 显示 | `MeetPdfExporter.cs:636-646` | 约 5.6 倍于显示目标的像素量 |
| F-02 | 预览位图换值时不 Dispose 旧图 | `MeetExportOptionsViewModel.cs:118-122` | `OnPreviewImageChanged` 未取 `oldValue`；每张 3.2–5.8MB 原生内存，GC 感知不到 |
| F-05 | 导入路径 `EndBusy()` 早于 `LoadProject()` | `MeetRosterViewModel.cs:435` vs `:506-507` | 遮罩在真正卡顿（约 1800 行 VM 构建）之前就消失，导入按钮提前重新可点 |
| F-06 | 一次秩序册导入把同一工作簿用 ClosedXML 开**三次** | `OrderBookImporter.cs:51-52,77,134-135`；`ScheduleWorkbookImporter.cs:26-27` | `DetectKind` + `OpenWorkbook` + `ScheduleWorkbookImporter.Import` 各自构造 `XLWorkbook` |
| F-11 | 时序总表识别把 `LastRowUsed()` 写进 `for` 循环条件 | `MasterScheduleImporter.cs:381,443` | 每轮迭代重算整表边界 → O(行²) |
| F-08 | 竞名报名表格与赛程树是非虚拟化 `ItemsControl` | `MeetRosterWindow.axaml:635,1159,1200,1249` | 同文件的班级列表却是虚拟化 `ListBox`（`:480`），模式已知却未套用 |

完整 25 条见 `.omo/drafts/perf-audit.md` 的「子代理发现与复核状态」表，每条带 `verified` / `待复核` 状态。

### 4. 测量设施是碎片化的，且关键路径零覆盖

| 现有通道 | 覆盖范围 | 缺口 |
| --- | --- | --- |
| `src/SeatGrid.App/SeatGridDiagnostics.cs`（`--slider-bench`） | 100 人 / 96 座的滑块拖拽 p50/p95 | 只覆盖归位滑块 |
| `tests/TeachCraft.Tests/MeetRosterLastRowBenchTests.cs` | 只测 ClosedXML `LastRowUsed` 的行为差异 | 不测导入耗时 |
| `src/MeetRoster.App/MeetRosterWindow.axaml.cs:56` `DumpRowMetrics` | 只量行内控件几何（y / h / gap） | 不量时间 |
| `src/TeachCraft.App/Views/MainWindow.Diagnostics.cs` | 截图与展开动画取证 | 不量时间 |

`ClassBlock` 与 `MeetRoster` 两个子系统 grep `Stopwatch|ElapsedMilliseconds` **零命中**；
导入、导出、自动保存、预览位图四条路径**没有任何基准覆盖**。这直接导致本次所有性能结论只能标
`inferred`（机制明确、数值未测），无法标 `measured`。

### 5. 性能无任何回归守卫

`.memory/06-决策与踩坑记录.md` 记录了多次「已修又被改回去」的模式（滑块样式、学生视角 180° 旋转、
`ExpandReveal` 停秒表）。性能项目前同样没有断言，唯一的守卫是人的记性。

### 验收口径（本 spec 的成功定义）

- **主目标是「可感知延迟」**（IS-1 / IS-2 / IS-3），不是内存数字。旧报告已论证「任何操作下 ≤200MB」
  不现实；内存只做实测追踪，不设硬目标，**且必须承认 ±20MB 低于测量噪声**（旧报告的 GC 对照实验
  在不同构建间给出 −20MB 与 −5MB 两种结论）。
- 任一导入 / 导出 / 保存操作，**状态栏与按钮态 100ms 内给出反应**；**遮罩仍按既有 400ms 阈值，
  不调快**——那个延迟是为「小文件不闪」刻意设计的，调快会重新引入闪烁。
- 三条真实规模语料（3000 人成绩表 / 60 班秩序册 / 20 班 694 节）有**可复现的**耗时基线，
  且能一条命令复测。
- **不改变**：工具 ID 与顺序、导出文件结构、界面布局与纸墨色板、全部中文提示、版本号规范、CI 契约。

### 参考机与产物形态（每条性能数字都必须标注）

| 项 | 约定 | 理由 |
| --- | --- | --- |
| 参考机 A | 开发机（24 核 / 16GB） | 与历史报告可比 |
| 参考机 B | **≤4 核 / 8GB** 档（限核启动或虚拟机） | 目标用户是学校笔记本；旧证据全部来自 24 核，低核区间从未测过 |
| 产物形态 | **发布版 `-sc` 单文件自解压** | 用户真正运行的那种；旧报告测的是 `bin/Release`，与发布产物不同 |
| 并发敏感点 | `MeetRosterViewModel.cs:417` 的 `MaxDegreeOfParallelism = Environment.ProcessorCount` | 并发度随核数变化，必须在低核机复测 |

**只写数字不写「哪台机器 / 哪种产物」的结果一律视为不合格。**

### 非目标（明确不覆盖）

- **性能证据仅覆盖 win-x64。** macOS / Linux 不在本次范围。
- 任何渲染相关改动（如 `src/TeachCraft.App/Program.cs:52` 的 `UsePlatformDetect`）**不得改变非 Windows
  平台行为**；若要动渲染后端，必须单独立项并证伪非 Windows 回归。
- 真实文件核对属于**人工环节**：AGENTS.md 第九节要求「用真实文件跑一遍」，而这些文件按第十节隐私红线
  不得入库。因此代理验收一律基于合成语料，真实文件核对在交付说明里单列，不计入自动化判定。

---

## What Changes

### 阶段一 · 建立测量（先测后改）

- 新增统一基准通道：复用仓库既有的「`--*-bench` 命令行参数 + 写 Markdown 报告」模式
  （先例：`SeatGridDiagnostics.RunLayoutBenchAsync` → `outputs/slider-bench-*.md`），
  **不引入 BenchmarkDotNet**（避免新增依赖与 CI 契约变更）。
- 为三条真实规模语料建立**合成** fixture（严格遵守 AGENTS.md 第十节隐私红线：
  合成学校 / 教师 / 学生，禁止引用任何真实数据）。
- 给四条零覆盖路径（竞名导入、竞名导出、时序导入、分晓 `.xls` 导入）加 `Stopwatch` 计时，
  汇入基准报告。
- 复测空载内存与峰值内存，**在目标硬件档**（低核 / 8GB）上也测一遍，而不只是 24 核开发机。

### 阶段二 · 修复有确定机制的问题

按「收益 / 风险 / 可独立验证」排序，每项都能单独验证、单独回滚：

- **P0（有确定机制、风险低）**：F-01/F-03/F-04 预览链路、F-02 位图泄漏、F-05 `EndBusy` 顺序、
  F-06 工作簿重复打开、F-11 循环内查边界。
- **P0 补充**：归位与时序**完全没有忙碌遮罩**（`SeatGridWindow.axaml` 与 `ClassBlockWindow.axaml`
  grep `busy|overlay` 均为 0 命中，只有按钮禁用），而分晓 / 织序 / 竞名三家都已有——这是反馈层
  最真实的缺口，按竞名 400ms 阈值补齐。
- **P1（需测量支撑再动）**：F-08 虚拟化、F-09 高亮重建、F-07 原表高亮整本载入。
- **P2（记录但不做）**：启动期内存、渲染后端 A/B —— 旧报告已论证不确定，本次只复测不下结论。

### 阶段三 · 建立守卫

- 把「不许回退」写成测试或基准阈值：预览不得回到同步渲染、预览位图必须 Dispose、
  导入不得重复打开工作簿、时序导入不得在循环内查边界。
- 阈值取**宽口径**（如 2 倍告警线），避免机器抖动造成 flaky。是否接入 CI 见 `task.md` 的决策项。

### BREAKING 变更

**无。** 本 spec 不改变工具 ID、导出结构、界面布局、提示文案与版本号规范。

---

## Impact

### 受影响的人

| 角色 | 今天 | 之后 |
| --- | --- | --- |
| 一线教师（8GB 学校本） | 竞名改一次导出设置冻一次；导入完遮罩先消失再卡 | 改设置即时响应；卡顿全程有反馈 |
| 维护者 | 只有一份过期报告 + 各模块各自为政的 bench | 一份可复测基线 + 明确的修复优先级 + 回归守卫 |
| 后续执行者 | 需要重新调查一遍才知道改哪 | 拿到「文件:行号 + 改法 + 验收命令」即可动手 |

### 受影响的范围

- `src/MeetRoster.App`：`MeetExportOptionsViewModel.cs`、`MeetRosterViewModel.cs`、`MeetRosterWindow.axaml`
- `src/MeetRoster.Core`：`MeetPdfExporter.cs`、`OrderBookImporter.cs`、`ScheduleWorkbookImporter.cs`、
  `MeetOriginalWorkbookHighlighter.cs`、`MeetCardRenderer.cs`
- `src/ClassBlock.Core`：`MasterScheduleImporter.cs`、`TimetableRenderer.cs`
- `src/ClassBlock.App`：`ClassBlockViewModel.cs`
- `src/GradeStrip.Core`：`GradeSheetImporter.cs`
- `src/SeatGrid.App` / `src/SeatGrid.Core`：`SeatGridDiagnostics.cs`（仅扩展基准）
- `src/TeachCraft.App`：`App.axaml.cs`（新增基准走查参数入口）
- `tests/TeachCraft.Tests`、`tests/GradeWeave.Tests`、`tests/GradeStrip.Tests`：新增守卫测试

### 版本与流程

- 性能修复按 **Patch** 晋级（AGENTS.md 第二节：Patch = Bug 修复、文案/间距/**性能微调**）。
- 新增基准走查参数属于开发工具，不改变用户可见能力，同样按 Patch。
- 若某条改动触及导出结构或工具契约，必须**单列并升 Minor**——本 spec 明确不做这类改动。

### 明确不做（Must NOT have）

- 不改任何产品行为、导出结构、界面布局、中文文案。
- 不引入 BenchmarkDotNet / 新的性能测试框架 / 新 NuGet 依赖。
- 不重开 `PublishTrimmed`、`PublishReadyToRun`、`ServerGC`、`TieredPGO=0` 的讨论——旧报告已否决。
- 不追求「任何操作下 ≤200MB」。
- 不把性能数据、截图或真实文件提交进仓库；基准报告只落本机 `outputs/`（已 gitignore）。
- 不重做已验证的优化：分晓虚拟化与全异步、归位 `SeatPaintCache`、织序 `_masterCells`、
  时序预览防抖管线、PDF 向量生成。
