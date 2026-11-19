# AGENTS.md

> 本文件供所有参与维护「知微 · TeachCraft」的 AI 智能体与开发者共同遵守，定义版本号规范、CI/CD 契约、仓库结构与编码准则。

---

## 一、项目定位

「知微」是面向教师的本地工具箱：10 件图片 / PDF 小工具，加上「分晓 · GradeStrip」成绩条生成器、「归位 · SeatGrid」座位表生成器、「时序 · ClassBlock」课程表生成器与「织序 · GradeWeave」全年级课表编排器，统一使用分晓的纸墨设计语言与本地优先原则。技术栈为 **C# / .NET 10 + Avalonia 12**，所有文件处理在本机完成。联网只有两处、都由用户明确开启：点「检查更新」读取版本号（GitHub Releases，或用户选定的 SECTL 镜像），以及主动开启「接入 SECTL 集控」后以设备身份保持一条长连接接收指令——集控默认关闭，关闭时不建立连接、不发送任何信息。

---

## 二、版本号管理规范（与分晓同构）

采用 **语义化版本 (SemVer 2.0.0) + 构建流水线递增号**：

$$\text{v}\{Major\}.\{Minor\}.\{Patch\}[-\text{preview}.\{RunNumber\}]$$

### 1. 唯一版本源

基准版本只写在仓库根目录的 `Directory.Build.props`：

```xml
<Version>1.0.0</Version>
```

**任何发布脚本与工作流都不得重复硬编码版本号。** 升级版本时只改这一处。

### 2. 触发规则

| 触发 | 版本号 | 发布形态 |
| :--- | :--- | :--- |
| 推送 git tag（如 `v1.30.0`） | `v1.30.0` | 正式 Release，`prerelease: false` |
| `workflow_dispatch` | `v{Version}-preview.{github.run_number}` | 公开 Pre-release |
| push `main` | `v{Version}-build.{github.run_number}` | **不创建 Release**，产物只作为 CI artifact |

- **push `main` 不再发布预览版。** 此前每提交一次就多一个预览版，发布页被淹没，且编号复用 `run_number` 而不连续（失败或跳过的运行会占号）。现在 push `main` 仍跑测试与 14 个包的构建——那能证明每个平台都构建得出来——但产物只留作 artifact。
- push `main` 的版本串带 `-build.` 而不是 `-preview.`：它不发布，但身份仍不能显示成正式版号，否则有人手动下载 artifact 装上会被「检查更新」误判。
- 预览版只在手动触发时产生，标签由 `softprops/action-gh-release` 自动创建；预览版保留在 Releases 中，由维护者按需清理。
- 版本脚本：`scripts/version.ps1`，读取 `GITHUB_REF_TYPE` / `GITHUB_REF_NAME` / `GITHUB_EVENT_NAME` / `GITHUB_RUN_NUMBER`，输出 `VERSION_TAG` / `IS_RELEASE` / `SHOULD_PUBLISH`。

### 3. 晋级规则

| 版本段 | 触发场景 |
| :--- | :--- |
| **Major** | 数据格式、工具 ID、发布结构发生不兼容变更 |
| **Minor** | 新增工具、新增输出格式、新增平台支持、界面大改版 |
| **Patch** | Bug 修复、文案/间距/性能微调 |

