# 14 · 扫描驱动能力与 TWAIN 待决策

> 状态：A 已落地（1.44.0）；B（WIA 驱动界面扫描）已落地；C（TWAIN）暂不实施，等条件成熟再评估。

## 一句话结论

- **现在**：扫描窗口只显示 WIA 驱动报告的选项（A）。驱动没报告的类别用保守默认值，并明确提示。
- **以后**：如果老师必须在知微里控制 ScanGear 2 的全部参数（8K/16K、文本/照片等），再考虑 C（TWAIN）。C 只做 Windows x64，成本高，必须有真实硬件回归。

## 当前 A 的行为

- 模型：`ScannerCapabilities`；读取：`WiaCapabilityReader`；入口：`IScannerService.GetCapabilitiesAsync`。
- 读取的 WIA 属性：`3086` / `3088`（来源）、`6146`（色彩）、`6147` / `6148`（分辨率）、`3097`（尺寸）、`3096`（最大页数）。
- 只显示驱动报告的来源 / 尺寸 / 分辨率 / 色彩；驱动什么都没报告时用「输稿器单面 / A4 / 300 dpi / 黑白」，并提示「驱动未报告可用参数」。
- **8K / 16K 不在 A 中显示**：WIA 没有标准枚举，只有自定义 page size + extents；需要走 B（自定义像素范围）或收件文件夹 / ScanGear 2。
- 分辨率允许驱动报告的非三档值（75–1200）；`ScanSettings.NormalizedForDriver()` 只做范围保护，不再强行夹到 200 / 300 / 600。

## C 是什么

- TWAIN 直接读 ScanGear 2 的 Data Source。
- `ShowUI=false`：知微自绘界面，能控制 ScanGear 2 暴露的完整参数。
- `ShowUI=true`：弹 ScanGear 2 自己的界面，知微只负责收图；代码少，但不是「软件内控制」。
- 只对 Windows 有意义；macOS 要 TWAIN framework，Linux 实际走 SANE。

## C 的成本

- `DSM_Entry`、原生窗口句柄、消息循环（`MSG_XFERREADY`）。
- `CAP_*` 容器（ONEVALUE / RANGE / ENUMERATION / ARRAY）与驱动差异。
- 多页 / 双面 / 取消状态机。
- 32/64 位：TWAIN 不做 thunk；64 位进程只能加载 64 位 DSM + 64 位驱动。
- 原生崩溃、CI 没有扫描仪只能人工回归、驱动升级回归。
- 打包 / 签名：要带 DSM 原生库；Windows 安装包、macOS 公证都多一层。
- 粗估：`ShowUI=false` 1–2 周；`ShowUI=true` 3–5 天；跨平台数周。

## 本机取证（2026-11-22）

- `C:\Windows\twain_32.dll` 存在；`TWAINDSM.dll` / `C:\Windows\twain_64` 不存在。
- `C:\Windows\twain_32` 只有 `wiatwain.ds`（WIA 桥）和 NLS；没有 Canon `.ds`。
- `Program Files\Canon` 下没有 `.ds` / `.dsm`。
- 结论：这台机器没有 64 位 TWAIN 栈；Canon ScanGear 2 可能没装，或只有 32 位。实际接扫描仪的那台机器要重新确认。

## 在扫描那台机器上怎么确认

```powershell
Get-ChildItem C:\Windows\twain_64,C:\Windows\twain_32 -ErrorAction SilentlyContinue |
  Select-Object Directory,Name,Length
Get-Item C:\Windows\System32\TWAINDSM.dll,C:\Windows\SysWOW64\TWAINDSM.dll -ErrorAction SilentlyContinue
Get-ChildItem 'C:\Program Files\Canon','C:\Program Files (x86)\Canon' -Recurse -Include *.ds -ErrorAction SilentlyContinue
```

- Canon `.ds` 在 `twain_64` 且 `TWAINDSM.dll` 存在 → x64 TWAIN 可行。
- 只在 `twain_32` → 32 位驱动，x64 知微需要 32 位桥接进程，成本高。
- 都没有 → 驱动没装，先装 Canon 64 位 ScanGear 2。

## B：WIA 驱动界面扫描（已落地）

- 入口：扫描窗口直接扫描页的「用驱动界面扫描」按钮。
- 实现：`WiaScannerService.AcquireWithDriverUiAsync` 调 `WIA.CommonDialog.ShowAcquireImage(0, 0, MaximizeQuality, PNG, true, UseCommonUI=true, CancelError=true)`，在 STA 上弹出扫描仪驱动自己的设置界面（就是 Windows 传真和扫描里那个），老师在那里选稿台玻璃 / 色彩 / DPI；扫完的 PNG 自动导入当前批次或新建批次。
- 优点：绕开驱动不暴露 WIA 类型化属性的问题，直接拿到驱动界面里所有真实选项；不依赖我们读能力。
- 局限：设置界面是驱动画的，不是知微原创纸墨风格；一次采集一张（多页需再次点按钮）；`UseCommonUI` 依赖驱动提供属性页。
- 所以现在是 A + B 并存：A 自绘界面（能读能力时按能力显示，读不到时列全部供手动选），B 用驱动界面拿完整选项。