当前基线：`1.36.0`（2026-11-18 第二轮 UX 审计修复轮，Minor 晋级：按 `.omo/reports/UX调研报告-2026-11-18.md` 的 72 条（P1×6 / P2×28 / P3×38）逐条修复，计划见 `.omo/plans/ux-audit-round2-fixes.md`。**三条 P1**：① **更新安装守卫时序倒置**——`UnsavedWindowGuard` 的扫描与确认原先插在 `Process.Start` **之后**，点「暂不安装」时静默安装包已在跑（`/CLOSEAPPLICATIONS` 还会被 Inno 反过来关掉正在用的知微）；绿色版替换路径更是完全没有守卫。现在抽 `ConfirmUnsavedBeforeInstall` 作共用前置，两条路径都在启动安装包 / 助手之前先扫，判定抽 `DecideInstallGate` 纯函数，两条「修复前必红」顺序断言锁住。② **OOBE 布线缺陷（1.30.0 起即存在）**——`showOobe = forceOobe || !isWalkthrough` 让正常启动恒为 true，`OobeStateStore` 里的「已完成 / 已跳过」从未参与判定，于是引导每次启动都强制弹；同文件注释写着「正常启动也传 null，由状态决定」，代码与注释直接矛盾，而走查路径因 `isWalkthrough=true` 恰好永远照不出来。现在正常启动传 null，四种取值（metrics / 普通走查 / 强制通道 / 正常启动）逐一写明并补布线级回归。③ **归位考场快照 × 撤销栈 × 导入链路三条交叉缺陷**——考场下导入名单 / 配置不清考场三态，导入后点「返回日常座位」会旧名单整体复活、新名单静默丢失；撤销跨考场边界后 `RestoreState` 无条件置空 `_dailySnapshot`，返回日常静默失效（还产生假撤销步）；考场「锁定不生效」的弹窗承诺与行为相反（按提示解锁会真删日常锁）。修法是给 `SeatStateSnapshot` 加**独立可空字段** `DailySide`（不设 required，避免测试工厂编译炸），`CaptureState` 在考场态嵌入日常侧、`RestoreState` 据此重建——**考场态快照里没有日常侧数据**，只有让考场态快照自己携带才成立。**其它成体系的改动**：竞名「另存为确认如实计数」是上一轮的**假修复**（提交说明与计划书都写着已修，`git log -S` 全历史零命中），本轮用 `RegistrationExportPlanner`（判定与文件系统注入）真做，并让「跳过」为真——被拦的原件一份都不写；`ArchiveExportTracker` 让关窗留档只认本次导出（旧路径会让「取消导出则留在编辑态」失效）。分晓导出守卫补上分析报表的 `SourceFullPath`（此前只比对成绩总表），替换考试补 `IsSameFile` 查重，`examKey` 去掉 `Order` 只用 `SourcePath`（重排 / 删除其他场次不再冲掉手改标题与筛选），二级页随会话重建；`ScoreStripExporter` 补 Core 层守卫（AGENTS 八.8 红线，上一轮 T2 的「不改 Core」就此作废）。织序拖拽导入补切换确认，并补上原案会漏的空档——`ConfirmSwitchProjectAsync` 在 `!HasProject` 时首行直接放行，「没开工程但本机有崩溃恢复文件」这个最危险场景仍会静默跳过，现加 `includeRecovery` 参数；孤儿走班课的指针入口（左键 / 右键 / 拖动）与 `ClearLessonAt` 对齐，`ConstraintRules.IsOrphanedLayeredLesson` 作唯一判定源。时序日块右界按「本块星期表头合并末列 ∩ 下一天表头列 - 1」重算（旧实现用整表末列，8 列日块下会读到下一天），课服 2 与晚修回退列不再跨日；早读诊断串从脚注移进 warnings（它会印上墙且超宽被裁）。工具箱水印预览字号改「正式公式套原图宽度再折算预览宽」（旧 `Math.Max(8f, 240×0.032×s)` 在 s≤1.04 时恒 8px，整段滑块行程无效）、复用 `IsLightImage` 选字色、解码走 EXIF 摆正；工具取消语义如实（「取消将在本轮处理完成后生效」），清理判定改「本轮运行起点快照」——旧 `File.Exists` 会在重跑取消时删掉上一场完好成品；参数预检前移到 `PickOutputAsync` 之前。单实例二次启动唤回已有窗口（命名 `EventWaitHandle`），`UnsavedWindowGuard` 派发表补时序（`HasMaster`）。**测试 949 → 1151 项**（TeachCraft 714 → 875、GradeWeave 133 → 168、GradeStrip 102 → 108），守卫类修复普遍带「修复前必红」对照。上一版 `1.35.0`（2026-11-17 UX 审计修复轮，Minor 晋级：按 `.omo/reports/UX审计报告-2026-11-17.md` 的 89 条（P0×3 / P1×17 / P2×45 / P3×24）逐条修复，计划见 `.omo/plans/ux-audit-fixes.md`。**三条 P0**：① 工具箱十件工具统一在 `ToolDispatcher.EnsureOutputDoesNotOverwriteInput` 拒绝输出覆盖输入（`PdfToImages` 的输出是目录，按将要写出的 `-第N页.png` 逐个判重）；② 分晓 `WouldOverwriteImportedSource` 在导出前比对全部导入源，`OnClosing` 有数据时先确认（走查通道 `--gradestrip-shot` 必须静默放行，靠 `ShouldConfirmOnClose(hasData, closeConfirmed, isWalkthrough)` 这个可测的纯函数）；③ 竞名导出把 `project.SourcePath` 也算进 `IsExportPathBlocked`。**其它成体系的改动**：`UnsavedWindowGuard` 在安装更新前扫出有未保存内容的窗口（`HasUnsavedWork` 按 DataContext 类型派发）；`WindowFit.Clamp` 收编全部 17 个窗口并删掉 GradeWeave 自己那份 `ScreenFit`（它比较的是 DIP 与物理像素、且从不降低 MinHeight，双向都错）；归位滑块交互只在内容真变了才入撤销栈（`_interactiveSnapshot` + `ContentEquals`，不要把它塞进通用 `RecordChange`——那会改掉全部 30 处入口的语义）；织序 `DeleteLayeredGroup` 连带清除该组走班课、`ClearLessonAt` 放行孤儿走班课；时序模板「复制为自定义」fork 新 Id（否则保存会覆盖源模板）、绑定行的「五天合并」勾选置灰、课服 / 晚修列按表头定位找不到就留空并告警（不再盲回退读相邻天的列）；竞名支持移除单个班、关窗给「先导出留档」出路、「打不开」与「认不出」分开报；工具箱接入 `IProgress<int>` 长任务进度与 `CancellationToken` 取消、水印面板加 240px 实时预览（`ImageToolbox.RenderWatermarkPreview`，与正式 `Watermark` 同一套字号 / 位置语义）、合并失败报出第几份与文件名。**删掉一个假开关**：`MaxSameSubjectPerAfternoon` 从未被调度器实现，AGENTS.md 与 `.memory` 里承诺的「下午同学科不连排」因此从未生效——声明已删除，文档不再承诺。测试 685 → 714 项（另 GradeWeave.Tests 130 → 133，GradeStrip.Tests 保持 102，合计 949）。上一版 `1.34.0`（2026-11-17 更新模块显示下载速度，Minor 晋级：新增 `src/TeachCraft.Core/Updating/DownloadProgress.cs`。此前只报 `IProgress<int>` 百分比，「正在下载 45%」卡住时分不清是慢还是断了；现在 `DownloadProgress`（百分比 + 已下载 + 总量 + 速度 + 是否知道大小）经新增的 `IProgress<DownloadProgress>` 重载传出，**旧 `IProgress<int>` 重载保留**。**速度必须用 3 秒滑动窗口，不能用「总字节 / 总耗时」**——后者是全程平均，慢启动一直拖着、网速恢复了数字还在爬；卡住时平均值又缓慢下滑，迟迟不显示「快停了」。`DownloadSpeedMeter` 纯函数式，跨度不足 250ms 返回 0（除以接近零的时间会得到「12.3 GB/s」这种一眼假的数），采样点永远至少留两个。**两个坑**：① `lastEmit` 初值用 `TimeSpan.MinValue` 会 overflow，`OverflowException` 被外层 catch 包装成「请检查网络后重试」——纯计算 bug 伪装成网络故障；② **不能只信响应头**：资产声明 1024、响应头也写 1024、实际只给 512 时，「读到字节 == 响应头」会通过，半截文件被当成完好的——进度用响应头，**结束时响应头与 asset.Size 两个基准都比**。界面：更新页进度条下方等宽小字「已下载 26.2 MB / 58.2 MB · 1.4 MB/s · 剩余约 23 秒」，**不含百分比**（上面已写 45%）；状态栏用 `ProgressLine` 才带百分比。文案全在 `DownloadText`（纯函数）。新增走查参数 `--preview-update-speed`（喂假进度，不联网不落盘），测试 647 → 685 项。上一版 `1.33.2`（2026-11-17 归位顶栏撤销 / 重做与「导入名单」重叠，Patch：根因是布局不是样式——撤销 / 重做被塞进标题与工具组之间的 `*` 弹性列，窗口一窄那列被压到不足按钮宽度，**而 Avalonia 不裁剪溢出内容**，按钮就画到右侧工具栏底下了；放进左侧 `Auto` 组又会被标题 `MaxWidth=280` 挤出可视区（表现为按钮消失）。最终挪进右侧 `WrapPanel` 开头：放不下时整排换行，既不重叠也不会消失。顺带修掉既存问题：那排 12 个控件在最小宽度 1180 下本来就溢出、「导出 Excel」被切掉一半，现在 `ShowHeaderTag` 在 <1300 时隐藏「课堂应用」小标腾位，`SetHeaderWidth` 只在跨阈值时发通知。新增走查参数 `--seatgrid-width <px>`——1440 下永远看不出这类问题。测试 645 → 647 项。上一版 `1.33.1`（2026-11-17 归位教室画布补「清空画布」，Patch：`ClearCanvas()` 早就存在，但右侧面板里**没有任何按钮调用它**——点过 U 型 / 多岛这些快速形状之后，中间那片虚线空格（`SeatChartCanvas.DisabledSeats` 写进去的过道标记，不是真间距）只能开画布编辑器一格一格刷回座位，或者靠撤销。现在「编辑画布…」右边并排一个「清空画布」，`CanClearCanvas` 在画布为空时置灰，清空走 `ApplyCanvas` 因而可撤销。测试 642 → 645 项，新增走查参数 `--seatgrid-shot <png> ushape|cleared`。上一版 `1.33.0`（2026-11-17 归位撤销 / 重做，Minor 晋级：新增 `src/SeatGrid.App/SeatUndoStack.cs`。**形态是整份快照而不是命令对象**——归位有 30 来处会改数据的入口，逐条写 Do/Undo 既容易漏、也容易在「撤销时依赖已被改掉的状态」上出错；快照是 O(座位数 + 人数) 的扁平拷贝，代价可忽略，换来「任何入口都没忘记登记」这条更强的保证。`RecordChange(label, action)` 是唯一登记点，**先拍照再改**；恢复期间 `_restoring` 为真不入栈，撤销 / 重做自身不能成为一步。快照只存**数据**：学生用 `Student.CopyKeepingId()`（`Student` 是普通类，不能用 `with`；普通 `Copy()` 会换 Id，座位与约束都会错位），座位存坐标，撤销时按 Id 复用现有 ViewModel——新造会让「已选中学生」与右侧编辑框脱钩。**考场必须进快照**：它把 `CurrentLayout` 整体换成 `_examPlan.Layout`，只还原座位不还原 `IsExamMode`，撤销看起来就像没生效。四条语义：内容没变不入栈、撤销后新改动清空重做栈、超出 100 步丢最老的一步（不是停止记录）、`LoadRoster` / `ApplyConfig` 整段挂起登记并在末尾清栈。界面：顶栏标题右侧「↶ 撤销 / ↷ 重做」，`Ctrl+Z` 撤销、`Ctrl+Y` 与 `Ctrl+Shift+Z` 重做，焦点在 `TextBox` 里时让给文本框自己的撤销。新增走查参数 `--seatgrid-shot <png> undo|redo`，测试 624 → 642 项。上一版 `1.32.0`（2026-11-17 归位考场导出修复与座号，Minor 晋级：导出新增座号这一输出内容。**缺陷本体是「导出走的是日常布局」**——界面网格读 `CurrentLayout`（考场下返回 `_examPlan.Layout`），导出读 `ToSettings()`（组 / 排 / 列取自日常滑块），再被 `SeatChartSettings.Normalize` 把 `Columns` 夹到 4；30 人考场 6 列 × 5 排被切成 4 列 × N 排，后排学生整片消失且没有任何座号，PNG / PDF / Excel / PPTX 四条导出一起坏。修法是给 `SeatChartSettings` 加 `LayoutOverride`：`GetLayout()` 直接返回它、根本不经过三个滑块字段，**`Normalize` 一个字没改**（夹取是给日常滑块用的不变量 A），考场只是绕开它——唯一的例外是画布那一行加了守卫，否则会拿考场尺寸去截日常画布（`ToSettings()` 交出去的是同一个引用，截掉就回不来）。`SeatChartDocument` 加 `SeatNumbers`（座位 → 1 基座号）、`DisplaySeat` 加 `SeatNumber`，空表时渲染 / Excel / PPTX 一律不画，日常导出零变化。座号画在格子左上角、取排标题的灰、字号随格子 7–11px 收缩；Excel 走富文本（姓名 + 换行小号座号），PPTX 加一个无填充小文本框。**考场导出带一张隐藏「座位设置」表并打上 `ExamSnapshot` 标记，「导出配置」在考场下直接禁用，「导入配置 / 导入名单」见到标记就如实拒绝**——考场是快照不是日常（第六节），宁可报错也不产出一份「导回来变成日常布局」的配置；约束表也不写，考场下锁定 / 互斥 / 结对 / 区域本就不生效。守门测试 `SeatGridExamExportTests` 24 项，含「修复前必红」的对照；测试 599 → 624 项。日常导出的 PNG / PDF 流 / xlsx 表内容 / pptx 幻灯片 XML 与修复前逐字节一致（仅 OOXML 的随机关系 ID 与时间戳不同）。新增走查参数 `--seatgrid-shot <png> exam-packed` 与 `--seatgrid-chart-export <路径> [excel]`。上一版 `1.31.0`（2026-11-16 织序导出设置两栏化 + 希沃全校课表预设，Minor 晋级：导出弹窗改为**左右两栏**——左设置、右实时预览，与竞名导出弹窗同构；`WeaveExportPreviewRenderer`（Core 层，SkiaSharp）三种画法：希沃版面、工作簿首页网格、单班 / 单教师 A4 课表，**PNG / PDF 必须走 `RenderSingle`**（导出的是当前班级/教师的 A4 课表，不是工作簿，预览要跟导出内容一致）；200ms 防抖 + 后台线程渲染，换图时立即 `Dispose` 旧位图。Excel 样式新增第三项**「希沃全校课表」**（`WeaveExportStyle.Seewo`）：把课表填进希沃官方「全校课表导入模板（行政班）」，供老师直接上传希沃平台。模板作为嵌入资源随程序走（`src/GradeWeave.Core/Assets/希沃全校课表模板.xlsx`），用户不必自己找模板。**核心约束是「只填进去、不做其他修改」**：`WeaveSeewoExporter` 不用 ClosedXML 重建工作簿，而是复制模板全部部件、**只替换 `sheet1.xml` 的 `<sheetData>` 内部与 `dimension` 末行**，新文本追加进 `sharedStrings.xml`，其余字节原样带过去——模板里除了单元格还有批注（D4「课程名称请控制在 20 字以内」、E4「老师姓名需与系统库匹配」）、VML 批注形状、外部链接与四个命名区域，希沃按列位与表名识别，重排过就不是那张表了。**不能用 XDocument 解析再序列化**：XML 往返会把 `&#10;` 实体化成真 CRLF、把 `<b/>` 写成 `<b />`，须知那段多行文本就悄悄变了；工作表与共享字符串表都在文本层做定位与拼接。正则替换串必须写 `${1}`——`$1` 紧跟数字会被当成组号 `$1149`（曾把 `<sst>` 整个吞掉）。模板版面：第 1 行标题、第 2 行填写须知（均黄底合并）、第 3–4 行表头（A3:B3 合并「班级」、C3:C4 纵向合并「课节（必填）」、D–Q 每天两列「课程名称 / 老师姓名或手机号」）、**第 5 行是示例行**（须知第 5 条要求正式填写时删掉，数据从第 5 行起写并覆盖它）。行结构 A 年级 / B 班级 / C 课节 / D–Q 七天两列；年级从班级名拆（`SplitGrade`：「九（1）班」→「九年级」、「高一8班」→「高一年级」、「2026级1班」→「2026级」），拆不出回落到工程年级名，都为空则 A 列留空并提醒。只写正课节次（课后服务与晚修不进模板，硬塞会让平台判成课节数不符）；没排课的格子留空；分层走班按层合成一格（「英语A层/B层」+「顾知远/叶临溪」），因为希沃的格位是「行政班这一节的课」。单元格样式号从**模板第 5 行（示例行）**读而不是表头——表头是合并标题样式，数据行是另一套。文本走 `sharedStrings` 不用 `inlineStr`（部分国产平台只认共享字符串表）。写入先落 `.tmp` 再 `File.Move`。表名校验不过就拒绝，绝不往不相干的表里填。`WeaveExcelExporter` 遇到 `Seewo` 直接抛错——它只会重排成自己的版式。预览只画周一到周五（七天会把每列压得看不清，导出文件仍保留七天）。模板入库前清掉了外部链接里指向希沃开发者本机的绝对路径。新增走查参数 `--gradeweave-export-shot <png> [excel|png|pdf] [software|reference|seewo]`，测试 115 → 130 项。上一版 `1.30.0`（2026-11-15 首次引导，Minor 晋级：新增 `src/TeachCraft.Core/Oobe`（`OobeState` / `OobeStateStore` / `OobePlanner`）与主窗口内的四页引导遮罩 `src/TeachCraft.App/Controls/OobeOverlayView.axaml`。**形态是窗口内遮罩不是第二个窗口**——再开一个会抢焦点、多一个任务栏条目，还要和单实例守卫、更新安装的关窗重启流程纠缠。状态落在 `%LOCALAPPDATA%\TeachCraft\Oobe\state.json`（原子写、损坏即当未完成、**不混进 `Update\state.json`**，否则「删除 state.json 重置更新提示」会误伤引导）。**显示判定只有一条**：用户从未结束过引导（既没看完也没跳过）才显示，结束过就再也不弹——不按版本号、不按天数、不按次数。曾经设计过「发布方推进通知版本就再告知一次」，已删除：那让引导在升级后又冒出来一次，与「只在第一次打开软件时显示」冲突。想重看有侧栏底部的「首次引导」入口。状态里也不再存版本字段，旧文件多出的 `LastNotifiedVersion` 会被忽略。**走查抑制只有一个判定点**：`showOobe = args.Any(a => a == "--oobe" || (a.StartsWith("--oobe-") && a != "--oobe-reset")) || !isWalkthrough`；走查路径一律注入 `NoopOobeStateStore`，绝不让一次截图把状态写进开发者本机。**焦点隔离靠禁用底层**（`AppContent.IsEnabled=false`）——Avalonia 没有焦点陷阱控件；遮罩根必须不透明，否则被置灰的侧栏会透出来像渲染 bug。`MainWindow` 顺带补上一直缺失的 `WindowFit.Clamp`（其余 16 个窗口都有；1366×768 @125% 下工作区只有约 590 DIP 高，而窗口固定 840）。`ToolDescription` 新增 `Name` 字段，引导的条目全部从目录取，`OobeContentTests.CatalogNamesMatchMainViewModel` 把两边钉死。新增走查参数 `--oobe` / `--oobe-step` / `--oobe-shot` / `--oobe-reset` / `--oobe-metrics` / `--oobe-interact` / `--oobe-motion` / `--oobe-keys` / `--oobe-width` / `--oobe-height`，测试 532 → 599 项。上一版 `1.24.0`（2026-11-09 更新通道可选，Minor 晋级：此前「正式 / 预览」由装的是哪一支隐式决定——正式版用户永远看不到预览版，预览版用户想回到稳定也没有显式选择。现在 `UpdateChannel` 做成可选：更新页「更新通道」一组单选，默认跟随安装形态（`UpdateChannelText.DefaultFor`），选择持久化到 state.json。稳定通道只给正式版；**预览版用户切稳定通道时允许降级**——正式版号低于同号预览，按「必须更高」筛会把唯一该给他的版本筛掉；且只给最高的那个正式版，不列出更低的（那不是选择，是版本考古）。预览通道下预览与正式都算，取严格更高的。通道贯穿 `IUpdateCheckService` / `UpdatePlanner` / `SelectCandidates`。测试 400 → 411 项。上一版 `1.23.3`（2026-11-09 Patch：原表高亮跨年级串色——分组表的班级列只写「6班」，块头才给年级，`HighlightGrouping` 早先只做 `className.EndsWith("6班")`，于是七 / 八 / 九年级的 6 班一起被刷成色块。改为逐行跟踪块头组别并把「6班」拼成全名再精确比对；真实秩序册核对九年级6班的高亮全部落在九年级块内（19 处）。测试 399 → 400 项。上一版 `1.23.2`（2026-11-09 Patch：团体项目队员名单在赛程 chip 里溢出——20*50 接力能报十几人，而 chip 是横向 `StackPanel`（不折行），长名单直接画到卡片外。改成纵向 `StackPanel` + `TextWrapping="Wrap"`，班级名 / 队员 / 道次各占一行，带队员的 chip 加 `Classes.team` 占满整行；ViewModel 不截断名单，完整名单同时在悬浮提示里。1120 与 1400 两种宽度实测正常。测试 397 → 399 项。上一版 `1.23.1`（2026-11-09 Patch：导出弹窗的「Excel 样式」在 PDF 模式下不消失——`OnSelectedFormatChanged` 逐条手写 `OnPropertyChanged` 时漏了 `ShowStyleChoice`，切到 PDF 后样式区仍在，点过样式才消失（那次点击触发了自己的通知），切回 Excel 又再也不出现。改为 `RaiseDerivedProperties()` 一处集中通知所有派生属性，格式 / 版式 / 对象三个入口都走它。测试 395 → 397 项，新增两条回归（含复现用户报告的操作顺序，去掉修复后确实变红）。上一版 `1.23.0`（2026-11-09 竞名团体项目补队员，Minor 晋级：秩序册只按班列团体项目、不点名队员，赛程里一直显示「4x100 接力队」这种占位；现在导入过报名表就用报名表里勾了该项目的人替换成真实队员。`MeetEntry.TeamMembers` / `TeamMembersFromRegistration` 承载这件事，空串 = 只知道「某班有队」，非空 = 知道具体是谁；按姓名匹配，**匹配不到就跳过，绝不把项目挂到错的人头上**。`MeetScheduleBuilder.ApplyTeamMembers` 只补团体条目、不整表重算（`RefreshEntries` 会重建整表，把秩序册带来的组次 / 道次抹掉）。报名表后到时补一次；再导入秩序册时 `CarryOverTeamMembers` 按姓名沿用旧勾选并在状态栏报告「沿用报名表的 N 条团体报名」。赛程 chip、Excel / PDF / 卡片版的「参赛人」列统一走 `ClassEntryLine.AthleteLabel`。测试 390 → 395 项。上一版 `1.22.0`（2026-11-09 竞名原表高亮、统一导入入口与长任务遮罩，Minor 晋级：Excel 新增「原表高亮」样式（`MeetOriginalWorkbookHighlighter`）——复制导入的原工作簿，只在「代表队名单」的目标班块与「项目分组表」的该班单元格涂底色，其余内容 / 列宽 / 合并单元格 / 隐藏表 / 下拉校验一律不动，原文件只读不写，另加一张「高亮说明」页；缺原文件或找不到该班时如实报错。`MeetProject.SourcePath` 记录导入来源供其当底板。顶栏「导入赛程」并入「导入秩序册」：`OrderBookImporter.DetectKind` 读表名判断秩序册（整本替换）还是只有赛程（只换赛程、保留班级），认不出就明说。长任务遮罩 `BeginBusy` / `EndBusy`：400ms 后仍未完成才亮（小文件不闪），写明在做什么、大概多久、「处理期间请勿关闭窗口」；导入 / 导出 / 保存 / 读名单全部接入，失败路径也必须收尾。新增走查参数 `--meetroster-busy` 与 `--meetroster-export-style original`，测试 376 → 390 项。上一版 `1.21.0`（2026-11-09 竞名导出预览与赛程卡版式，Minor 晋级：导出设置弹窗改成左右两栏——左边设置、右边实时预览，`MeetExportPreviewRenderer` 现场画缩略图（PDF 首页按真实分页规则取前若干卡 / 表格，Excel 画前几行网格），改设置即刷新且不落盘。新增 `MeetPdfLayout`：`Handbook`（原纸墨表格）与 `Card`（照软件内赛程页「仅显示该班选手」的观感：每个时段一张浅色圆角卡，左侧大号时间、彩色分类徽标、项目名与小 chip、右侧人次、下方选手 chip 流；单班导出 chip 不重复写班级名，全部班级导出才带前缀）。卡片版按时段装箱而不是整天——一天塞不下时把下午挪到下一页，卡片不会被劈开；日期标题只在当天第一次落位时计一次高度（首版按时段重复计算，每页空掉大半）。卡片版对 60 班秩序册是 5 页 / 1.4MB，表格版 83 页 / 13MB。卡片版只画赛程，弹窗里「包含内容」勾选框随之置灰并写明原因。新增走查参数 `--meetroster-export-layout handbook|card`，测试 365 → 376 项。上一版 `1.20.0`（2026-11-09 竞名接入秩序册与导出参赛手册，Minor 晋级：新增 `src/MeetRoster.Core/Import/OrderBookImporter.cs`，一份工作簿同时读「代表队名单 + 竞赛日程 + 项目分组表」并合成同一个 `MeetProject`——号码布、领队 / 教练 / 工作人员进模型，`FieldValue` 把被 Excel 压成科学计数法的手机号还原（`1.7876832726E10` → `17876832726`）；团体项目记在 `MeetClass.TeamEvents` 而不是每个运动员的团体格，因为秩序册按班列队并不点名队员，伪造到个人会让报名表显示「全班都跑接力」并虚增项次。同时修掉赛程导入的真实缺陷：分组表按每页 8 道排版，一组跨页时续页会重新给一行道次，旧实现只读第一页——真实秩序册里 34 人的 800 米只读到 16 人，`ReadIndividualGroupPages` 现在循环读到块结束（女子 800 米 34/33/23 人、男子 1000 米 39/38/36 人，与日程表人数列一致）。新增 `MeetWorkbookExporter`（班级报名 / 单项名单 / 赛程 / 班级汇总四页，单班导出整行加底色）与 `MeetPdfExporter`（纸墨版式 A4，SkiaSharp 画 1.8 倍 PNG 再嵌 QuestPDF；长表格按行切开并重复表头，续页标题标「（续）」——首版整表不可切，60 个班的秩序册有一半名单被画到页外）；赛程页新增「仅显示该班选手」；赛程日期新增「全部」（排在第一，把每个比赛日各自分上午 / 下午接排，每天第一组上方画日期小标题，右侧统计标题改为「全部项目」；合成项只在视图层，不进 `MeetProject.Days`，导出的赛程仍按真实比赛日分节）；顶栏「导出…」打开 `MeetExportOptionsWindow` 统一设置（导出对象 / 格式 / 分页内容 / 文件名），新增 `--meetroster-orderbook` / `--meetroster-only-class` / `--meetroster-export-dialog` 走查参数，测试 336 → 365 项。上一版 `1.19.0`（2026-11-08 绿色版支持自替换，Minor 晋级：此前便携 zip 用户点「下载并安装」只得到一句「请解压到另一个文件夹再手动替换」——把机械活推给老师。现在 `InstallerAction.ReplacePortable` + `PortableUpdater` 由独立助手进程在旧进程退出后解压覆盖并重启：运行中的映像不能自我替换是 Windows 硬限制，但助手是另一个进程，不受此限。三条约束——只覆盖包内文件（用户放在同目录的配置/脚本一律不动）、先备份再覆盖、失败回滚；助手把结果写 `last-replace.txt`，新版本启动时读取并如实提示，否则失败会表现成「点了更新，程序关了再没回来」。macOS / Linux 仍给指引（Gatekeeper 与可执行权限必须人工确认）。测试 326 → 336 项。上一版 `1.18.1`（2026-11-08 Patch：镜像同步是分步的——版本先登记、包后到位，`SectlMirrorFeed` 不再把「有版本但零资产」的条目过滤掉，否则用户会被谎报「已是最新」；`UpdatePlanner` 改为如实报告「已在镜像上登记，但安装包还没同步完成」。上一版 `1.18.0`（2026-11-08 新增 SECTL 镜像更新源，按 Minor 晋级：`src/TeachCraft.Core/Updating/SectlMirrorFeed.cs` 把镜像 `/distribution` 响应归一化成与 GitHub 相同的 `ReleaseEntry`，下游选择逻辑（`SelectCandidates` / `UpdateAssetSelector`）完全复用、不另写一套；`MirroringUpdateCheckService` 负责「GitHub 为主、失败回退镜像」，手动选源时不回退；镜像包的校验清单取自镜像同版本目录下的 `SHA256SUMS`，与 GitHub 路径的清单同源同哈希；新增 `--check-update mirror` 走查通道，测试 303 → 326 项）。`1.17.0`（2026-11-03 检查更新：新增 `src/TeachCraft.Core/Updating`，从 GitHub Releases 读取版本号，按构建身份与安装形态挑出唯一该下载的资产，下载后由 App 层决定何时关窗并运行安装包；失败话术集中在 `UpdateErrorText`。联网只发生在「检查更新」这一个动作上，不携带本机标识（本地优先条款同步改写）。发布侧：`Directory.Build.props` 关闭 `IncludeSourceRevisionInInformationalVersion`，改由 CI 注入形如 `1.17.0-preview.142.sc.win-x64` 的身份串；Inno 安装包写入身份记录供识别安装形态；预览版保留在 Releases、不再自动转草稿。TeachCraft.Tests 93 → 326 项，按 Minor 晋级；同版另有 Minor 级的座位表导回：「导出 Excel」附隐藏「座位设置」表，可从「导入名单」或「导入配置」导回继续修改，旧版导出按名单页还原座位；以及 Patch 级修复：全局纸墨滑块改用 `Slider:horizontal` 才真正生效，此前不带伪类的写法压不过 Fluent 主题、滑块一直是默认粗钮；归位右侧设置面板重排为单卡分节；学生视角改为真正的 180° 旋转，此前只倒组序不倒组内列序，同桌左右对调；导入配置后视角按钮与滑块数值同步刷新；名单识别「学号」优先于「序号」，未排座的学生不再被写成 1 组 1 排 1 列；工具箱「分晓」常备卡改为与其他常备卡同款的行卡，原卡的成绩条预览与「项目详情」收进点开后的说明区；「检查更新」入口从状态栏移到侧栏底部，点开是独立的「检查更新」页（`src/TeachCraft.App/Views/UpdatePanelView.axaml`，与工具／项目面板共用同一个抽屉、三者互斥）：分「当前安装 / 检查结果 / 联网与隐私」三节，摆出构建身份、上次检查时间与失败原因，走查参数为 `--panel update`）。`1.16.0`（2026-11-01 工具箱「工具说明」展开：10 件工具卡与 5 件常备卡默认只显示短描述，点卡片或卡片标题才展开完整说明（一句话定位 + 功能要点 chip + 输入 / 输出 / 处理方式 + 三条功能摘要），展开 / 收起为 260ms `EaseOutCubic` 的高度揭示（`src/TeachCraft.App/Controls/ExpandReveal.cs`，系统减弱动效时直接落位）；文案单点维护在 `ToolDescriptionCatalog` 并同进搜索命中面；工具卡从 `Button` 改为 `Border.tool-card`，双列改用 `WrapPanel` + `ColumnWidthConverter` 以免同位等高留白；展开后按最终 `Bounds` 做最少必要滚动。新增 `--expand` / `--width` / `--shot-metrics` / `--expand-anim` / `--interact` / `--title-click` / `--expand-visual` / `--app-interact` / `--reduced-motion` / `--repeat-toggle` 走查参数；同轮修掉四个真实缺陷——常备卡状态写成 static 导致跨实例共享、`ExpandReveal` 停秒表使收起卡在 1.000、工具卡漏 `Classes.expanded` 且常备卡局部 `Background`/`BorderBrush` 压过样式、标题色被全局 `TextBlock` 样式压住；滚动定时器改为复用单实例，测试 78 → 93 项，按 Minor 晋级）。`1.15.0`（2026-10-28 织序导出改为统一设置弹窗：可选 Excel / PNG / PDF、Excel 双样式（软件自带 / 参考校）、按分页勾选总表 / 分工表 / 教师职位表 / 班级课表 / 教师课表 / 冲突页；新增教师职位安排表与横向「参考校样式」课程总表，按 Minor 晋级）。`1.14.1`（2026-10-27 竞名 UI 地毯式审查修复，Patch：报名表在 1120 最小窗口下不再横向溢出（列宽改 `30,106,78,1*,1.1*,28` 比例列、去掉 `MinWidth=860`），表头移出滚动区常驻；导入替换报名表、替换赛程、导出覆盖同名文件、删除有内容的行、关窗共 5 处补 `ConfirmDialog` 后果说明；三处 `catch` 改走 `ErrorText`；报名体检面板常驻并区分「结构完整/N 项需处理」且不再丢弃提醒类问题；性别切换自动摘除新性别不存在的项目并在状态栏说明，导入/校验同步补「女子没有 1000 米」规则；`Button.ghost` 补 `:pressed/:disabled`、`section-tab/day-card/filter-chip` 补按下态，tab 选中加底部 Accent 指示条；正文字号下限抬到 10、装饰英文小标 9，`#FCFAF4/#F7F4EB/#FBF3EC/#FBE4D7/#E7E3D8` 收敛为 `WashBrush/PinnedBrush/WashHoverBrush/AccentWashBrush/LineBrush` 令牌（新增于 `App.axaml`）；赛程「趣味」并入「团体」筛选与配色、未高亮条目透明度 0.38→0.7、`6 人`改`6 人次`、右侧概览补空态、日期列表可滚动；状态栏加状态圆点与 tooltip、成功/中性/错误三色；导出按钮按是否有原始报名表启用；性别下拉与项目标签在行内垂直居中（补回 `ComboBox.cell` 基样式、标签容器 `VerticalAlignment=Center`），新增窄窗形态（窗口 <1240 收起右侧概览，1120 下三枚团体标签单行、行高回到 41px），新增顶栏「保存」（写回原始报名表，先备份 `.bak`，Ctrl+S）与「另存为…」、`IsDirty` 未保存标记；报名体检改为实时校验单源，导入时的历史提示独立成「导入时的 N 条提示 + 知道了」并在首次编辑时自动收起（修复「解决冲突后提示不消失」）；新增 `--meetroster-scale` / `--meetroster-metrics` / `--meetroster-remove` 走查参数，测试 40 → 41 项）。`1.14.0`（2026-10-27 竞名报名表项目格改为「只显示已报项目」：团体项目与比赛项目都按已报数量渲染标签——没报只留一个「选择项目」占位框、报一个只显示一个、最多三个；团体三格（T1/T2/T3）合并为一列多选标签，点任一标签打开多选弹窗（上限 3 项、实时显示「本班已有 N 人报此项目」），写回按顺序填补空位；`20*50米男女混合接力` 与 `一分钟跳绳` 恢复为可选团体项目（导入不再忽略、回写不再丢弃、校验不再提示「现场出人」，改为不在模板列表才提示），并补团体项目重复填写校验；标签文字统一垂直居中（`Button.event-cell` 加 `VerticalContentAlignment` 与 `TextBlock` 居中）；合成示例数据补 1/2/3 项差异，新增 `--meetroster-dialog-slot` 走查参数，按 Minor 晋级）。`1.13.0`（2026-10-27 归位自动排座新增同桌性别弹窗：点「自动排座」先在弹窗里选「异性同桌 · 男女交替」或「同性同桌 · 男男/女女成对」，Core 新增 `ArrangeMode.GenderPair` 与下拉「同性同桌」项（男男/女女两两成对、未知排末尾、一侧用尽另一侧连排），选名单顺序/随机/按身高/按视力时弹窗补「按当前方式」直达；按 Minor 晋级）。`1.12.1`（2026-10-27 UI 手感第二轮，补记版本：改动落在 1.12.0 之后的提交里、最终随 1.13.0 发布，Patch：归位拖拽按 Apple 指数衰减投影（`project(v, 0.998)`，`src/Shared/DragPhysics.cs`）预测落点、幽灵 120–200ms 临界阻尼飞向目标格中心、出界 0.55 橡皮筋并保留「拖出即取消」；织序拖拽浮层移出裁剪层到窗口根并用同套物理，修复 `OnGridPointerMoved` 只放行 Candidate 导致幽灵不跟手的既有缺陷；归位滑块经 60 帧压测（130 人名单 / 96 座 / 96 人入座，尺寸刷新 p95 3.1–3.4ms、BuildGrid p95 3.1ms，均低于 8ms 阈值）改回拖动中合并重建（`Dispatcher.Post(Background)` + `_rebuildQueued`），新增 `--slider-bench` 取证通道；新增 `src/Shared/MotionPreference.cs`（Windows SPI_GETCLIENTAREAANIMATION / macOS NSWorkspace，Linux 暂 false），reduced 时抽屉纯 120ms 淡入、忙碌旋转改不确定进度条、入场缩放取消。`1.12.0`（2026-10-27 新增「竞名 · MeetRoster」：第五件常备应用，落地报名界面、模板字段（参赛单位、编码、领队与体育老师、姓名 / 性别 / 团体 1–3 / 比赛 1–2）、多班报名表导入与按班回写、往届“竞赛日程 + 项目分组表”导入与班级高亮；赛程自动生成规则留待后续）。`1.11.2`（2026-10-27 UI 一致性第一批修复：新增 `src/Shared/` 共享 UI 工具——`WindowFit` 按 `Screen.Scaling` 把窗口钳进工作区、`ErrorText` 异常文案网关、`ConfirmDialog` 统一确认弹窗；三个课堂应用最小尺寸降到 1120×680 并全窗口接入钳制，NewProject 放开缩放；织序研究/走班/编排三个课表网格补双向滚动；归位删学生/清座位/重导入名单与配置/关窗、分晓替换考试、织序切换工程补后果说明式确认；织序分工保存改为校验前置（不再留下半成品工程）；Muted 统一 `#6F6A5E`、错误色改 `#A34A36` 并清理 `#9C9585/#A29C8C/#8E8776/#B0AA9C` 散值；四处导出补 `ShowOverwritePrompt`、工具箱运行按钮补重入守卫、两处拖拽尊重抓取偏移与真实尺寸，按 Patch 晋级）。`1.11.1`（2026-10-27 内存与预览管线微调：归位预览改按 1600 宽解码并在关窗删除临时图（最大布局从 84MB 峰值降到约 8MB）、临时目录兜底清理一天前的残留、时序预览从 2.0 倍降到 1.25 倍（常驻 21MB → 约 8MB）、新增 `runtimeconfig.template.json` 写入 `System.GC.ConserveMemory=9`；实测空载工作集 270MB → 247MB，按 Patch 晋级）。`1.11.0`（2026-10-27 织序「教师分工」改为先建教师名单再分工：名单视角维护姓名 + 学科并支持批量粘贴；班级 / 教师 / 学科视角从名单下拉选人，按学科过滤；分工体检汇总待补与教师负载。五种视角共享同一份草稿，按 Minor 晋级）。`1.10.2`（2026-10-27 测试资产隐私硬化：两份智学网 `.xls` fixture 在字节层擦除全部残留真实姓名 / 考号 / 校名，覆盖 SST 奇对齐孤儿条目与「姓名_成绩单」属主标题两种此前不可见形态；dry 复扫零命中、NPOI 金丝雀与全量测试结果不变，按 Patch 晋级）。`1.10.1`（2026-10-27 全链路性能修复：织序并发生成门禁与自动保存版本守卫、PDF 转图片批量加载与流式编码、织序课表索引与格子复用、时序预览防抖/取消/后台解码、分晓导入单次物化与 O(N) 排名、归位滑块松手提交与渲染缓存、R2R 实测自包含 +115MB / 框架依赖 +62MB 体积代价过高未启用，按 Patch 晋级）。`1.10.0` 为织序新增「冲突聚合台」与锁课：冲突按类型聚合、可按班级 / 教师 / 关键词筛选并一键定位；课程格可锁定，重新生成时保留，拖拽移动自动锁定，按 Minor 晋级。`1.9.1` 为织序走班组教师下拉只显示当前学科任课教师，并按分工带入任课班教室。`1.9.0` 为织序新增自动保存：每 2 秒防抖保存、关闭前落盘、当前工程原子写回、无路径时写本机恢复文件并可恢复。`1.8.0` 为织序「作息时间表」步骤与确认门槛；`1.7.0` 为织序顶栏「教师分工」入口、新走班组默认自动安排与手动放课；`1.6.0` 为织序「全新制作」、走班自动安排与 `.xls` 导入；`1.5.0` 为织序「临时活动」与 PDF 依赖修复；`1.4.0` 为「织序 · GradeWeave」首版；`1.3.0` 为时序作息模板与演示数据合成化，`1.2.1` 修复归位空名单时的蒙层残影（Patch），`1.2.0` 新增「时序 · ClassBlock」，`1.1.0` 是归位上线版本，`1.0.0` 是首个完整工具箱版本。