## 扫描仪报错回传（2026-11-23）

- `WiaErrorText` 把 WIA 错误码翻译成中文可操作提示：`0x80210002` 卡纸、`0x80210003` 缺纸、`0x80210020` 多页进纸、`0x80210016` 盖板未合、`0x80210006` 忙、`0x80210005` 离线、`0x8021000A` 通信失败等。
- A 路径每页传输前后读 `WIA_DPS_DOCUMENT_HANDLING_STATUS`（3087）位标志：卡纸 `0x20`、多页进纸 `0x200`、需人工处理 `0x400`、灯故障 `0x800`、盖板未合 `0x08/0x10`；命中即停扫并显示明确提示。COM 异常按错误码翻译成中文，未知码才回退到原始消息。
- B 路径把多种调用尝试的 COM 码翻译成中文附在失败记录里；`--homework-scan-caps` 诊断也带 3087 状态原文。
- 限制：驱动必须在 COM 异常或 3087 里报告；若驱动只自己弹窗（如 Canon 界面里的提示），程序只能拿到最终错误码。HUAWEI 这类属性全空的驱动读不到 3087，但 COM 码仍可能返回。

## Canon 连接后 B 项报「参数错误」（2026-11-23）

- 现象：连 Canon 时点「用驱动界面扫描」，程序提示参数错误。`WIA.CommonDialog.ShowAcquireImage` 的 7 参数签名在文档上正确，但 Canon 驱动会按自己的实现挑参数/格式（常见是 PNG 不被接受、AlwaysSelectDevice/UseCommonUI 组合不被接受，或 WIA 1.x 的 6 参数版本）。
- 处理：`AcquireWithDriverUiCore` 不再只发一种调用，而是依次尝试 6 种组合（7 参数 / BMP、7 参数 / 默认格式、7 参数 / JPEG、7 参数 / 未指定设备、4 参数、6 参数 WIA 1.x），CancelError 一律 false（取消返回 null）。哪种成功用哪种；全部失败时把每种尝试的 `0xErrorCode + Message` 拼进 `HomeworkException`，界面 ScanStatus 会显示完整记录。
- 仍需用户在 Canon 机器上复核：如果弹的是驱动自己的「参数错误」对话框（不是 COM 异常），把那段完整文字发来，按驱动提示再调整。
- 相关：BMP 已在支持格式里（收件文件夹 / 导入选择器 / `PageImageDecoder`），驱动界面扫描保存为 bmp 也能进辑册。

## HUAWEI Scanner NW 实测（2026-11-23）

- 走查命令：`TeachCraft.exe --homework-scan-caps <路径>`，输出扫描仪名、解析结果、item/device/device-info 的 WIA 属性原文。
- 结果：WIA 枚举到「Huawei Scanner NW (wia-1)」，但 `Items[1].Properties`、`Device.Properties`、`DeviceInfo.Properties` **三个集合全空**；按数字 ID、字符串 ID、WIA 规范名三种方式读 `3086/3088/6146/6147/6148/3097/3096` 均读不到。
- 结论：该驱动不通过 WIA 自动化暴露任何能力，WIA 能力发现对它无效。它不是知微的读取实现问题（已用单值 / 多种 key / 0 基与 1 基 Vector 兼容）。
- 应对：`IsReported=false` 时界面**列出全部选项供手动选择**（来源含平板、尺寸、分辨率、色彩），并在能力提示里写明「驱动未报告可用参数，已列出全部选项供手动选择」。老师可手动选平板 / 300 dpi / 黑白；偏好持久化，下次不再重选。
- 这也是 C（TWAIN）的典型触发条件：如果老师需要在软件内自动拿到 ScanGear 2 的真实能力，而不是每次手动选，才值得做 C。

## 什么时候重新考虑 C

- 老师明确要求在知微里调 8K/16K 或 ScanGear 2 的完整参数，且收件文件夹 / 驱动界面不可接受。
- 扫描那台机器确认是 64 位 Canon TWAIN DS。
- 有人能长期做真实硬件回归（至少一台 Canon iR-ADV C5560i）。
- 能接受 C 只支持 Windows x64，x86 / arm64 继续用 A/WIA 或收件文件夹。
- 愿意承担原生互操作与驱动升级回归。

## 如果做 C，建议的顺序

1. 先做 `ShowUI=true` 的最小闭环（弹 ScanGear 2、收图、导入批次），验证 DSM、消息循环、多页。
2. 再做 `ShowUI=false`，读 `CAP_*` 自绘界面；先做分辨率 / 色彩 / 双面 / 纸张，8K/16K 最后。
3. 加位数检测：`Environment.Is64BitProcess`；x86 / arm64 禁用 C 并提示。
4. 加硬件回归清单：单面 / 双面、ADF 多页、平板、取消、卡纸、驱动升级。
5. 不要动现有 A/B 路径；C 是可选增强，失败时回退到 A / 收件文件夹。

## 相关文件

- `src/Homework.Core/Scanning/ScannerCapabilities.cs`
- `src/Homework.App/Scanning/WiaCapabilityReader.cs`
- `src/Homework.App/Scanning/WiaScannerService.cs`
- `.memory/13-作业整理.md`
- `AGENTS.md` 第 8 节第 14 条