---

## 三、CI/CD 契约

工作流：`.github/workflows/release-draft.yml`

1. **测试**：`windows-latest` 上依次运行 `dotnet test tests/GradeStrip.Tests/GradeStrip.Tests.csproj -c Release`（108 项，分晓导入 / 报表 / 导出回归 / Core 导出守卫）、`dotnet test tests/TeachCraft.Tests/TeachCraft.Tests.csproj -c Release`（875 项，工具箱、时序模板与日块右界、竞名报名/赛程/秩序册/名单导入导出端到端与导出设置、工具说明展开、检查更新页与镜像回退与下载速度、发布身份与版本脚本、归位考场导出与座号、撤销重做与考场快照交叉、清空画布回归）与 `dotnet test tests/GradeWeave.Tests/GradeWeave.Tests.csproj -c Release`（168 项，织序自动保存 / 作息时间表 / 导入与拖拽确认 / 编排 / 活动 / 走班与孤儿课 / 手动放课 / 锁课 / 冲突聚合台 / 教师名单与分工五视角 / 导出设置与三样式 / 希沃模板导出 / xls 导入 / 导出 / 工程往返 / 岗位匹配回归），失败则阻止发布。
2. **构建矩阵**：7 个 RID × 2 种部署模式 = 14 个包。

| RID | 运行器 | 产物 |
| :--- | :--- | :--- |
| `win-x86` / `win-x64` / `win-arm64` | windows-latest | `.zip` + `*-Setup.exe`（Inno Setup） |
| `osx-x64` / `osx-arm64` | macos-latest | `.app` + `.dmg` + `.zip`（ad-hoc 签名） |
| `linux-x64` / `linux-arm64` | windows-latest（交叉发布） | `.zip` |

- **`linux-arm` 不参与矩阵**：QuestPDF 无该架构原生库。若未来要支持，必须先替换 PDF 排版引擎或做功能降级，不能在缺少原生库时宣称支持。
- 部署模式：`self-contained`（`sc`，内嵌运行时）与 `framework-dependent`（`fdd`）。
- 单文件发布在 Windows / Linux 使用 `PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true`；macOS 按 Avalonia 官方建议只使用 `PublishSingleFile=true`，不启用 native 自解压。
- 发布前删除 `*.pdb`，避免 80MB+ 的 Skia PDB 进入压缩包与签名流程。
- 发布目标：softprops/action-gh-release；资产名格式 `TeachCraft-{VERSION_TAG}-{rid}-{sc|fdd}.{zip|dmg}`、`TeachCraft-{VERSION_TAG}-{rid}-{sc|fdd}-Setup.exe`。

### 本地等价命令

```powershell
dotnet test tests/TeachCraft.Tests/TeachCraft.Tests.csproj -c Release

dotnet publish src/TeachCraft.App/TeachCraft.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist/TeachCraft-win-x64-sc

./build-installer.ps1 -Rid win-x64 -Version v1.0.0 -Deployment self-contained
```

macOS：

```bash
chmod +x bundle/macos/build-dmg.sh
./bundle/macos/build-dmg.sh osx-arm64 v1.0.0 self-contained
```

---

## 四、仓库结构

```
TeachCraft.slnx
Directory.Build.props        # 版本号唯一来源
src/TeachCraft.App           # Avalonia 桌面端（工具箱壳 + 五只常备应用入口）
src/TeachCraft.Core          # 图片 / PDF 处理引擎
src/SeatGrid.App             # 归位座位表窗口
src/SeatGrid.Core            # 名单识别、排座、PNG/PDF/Excel 导出
src/ClassBlock.App           # 时序课程表窗口
src/ClassBlock.Core          # 总表识别、班级课程提取、PNG/PDF 渲染
src/MeetRoster.App           # 竞名运动会报名与赛程窗口
src/MeetRoster.Core          # 报名模型、项目目录、示例排程与合成演示数据
src/GradeWeave.App           # 织序课表编排窗口
src/GradeWeave.Core          # 工作簿导入、自动编排、冲突分析、Excel/PNG/PDF 导出
src/GradeStrip.App           # 分晓成绩条窗口（Library，同进程内嵌）
src/GradeStrip.Core          # 成绩导入、分析、xlsx 导出
tests/GradeStrip.Tests       # xUnit 测试（102 项，分晓导入 / 报表 / 导出回归 + 合成 fixtures）
tests/GradeWeave.Tests       # xUnit 测试（133 项，织序自动保存 / 作息时间表 / 导入 / 编排 / 活动 / 走班 / 手动放课 / 锁课 / 冲突聚合台 / 教师名单与分工五视角 / 导出设置与三样式 / 希沃模板导出 / xls 导入 / 导出 / 工程往返 / 岗位匹配回归 + 合成 fixtures）
tests/TeachCraft.Tests       # xUnit 测试（714 项，工具箱、时序模板、竞名报名/赛程/秩序册/名单导入导出端到端与导出设置、工具说明展开与检查更新页、归位考场导出与座号、撤销重做与清空画布回归）
installer                    # Inno Setup 脚本与中文语言文件
bundle/macos                 # Info.plist / Entitlements / DMG 脚本
scripts/version.ps1          # 版本号计算
.github/workflows            # 跨平台构建与发布
.memory                      # 项目记忆（决策、踩坑、架构）
design-preview               # 已评审的 HTML 视觉稿（只读参考）
```

---

## 五、分晓（GradeStrip）集成约定

- 分晓以 **一等项目 + 程序集内嵌** 的方式进入工具箱：`src/GradeStrip.App`、`src/GradeStrip.Core` 与归位、时序并列，直接在本仓库开发。点击「打开应用」在 **同一进程** 里创建 `GradeStrip.App.Views.MainWindow`，不要改成启动外部 exe。
- `GradeStrip.App` 的 `OutputType` 必须是 `Library`：它只作为窗口模块使用；若改回 `WinExe`，framework-dependent 发布会报 `NETSDK1151`（自包含可执行文件不能被非自包含可执行文件引用）。
- 命名空间保持 `GradeStrip.App` / `GradeStrip.Core` 不变（与 `SeatGrid.*` 同构），不要为了统一品牌做全局重命名。
- `AvaloniaUI.DiagnosticsSupport` 包引用必须 **无条件引入**（只加 `PrivateAssets=All`），不能按 Configuration 条件排除——否则 Release 还原后 `Program.cs` 的 `WithDeveloperTools()` 会报 CS1061。
- 历史渊源：代码源自 `Evan0148/GradeStrip`（上游 2026-10-24 main，含概览报表管线与成绩导入兼容修复）。2026-10-24 起并入本仓库直接开发，上游仓库转为独立应用、不再同步；若上游未来有新特性，手动 cherry-pick。
- 若引入 App 级 `StaticResource`（笔刷、字体、样式），必须同步进 `src/TeachCraft.App/App.axaml`，否则 XAML 会在运行时找不到资源。
- 发布目录里的 `GradeStrip.App.exe` / `GradeStrip.App.runtimeconfig.json` 由 `RemoveGradeStripAppHost` target 兜底删除，只保留 DLL。

---

## 六、归位（SeatGrid）集成约定

- 归位以独立 Avalonia 窗口运行，但**由工具箱同进程打开**（`SeatGridWindow`），单实例复用；不要改成外部 exe。
- 数据管线固定为：`RosterImporter` → `SeatingEngine` → `SeatChartComposer` → `SeatChartRenderer` / `SeatChartExcelExporter` / `SeatGridConfigIO`。界面、PNG、PDF、Excel 共用同一个 `SeatChartDocument`，不得各自写一套布局。
- **同桌与邻座语义单源**（1.25.0 起）：`SeatChartLayout.DeskmateOf / AreDeskmates`（同组同排、同一对双人桌，不跨组不跨排，`Columns=1` 无同桌、`Columns=3` 第 3 列落单）与 `NeighborsOf / AreAdjacent`（前后左右，不跨组、不含对角）。`GenderPair` 必须用它成对，不得再依赖 `FillOrder` 的列循环。
- **几何单源**（1.26.0 起）：`SeatChartGeometry.Measure` 是格子大小与全部间距的唯一来源，界面、PNG / PDF、Excel 一律读它，**不得再各写一套**（此前 XAML `Spacing`、`UpdatePodium`、Renderer 三份副本互不相等）。偏移数组按显示序生成；阶梯深度按物理排号算；过道挂在它后面那一段上，最后一段也要算。
- **教室画布**（1.26.0 起）：`SeatChartCanvas` 不可变（所有变换返回新实例，`Settings.Clone()` 的浅拷贝靠它保证安全），用 `FrozenSet` 而非 `HashSet`（后台渲染线程并发读）。`SeatChartLayout` 加可选 `Canvas` 参数；`FillOrder` 过滤过道，`AllSeats` 不过滤。门窗用「边缘枚举 + 0–1 比例」，UI 只提供 Front / Back；**Excel 端门窗只能落成文字**（ClosedXML 无形状 API），与 PNG 的门洞线段形态不同是刻意降级。讲台倾角不做、位置不是自由参数。**门窗编辑尚未落地**：「显示门窗」复选框置灰并写明「编辑将在后续版本提供」，画布编辑器只刷过道；考场模式下「编辑画布…」与「清空画布」一律置灰（`CanEditCanvas => IsIdle && !IsExamMode`），拖拽悬停过道格不再画落点高亮、`NearestSeatTo` 跳过过道格。
- **考务套件**（1.27.0 起）：考场模式是**独立快照**（`ExamPlanner`），不改日常 `_assignments`、不改 `ViewMode`；`CurrentLayout` 在考场模式下直接返回 `_examPlan.Layout`，**不得改日常组/排/列滑块值**（否则界面显示与实际不符）。考场下锁定 / 互斥 / 结对 / 区域不生效并明说；返回日常从内存快照还原。桌贴沿用「Skia 画 PNG → QuestPDF 嵌 A4」，不做条码。周轮转 `RotateGroups` 只换 Group，与「整班平移」严格区分，考场视图下禁用。**导出必须走考场布局与座号**（1.32.0）：`ComposeDocument` 在 `IsExamMode` 下给 `SeatChartSettings.LayoutOverride` 赋 `_examPlan.Layout` 并把 `SeatNumbers` 传进 `Compose`——`GetLayout()` 有显式布局时直接返回它，绕开 `Normalize` 对 `Columns` 的 4 列夹取（考场上限是 8）；**不得改回 `Compose(ToSettings(), …)`**，那会让 30 人考场按日常滑块切成 4 列、后排学生整片消失。座号只进 `SeatChartDocument.SeatNumbers` / `DisplaySeat.SeatNumber`，空表时四条导出一律不画，日常导出零变化。考场导出可带隐藏「座位设置」表，但必须写 `ExamSnapshot` 标记：`导出配置` 在考场下禁用，导入侧见到标记如实拒绝——考场是快照不是日常，绝不产出一份「导回来变成日常布局」的配置。
- **交互与格式**（1.29.0 起）：导师制是**纯软约束**（只进 `SoftCost`，绝不推翻锁定 / 区域 / 互斥 / 结对），组长是算出来的、不持久化。拼音索引在 `StudentViewModel` 构造时算一次，取不到读音原样保留。PPTX 导出必须是「母版 + 版式 + 主题」的最小结构（缺了 PowerPoint 报修复），座位顺序复用 `GroupOrder / ColumnOrder / RowOrder`，只画座位不含未排座学生。
- **空间变换**（1.28.0 起）：末尾增删只允许删末尾（`Row = 0` 是物理不变量，删中间排要重编号）；追加不动学生、删除必须报数并二次确认。镜像（`MirrorColumns` / `MirrorGroups`）是**数据变换**，与学生视角的展示旋转严格区分。`ShiftOnScreen` 目标格是过道时原地不动并报数。异形教室预设（`SeatCanvasPresets`）只产出普通 `SeatChartCanvas`，不引入「形状」概念；布局太小时返回 null。
- **撤销 / 重做**（1.33.0 起）：`SeatUndoStack` 存**整份快照**而不是命令对象。任何改数据的入口都必须经
  `SeatGridViewModel.RecordChange(label, action)`（窗口层用 `RecordWindowChange`）——不经过它，那次改动就撤不回去。
  **先拍照再改**；恢复期间 `_restoring` 为真不入栈，撤销 / 重做自身绝不能成为一步。快照只存数据不存 ViewModel：
  学生 `CopyKeepingId()`（普通 `Copy()` 换 Id 会让座位 / 约束错位），座位存坐标，恢复时按 Id 复用现有 ViewModel。
  **考场状态必须进快照**——只还原座位不还原 `IsExamMode`，撤销看起来就像没生效。
  四条语义：内容没变不入栈（滑块连发不能要按几十次）、撤销后新改动清空重做栈、超出 100 步丢最老的一步、
  `LoadRoster` / `ApplyConfig` 这种换数据的操作挂起登记并清栈。`Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z`，
  **焦点在 `TextBox` 里时让给文本框自己的撤销**。
- **排座约束**（1.25.0 起）：`SeatingEngine.Arrange(students, layout, mode, ArrangeConstraints)` 返回 `ArrangeResult`（座位 + `Issues` + 未排座学生）；旧的 3 参重载保留为薄包装。约束四步：固定点预占 → 约束感知贪心 → 局部修复（≤4 轮）→ 报告，不做通用 CSP。锁的是「学生-座位绑定」；手动移动 / 平移 / 整排整列对调遇锁定整体拒绝；`ClearSeats` 保留锁定；`BuildGrid` 丢弃越界座位必须报数（`!_interactiveLayoutEdit` 守卫）。排座说明进状态栏首条 + 右侧「排座约束」节 Expander，不新建窗口。
- 名单识别必须同时支持 **带表头** 与 **无表头** 的 xlsx / xls / csv；不得要求用户套用固定模板。识别字段：姓名、性别、学号、身高、视力、成绩、原小组、备注。
- 座位坐标统一用 `SeatPosition(Group, Row, Column)`；`Row=0` 永远表示靠近讲台的第一排。教师视角 / 学生视角只改变展示顺序，不改变物理座位。屏幕摆放顺序只由 `SeatChartComposer.GroupOrder / ColumnOrder / RowOrder` 给出，界面、PNG / PDF / Excel 与整班平移共用，不得各写一套；学生视角是教师视角整体旋转 180°，组序、组内列序、排序一起反转。
- 打印 PDF 采用「高分辨率 PNG 嵌入 A4」的方案（QuestPDF），不要改回 SkiaSharp 矢量 PDF：后者会把整个中文字体内嵌，单张座位表可达 23MB。
- 导出路径不得覆盖已导入的名单文件；UI 层必须做 `IsSamePath` 检查。
- 「导出 Excel」与「导出配置」共用 `SeatGridConfigIO.WriteSettingsSheet / WriteRosterSheet`：座位表附一张隐藏的「座位设置」（A1 为 `SeatGrid Config v1` 标记），名单页的组 / 排 / 列为 1 基物理座位，未排座的学生三列留空（不得写成 1/1/1）。「导入名单」先用 `SeatGridConfigIO.TryImport` 探测：是本软件导出的座位表或配置就整体还原、不重新自动排座，否则按普通名单识别。
- **约束持久化**（1.25.0 起）：锁定 / 互斥 / 结对 / 上期同桌 / 区域写进隐藏的「座位约束」表（12 列，学生按「名单序号 + 姓名」双校验定位——`Student.Id` 每次导入都重新生成，**绝不能当持久键**）。**Marker 仍为 `SeatGrid Config v1`**：判据是「旧版读者读到新文件会不会产出错误结果」，而不是「有没有新增字段」；升 v2 反而会让旧版掉进 `InferLegacyChartSettings` 误推布局。
- 新增第 11 件小工具或归位的新导出格式时，按 Minor 版本晋级，并同步 `.memory/09-归位座位表.md`。
- 归位的默认格子缩放为 85%（68px），保证 1440 宽窗口能完整显示 4 组 × 2 列；用户可在设置里改回 100%（80px）。

---

## 七、时序（ClassBlock）集成约定

- 时序以独立 Avalonia 窗口运行，由工具箱同进程打开（`ClassBlockWindow`），单实例复用；不要改成外部 exe。
- 数据管线固定为：`MasterScheduleImporter` → `TimetableBuilder` → `TimetableRenderer`。界面预览、导出 PNG、打印 PDF 共用同一个 `ClassTimetable`，不得各自写一套版面。
- 总表识别只接受 `.xlsx`；必须同时找到「星期一…星期五」「1–7 节」和班级行，才认定是课程表。`.xls` 提示用户另存为 xlsx，不静默失败。
- 每天按 10 列分组：7 节课 + 课后服务1 + 课后服务2 + 晚修。读取单元格必须先解析合并区域，否则合并的「培优辅差 / 阳光体育」会只对第一行生效。
- 辅助信息从同一份工作簿读取：班主任（分工表）、午学（含 12:00 的 5 天表头）、课服/晚修（班级行上方含课服1/2/晚修的子表头）、早读（含「早读」与语文/英语的备注）。缺表时写入警告，不阻断生成。
- 课程表行序与时间由作息模板驱动：模板行通过 Slot（第 N 节 / 早读 / 午学 / 课服1/2 / 晚修 / 静态）绑定总表数据。内置七 / 八 / 九三套预设；九年级预设 = 原固定行序快照（早读 → 1 → 2 → 大课间 → 3 → 4 → 午餐 → 午学 → 5 → 6 → 7 → 课服1 → 课服2 → 晚餐 → 新闻联播 → 晚修），七 / 八年级在第三节后插入朗读行。午餐、晚餐为整行红字；大课间、新闻联播合并星期列；晚修为两段时间的高行。
- 作息模板可在界面编辑器中复制内置、增删行、改时间后另存为自定义模板，存于 `%APPDATA%\TeachCraft\ClassBlock\templates`；导入总表后按识别年级自动套用该年级上次使用的模板，无记录时用内置预设。学校改作息只需改模板；新增模板行类型或 Slot 槽位才需要改 Core 并升 Minor，并同步 `.memory/10-时序课程表.md`。
- 主课默认只显示学科；开启「显示任课教师」后学科加粗、教师排第二行。午学、晚修始终显示教师姓名并补「老师」，含「详见附件」的说明原样保留。
- 五天课服内容一致才合并为一格；内容不同必须逐日显示，不能为了好看强行合并。
- 打印 PDF 继续用「高分辨率 PNG 嵌入 A4」（QuestPDF），不要改成矢量 PDF。
- 导出路径不得覆盖已导入的总表；UI 层必须做 `IsSamePath` 检查。
- 新增总表格式或课程表行时，按 Minor 版本晋级，并同步 `.memory/10-时序课程表.md`。

---

## 八、编码与设计准则

1. **语言**：界面中文，工具 ID 用等宽拉丁（`image-convert` 等）；异常消息面向老师，必须是可读中文。
2. **设计语言**：沿用分晓的 Anthropic 纸墨色板（`#FAF9F5` / `#141413` / `#D97757`）、Anthropic Serif + Mono 字体、1px 细边白卡、无阴影、0.12–0.32s 克制动效。
3. **本地优先**：不得加入遥测与账号体系。联网只允许两处，且都必须由用户明确开启：①「检查更新」读取版本号——GitHub Releases 或 SECTL 镜像；②「接入 SECTL 集控」——用户在设置里主动开启后，以设备身份与集控 Hub 保持长连接、上报班级与运行状态并接收指令。**集控默认关闭**，关闭时不建立任何常驻连接。任何新增联网行为都必须同时在设置里可见、可关，并在 `LocalFirstCopyTests` 里补一条守护。
4. **工具定义**：工具 ID 与顺序是稳定契约，变更需升 Minor 以上并同步 `.memory/03-功能与工具面板.md`。
5. **异步**：所有文件处理走 `Task.Run`，UI 线程只做选择与展示；`Progress<int>` 仅在长任务（PDF 转图片）上报进度。
6. **错误处理**：预期内失败抛 `ToolboxException`（中文、可操作）；未知异常统一包装为「处理失败：…」并写入状态栏。
7. **多文件顺序**：合并 / 图片合成必须允许用户上移、下移、移除；不要依赖选择器返回顺序。
8. **输出保护**：任何可能覆盖输入文件的路径必须在 Core 层拒绝。
9. **提交规范**：Conventional Commits（`feat:` / `fix:` / `chore:` / `docs:`），正文说明「为什么」。
10. **工具说明**：每件工具与常备应用的说明文案只在 `src/TeachCraft.App/ViewModels/ToolDescriptionCatalog.cs` 维护，界面一律绑定，不在 XAML 里另写一份；说明默认收起，点卡片或卡片标题才展开（`ExpandReveal`，260ms 高度揭示）。新增工具或常备应用必须同时补一条说明，否则卡片不会展开空白区——`ToolDescriptionTests` 会拦住漏写。

---

## 九、验证要求

- 提交前必须通过：`dotnet build TeachCraft.slnx -c Release` 与全部 xUnit 测试（GradeStrip.Tests 102 项 + TeachCraft.Tests 714 项 + GradeWeave.Tests 133 项）。
- 涉及图片 / PDF 引擎的改动，必须额外用真实文件跑一遍：格式转换、PDF 合并、Word→PDF、PDF 转图片。
- 涉及归位的改动，必须额外验证：带表头 / 无表头名单识别、配置 xlsx 往返、PNG / PDF / Excel 三种导出。
- 涉及时序的改动，必须额外验证：真实总表的年级/学期/月份/班级数识别、目标班级 7 节课与课服/午学/晚修提取、PNG 与 PDF 输出。
- 涉及织序的改动，必须额外验证：真实结构工作簿的导入、生成后无教师 / 班级冲突、教研与会议时段不被占用、分层走班同步、Excel / PNG / PDF 三种导出。
- 涉及教师分工的改动，必须额外验证：先建教师名单再分工的默认顺序、名单批量添加与未任课教师保存、班级 / 教师 / 学科 / 体检视角切换不丢草稿、下拉按学科过滤、删除教师前的任课 / 班主任 / 课表引用拦截。
- 涉及竞名的改动，必须额外验证：多班报名表导入、每项男女各 2 人 / 每人 2 个单项的超限提示仅在违规时显示、20*50 与一分钟跳绳的忽略且不进入体检、比赛项目列 0/1/2 个框的动态显示、按班回写的文件名与字段、往届赛程“竞赛日程 + 项目分组表”导入、班级高亮与 1120×680 布局；**秩序册导入须用真实文件核对名单人数与分组表人数一致（含跨页续页）、导出 Excel 四页与单班底色、导出 PDF 页数与跨页表头**。
- 发布前用 `--shot` / `--panel <tool-id>` / `--gradestrip-shot` / `--seatgrid-shot` / `--classblock-shot` / `--gradeweave-shot` / `--meetroster-shot` 做一次视觉走查（详见 `.memory/07-验证与测试资产.md`）。

---

## 十、数据隐私红线（最高优先级）

- **绝不能泄露参考数据**：真实学生姓名、考号、学校名、评语等可识别信息不得进入代码、测试、文档、演示数据、截图与提交信息；仓库公开托管，一经提交即永久公开。
- **测试必须使用生成的假数据**：fixture 与演示数据一律使用合成克隆（真名→合成名、考号→合成考号、校名→合成校名，成绩/名次/结构保留）或内存构造数据；新增 fixture 必须按同一做法合成，禁止把用户提供过的真实文件内容直接入库。
- **演示内容与截图同规则**：`StripPreview` 演示表、`Assets/Shots` 截图、`design-preview` 资产一律基于合成数据生成；数据更新后用上游分晓 `--shot` 重截。
- **身份替换对照表不进仓库**：真名→假名映射表仅存本机 `temp/`（已 gitignore）。
- **提交前自检**：diff 中出现真实姓名/考号/校名（含作为更长字符串的子串出现）即违规，立即停止提交并清理；合成名须选不含任何真实 token 的写法。

---

## 十一、织序（GradeWeave）集成约定

- 织序以独立 Avalonia 窗口运行，由工具箱同进程打开（`GradeWeave.App.GradeWeaveWindow`），单实例复用；不要改成外部 exe。
- 数据入口有三个：导入工作簿（`.xlsx` / `.xls`，`.xls` 经 NPOI 转内存工作簿后与 `.xlsx` 走同一解析管线）、打开 `.weave.json` 工程、「全新制作」空白工程（`NewProjectWindow` 配置学校 / 年级 / 班级数 / 学科课时，`AssignmentEditorWindow` 先建教师名单，再填班主任与任课教师）。空状态与顶栏都有「全新制作」入口；「教师分工」不进顶栏，作为编排流程的一环放在 05 步「编排对象」卡内，配任课 / 教师摘要并在任课为 0 时提示先补分工，「全新制作」完成后直接接上分工窗口。
- 教师分工编辑器（`AssignmentEditorWindow` / `AssignmentEditorViewModel`）固定为五种视角共享同一份草稿，并把**教师名单**设为默认第 1 步：名单视角维护教师姓名 + 学科，支持批量粘贴（每行「姓名,学科」），新增但未任课的教师也会在保存时写入 `Teacher` 档案；**班级视角**按班填班主任与各学科教师，教师格是名单下拉；**教师视角**选中一位教师后用「班级 × 学科」勾选矩阵指派 / 取消任课；**学科视角**按学科逐班选择教师，支持「填入空班 / 全部替换」；**分工体检**汇总待补与教师负载。班级 / 学科下拉按学科过滤名单，旧工程的跨学科任课保留为当前项；删除名单教师前必须解除任课、班主任与课表 / 走班 / 会议 / 活动引用。切换视角只能刷新派生视图，不得丢失或提前写回草稿；只有点「保存分工」才同步教师档案、按姓名重建任课关系、重算周课时并写回工程。新增视角或改变草稿 / 保存规则时按 Minor 升级并同步 `.memory/12-织序课表编排.md`。
- 数据管线固定为：`WeaveWorkbookImporter` → `TimetableService`（确认作息并编译节次）→ `WeaveScheduler` → `ConflictAnalyzer` → `WeaveExcelExporter` / `WeaveImageRenderer` / `WeavePdfExporter`（冲突报告走 `WeaveConflictReportExporter`）。界面预览与三种导出共用同一个 `WeaveProject` 与 `LessonPlacement`，不得各自写一套排课结果。
- 五步流程固定为：**确认作息时间表** → 导入年级工作簿（分工表 / 确认表 / 课程表 / 会议活动备注）并核对班级、学科、教师与课时 → 设定教研、走班、会议约束 → 检查冲突后导出。作息时间表未确认时 `WeaveScheduler.Generate` 必须抛出可读中文异常，顶栏「生成课表」与 05 步「重新生成」必须禁用；手动放课、拖拽与清除也必须拒绝。
- **作息时间表（`WeaveTimetable` / `TimetableRow` / `TimetableService`）**：一天的有序时段列表，行类型固定为 `Lesson / MorningReading / Break / NoonStudy / Lunch / Rest / EyeExercise / AfterSchool / EveningStudy / Routine`；只有 `Lesson` 编译为正课节次，`AfterSchool / EveningStudy` 编译为 `IsExtension=true` 的扩展节次，其余只作作息说明。`TimetableService.Validate` 必须拒绝空表 / 无正课 / 非法时间 / 起止倒置 / 时段重叠，并按开始时间给出排序提示；`Confirm` 把节次写入 `WeaveProject.Periods`、写入内容签名 `ConfirmedSignature`，逐行改动后签名自动失配，必须重新确认。确认后节次减少时，超出新正课范围的 `LessonPlacement` 要移除并如实报告；教研 / 会议 / 走班 / 活动引用越界只提示，不静默改动。内置七 / 八 / 九年级作息与「仅正课七节」预设只使用通用年级名，不得写入真实学校作息；编辑窗口为 `TimetableEditorWindow`，沿用 ClassIsland 的时段列表 + 详情 + 时间轴编排方式，确认才写回工程。新增行类型、内置预设或改变节次编译规则时按 Minor 晋级，并同步 `.memory/12-织序课表编排.md`。
- 分层走班分两种时段模式：**自动安排**（新建走班组默认；`IsFlexible`，不占固定时段，`PermutationScheduler.ResolveFlexibleGroup` 在生成时挑选所有参与班与各层教师都空闲、不撞教研会议且同科不超限的 `WeeklyHours` 个槽位，落位写入 `ResolvedSlots`）与**固定时段**（学校已排定时才选自选星期与节次）。自动安排一个槽位都找不到时整组退化为普通课并在消息里说明，不静默丢安排；部分落位时余量按普通课编排。固定组优先落位，自动组在固定组之后挑选。生成后状态栏必须报告各组实际时段与「N 个班同节走班」，班级格与总表用底色与「走班 · N 个班同节」标记并在悬浮提示里列出 A/B 层教师；`ConstraintRules.TryGetLayeredGroupAt` 必须同时识别固定区间与 `ResolvedSlots`。走班组教师下拉只列出当前学科的任课教师（`WeaveProject.TeachersOfSubject`），选中教师后优先把教室带成该教师在本学科任课的参与班，带入教室后也按分工表反查该班该学科教师，不得把全年级教师都列进下拉。
- 手动放课：班级视角 / 总表视角点击课程格打开 `LessonEditorWindow`，按「学科 · 任课教师」列出本班候选并显示应排 / 已排课时与教师本节冲突预判；点一行即放入或替换。`LessonPlacementService` 写入 `IsFixed=true` 的课，重新生成时保留；活动占位与走班时段不得手动放课；教师冲突、教研 / 会议占用、超课时只写警告交给 `ConflictAnalyzer`，不得静默改课。
- 锁课与冲突聚合台：05 步右侧冲突面板底部「打开冲突聚合台」打开非模态 `ConflictCenterWindow`，按 `ConflictKind` 分组并支持类型 / 班级 / 教师 / 关键词筛选；有星期与节次的冲突定位到课程格，无节次时定位到相关班级或教师，定位只读、不改课。课程格右键菜单、选中后按 `L`、表头「锁定本班 / 解锁本班」可切换 `IsFixed`；锁定课在重新生成时保留，拖拽移动与交换自动锁定两格。活动占位与走班时段不得锁定，仍由活动列表与 03 步走班组管理。
- 编排策略：每个班的教学时段视为排列，用「分散式初始分配 + 最小冲突局部搜索」消除教师冲突、教研/会议占用与同科每日超限；超过时限（`ScheduleOptions.TimeLimit`）降级返回已排结果并列出未排课程（`UnplacedLesson`），不得谎报“全部排完”。同一班级同一学科每天不超过 2 节（`MaxSameSubjectPerDay`）；主课优先上午。20 班 / 62 教师 / 694 节的真实规模回归必须保持可排满。
- 临时活动（`ActivityBlock`）分两种：`Overlay` 只在格子与导出里叠加「活动名 + 时间」，不改课程、课时与冲突；`Replace` 移除重叠节次的原课，写入 `LessonPlacement(IsActivity=true, IsFixed=true)` 固定占位，重排时必须保留该占位，被移除课时形成的缺口（课时不符 / 未排课）必须如实报告，不得静默补齐。时间通过 `ActivityResolver` 匹配 `PeriodSlot.TimeText` 的重叠节次（端点相接不算重叠）；作息时间不标准时可用 `Period` 手动指定。
- 活动占位格不可拖拽、不可右键清除；界面与导出必须显示活动名称与时间，活动不计入教师「已排 / 应排」周课时。
- 冲突类型固定为：教师冲突、班级冲突、教师不可用（教研 / 会议）、走班冲突、缺任课教师、课时不符、未排课程；每条冲突应尽量带星期与节次，导入数据缺节次时聚合台显示「位置未定位」并定位到相关班级或教师。
- 导出入口统一为 04 步顶栏「导出…」→ `ExportOptionsWindow`：格式 Excel / PNG / PDF；Excel 再选「软件自带样式」或「参考校样式」并勾选总表、分工表、教师职位表、班级课表、教师课表、冲突页。Excel 结构必须包含总表、分工表、教师职位表、班级课表、教师课表与冲突页；「参考校样式」按参考工作簿生成 `1-分工表`、`2-课程表`（横向 5 天 × 7 节 + 课后服务 2 列 + 晚修 1 列）与 `3-教师职位表`，宋体 / 楷体、黑色细框。打印 PDF 继续用「高分辨率 PNG 嵌入 A4」（QuestPDF），不要改成内嵌整套中文字体的矢量 PDF。
- 导出路径不得覆盖已导入的工作簿；UI 层必须做 `IsSamePath` 检查。首版只接受 `.xlsx`，`.xls` 提示另存为，不静默失败。
- **自动保存**：窗口每 2 秒检查一次工程快照，内容有变化才落盘；关闭窗口前同步补写一次，不丢最后几秒的改动。当前工程来自 / 已手动保存为 `.weave.json` 时，用 `WeaveProjectIO.SaveJson` 原子写回该文件；没有工程路径或写回失败时，写 `%LOCALAPPDATA%\TeachCraft\GradeWeave\autosave.weave.json` 与 `autosave.meta.json`，空状态显示「恢复上次自动保存」。自动保存绝不能写导入的 `.xlsx` / `.xls`；不得在仓库或上传服务中写快照。UI 状态栏必须显示「自动保存 · HH:mm:ss」或失败提示，不得静默失败。
- 本地优先：不得加入遥测、上传、账号体系；示例、fixture 与截图一律使用合成学校 / 教师 / 班级（示例中学、沈青禾、陆明川等虚构名），禁止引用真实课表。
- 新增导入表结构、约束类型或导出格式时，按 Minor 版本晋级，并同步 `.memory/12-织序课表编排.md`。

---

## 十二、竞名（MeetRoster）集成约定

- 竞名以独立 Avalonia 窗口运行，由工具箱同进程打开（`MeetRoster.App.MeetRosterWindow`），单实例复用；`MeetRoster.App` 保持 `Library`，不要改成外部 exe。
- 窗口默认必须是空工程：报名页显示「从导入报名表开始」，赛程页显示「尚未导入赛程」，不得自动载入合成示例数据。示例数据只允许在 `--meetroster-demo` 截图走查或测试中显式使用。
- **秩序册导入**（`MeetRoster.Core.Import.OrderBookImporter`）：一份工作簿同时读「代表队名单 + 竞赛日程 + 项目分组表」，合成同一个 `MeetProject`，顶栏「导入秩序册」入口，导入前需确认替换。团体项目记在 `MeetClass.TeamEvents`（班级层），不得写进每个运动员的团体格——秩序册按班列「第 1 组：10班 5班 …」并不点名队员。名单里的「例：张三」占位必须跳过并计入提醒；分组表里找不到的姓名 / 班级只提示不静默丢。
- 分组表按每页 8 道排版，一组跨页时续页会重新给一行道次：`ReadIndividualGroupPages` 必须循环读到块结束，不得只读第一页（真实秩序册里 34 人的 800 米曾只读到 16 人）。
- **导出**统一走顶栏「导出…」→ `MeetExportOptionsWindow`：导出对象（全部班级 / 某一个班）、格式（Excel / PDF）、PDF 版式（表格手册 / 赛程卡）、分页内容（报名名单 / 单项报名 / 参赛日程 / 参赛概览）与文件名；弹窗右侧是实时预览（`MeetExportPreviewRenderer`），改设置即刷新、不落盘。`MeetWorkbookExporter` 输出四页 Excel，单班导出整行加底色；`MeetPdfExporter` 用「SkiaSharp 画版 + QuestPDF 嵌 A4」输出：表格版长表格按行切开并重复表头、续页标题标「（续）」；卡片版（`MeetCardBuilder` / `MeetCardRenderer`）每个时段一张卡、按「时段」装箱分页，**不得**把一张卡劈到两页，也不得按时段重复计算日期标题高度。两份文件共用 `MeetExportBuilder`，内容必须一致。导出路径不得覆盖任何导入来源（报名表或秩序册），UI 层必须检查。
- **统一导入入口**：顶栏只有「导入秩序册」，`OrderBookImporter.DetectKind` 判断工作簿类型——带「代表队名单 / 参赛队统计」按秩序册整本导入（替换当前数据，需确认），只有「竞赛日程 / 项目分组表」按赛程导入（保留现有班级，需确认替换赛程），认不出则明说。不得再单独提供一个「导入赛程」按钮让老师先判断。
- **原表高亮**（`MeetExcelStyle.OriginalHighlight`）：复制 `MeetProject.SourcePath` 指向的原工作簿，只给目标班在「代表队名单」的块与「项目分组表」的该班单元格涂 #FBE4D7；**分组表里班级只写「N班」，必须用所在块头的组别拼成全名再比**，不得只按班级号匹配（会把其他年级的同号班一起刷色）；**不得**修改原文件、不得改动内容 / 列宽 / 合并 / 隐藏表 / 下拉校验，也**不得**在找不到原文件或该班时静默产出空表。
- **长任务提示**：任何可能超过几百毫秒的操作（导入、导出、保存、读名单）都必须走 `BeginBusy` / `EndBusy`，遮罩延迟 400ms 才亮，写明正在做什么与「处理期间请勿关闭窗口」；失败与提前返回的路径也必须 `EndBusy`，否则遮罩会盖住界面退不掉。
- 赛程页「仅显示该班选手」只改展示：摘掉其他班条目、整段没有该班选手的时段不显示；不得改动赛程数据。清除班级高亮时同步关闭该筛选。
- 赛程日期第一项固定是「全部」（`ScheduleDayItemViewModel.CreateAllDays`）：把每个比赛日的时段按「日 → 上午/下午」接排，每天第一组带日期小标题；它是视图层合成项，**不得**写进 `MeetProject.Days`（否则导出的赛程会多出一节、统计也会重复计数）。左侧徽标与页头「N 天」只数真实比赛日。
- **团体项目队员**：秩序册不点名队员，只能给到「某班有队」。导入过报名表后必须用报名表里勾了该项目的人把占位换成真实队员（`MeetEntry.TeamMembers` / `TeamMembersFromRegistration`）；按姓名匹配，匹配不到就跳过，**不得**编造或挂到同名以外的人头上。补队员只走 `ApplyTeamMembers`，**不得**用 `RefreshEntries` 整表重算——那会把秩序册带来的组次 / 道次抹掉。
- 报名表模板固定为：`A1` 运动会名称、`B2` 参赛单位、`B3` 参赛单位编码、`B4` 领队（班主任）与电话、`B5` 任课体育老师、第 7 行表头、第 8 行示例、第 9 行起为运动员。字段为姓名、性别、团体项目 1–3、比赛项目 1–2；不得要求用户改模板。
- 报名表的团体项目保留 3 个框；比赛项目合并为一列：0–1 个单项显示一个框（未报显示「选择项目」），2 个单项显示两个框。点击比赛项目列打开多选弹窗（同织序视觉）：可搜索、同时选 / 取消最多 2 个单项、人数预判与满员 / 重复提醒、清空全部、完成后按数量显示；团体项目框仍为单选。所有项目框都不带三角形。
- 报名规则固定为：每个单项每班男女各最多 2 人；每人最多 2 个单项（不含集体项目）；`20*50米男女混合接力` 与 `一分钟跳绳` 不填、现场出人；`4x100米接力` 可填。导入时忽略前两项且不计入违规；导出时也只写 `4x100米接力`。
- 「报名体检」只在真正违反报名规则（单项男女超 2 人、同一人超 2 个单项或重复项目、性别 / 项目无法识别）时出现；被忽略的团体项目、文件名建议、重复姓名等非规则提醒不显示，合规班级不出现体检卡片。
- 报名导入支持一次选择多个 `.xlsx` 班级文件；文件名应为「**年级**班运动会报名表.xlsx」，文件名与表内 `B2` 不一致时提示但不阻断。按班回写必须复用导入原文件以保留隐藏工作表与下拉校验，目标文件名由程序生成，禁止覆盖原始报名表。
- 赛程导入固定读取往届工作簿的「竞赛日程」与「项目分组表」：日程提供日期 / 上午下午 / 径赛田赛趣味赛 / 组别 / 性别 / 项目 / 赛次 / 人数 / 组数 / 录取 / 时间；分组表提供组次、道次、号码、姓名与班级。`预决赛` 与 `预赛` 视为同一赛次；`跳绳` 归一为 `一分钟跳绳`，`20x50米接力` 归一为 `20*50米男女混合接力`。
- 项目分组表的班级只写「6班」，必须与组别拼成完整班级名（如「八年级6班」）后再匹配报名班级与高亮；`1000米` 保留为个人项目（图片清单可能省略，但模板与往届赛程均存在）。
- 班级高亮只改变展示，不得改赛程数据：选中班级后，日程中包含该班条目的项目卡与分组条目同时着色，其余条目淡化；清除高亮恢复中性。高亮班级下拉来自当前班级集合，不写死示例班级。
- 真实报名表与赛程工作簿只在本机读取、不入库；测试、演示数据与截图一律使用合成学校 / 学生 / 班级（示例中学、苏晚晴、陈屿等虚构名）。真实数据不得进入 `tests/fixtures`、`.memory`、`design-preview` 或提交信息。
- 新增报名表字段、规则、赛程表结构或导出格式时按 Minor 版本晋级，并同步 `.memory` 中的竞名说明与测试数量。
