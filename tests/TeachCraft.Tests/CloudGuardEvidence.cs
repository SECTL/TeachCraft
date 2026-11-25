// 本文件不含任何 [Fact]：它只是 T9b「修复前必红（fix-before-red）」证据的归档，不参与测试发现。
//
// 执行状态：四条证据全部【实际执行】，没有一条是纯逻辑推演。
// 做法：在真实源码里临时引入旧行为 -> 只跑对应那一条 guard 捕获失败输出 ->
// 立即还原源码 -> 重跑同一条 guard 确认转绿。每次还原都由 PowerShell 的 try/finally
// 保证执行，还原后又用 grep 复核了四处原文均已回到基线（见文末「还原复核」）。
// 原始完整输出另存于 %TEMP%\opencode\red{1..4}.txt 与 green{1..4}.txt。
//
// 执行命令模板：
//   dotnet test tests/TeachCraft.Tests/TeachCraft.Tests.csproj -c Release --filter "FullyQualifiedName~<GuardName>"
//
// +---------------------------------------------------------------------------+
// | 证据表                                                                    |
// +--------+--------------------------------------+---------------------------+
// | 编号   | Guard                                | 旧行为（被打红的对象）     |
// +--------+--------------------------------------+---------------------------+
// | (i)    | WalkthroughWiringCannotConstructCloudServices | CloudWiring.Create 忽略 isWalkthrough |
// | (ii)   | App_StatesBothNetworkSurfaces        | MainWindow.axaml 回到两处联网文案 |
// | (iii)  | Copy_NamesAllOptInNetworkSurfaces    | OobeContent 写「联网只有两处」 |
// | (iv)   | ModuleMatrixCopyIsHonest             | README 宣称「所有应用都可以…共享名单」 |
// +--------+--------------------------------------+---------------------------+
//
// ---- (i) WalkthroughWiringCannotConstructCloudServices -----------------------
// 临时改动：src/TeachCraft.App/Cloud/CloudWiring.cs:26
//   原：if (isWalkthrough || string.IsNullOrWhiteSpace(clientId) || !SectlPlatformDefaults.IsConfigured)
//   改：if (string.IsNullOrWhiteSpace(clientId) || !SectlPlatformDefaults.IsConfigured)
//   —— 即旧实现「装配点不听 isWalkthrough」，走查也会 new 出全套云服务。
// 红（实际输出，节选）：
//   [xUnit.net] TeachCraft.Tests.LocalFirstCopyTests.WalkthroughWiringCannotConstructCloudServices [FAIL]
//   失败 ... [48 ms]
//   错误消息:
//    Assert.False() Failure
//   Expected: False
//   Actual:   True
//      at ...LocalFirstCopyTests.WalkthroughWiringCannotConstructCloudServices() ...:line 105
//   失败! - 失败: 1，通过: 0，总计: 1
// 绿（还原后实际输出）：
//   已通过! - 失败: 0，通过: 1，总计: 1
//
// ---- (ii) App_StatesBothNetworkSurfaces --------------------------------------
// 临时改动：src/TeachCraft.App/Views/MainWindow.axaml:450
//   原：Text="联网仅限更新、账号与集控"
//   改：Text="联网仅限更新与集控"   —— 旧的两处联网口径。
//   注意：MainViewModel.DefaultStatusText 里仍有该字符串，所以「全目录 corpus 断言」
//   不会红；正是本轮新增的「MainWindow.axaml 必须真的 shipped 这句」才把它打红。
// 红（实际输出，节选）：
//   [xUnit.net] TeachCraft.Tests.LocalFirstCopyTests.App_StatesBothNetworkSurfaces [FAIL]
//   失败 ... [35 ms]
//   错误消息:
//    Assert.Contains() Failure: Sub-string not found
//   String:    "<Window xmlns="https://github.com/avaloni"···
//   Not found: "联网仅限更新、账号与集控"
//      at ...LocalFirstCopyTests.App_StatesBothNetworkSurfaces() ...:line 83
//   失败! - 失败: 1，通过: 0，总计: 1
// 绿（还原后实际输出）：
//   已通过! - 失败: 0，通过: 1，总计: 1
//
// ---- (iii) Copy_NamesAllOptInNetworkSurfaces ---------------------------------
// 临时改动：src/TeachCraft.App/ViewModels/OobeContent.cs:93
//   原："联网只有三处：点「检查更新」…"
//   改："联网只有两处：点「检查更新」…"   —— 旧的引导文案。
// 红（实际输出，节选）：
//   [xUnit.net] TeachCraft.Tests.OobeContentTests.Copy_NamesAllOptInNetworkSurfaces [FAIL]
//   失败 ... [16 ms]
//   错误消息:
//    Assert.DoesNotContain() Failure: Sub-string found
//   String: ···"状态。\n处理结果不会覆盖导入的原文件。\n联网只有两处：点「检查更新」读取版本号；登"···
//   Found:  "联网只有两处"
//      at ...OobeContentTests.Copy_NamesAllOptInNetworkSurfaces() ...:line 130
//   失败! - 失败: 1，通过: 0，总计: 1
// 绿（还原后实际输出）：
//   已通过! - 失败: 0，通过: 1，总计: 1
//
// ---- (iv) ModuleMatrixCopyIsHonest -------------------------------------------
// 临时改动：README.md「本地优先」节注入一行
//   - **云同步全覆盖**：所有应用都可以拉取共享名单。
//   —— 旧式过度宣称（把所有模块都说成已接入）。
// 红（实际输出，节选）：
//   [xUnit.net] TeachCraft.Tests.LocalFirstCopyTests.ModuleMatrixCopyIsHonest [FAIL]
//   失败 ... [4 ms]
//   错误消息:
//    Assert.DoesNotContain() Failure: Sub-string found
//   String: ···" 本地优先\n\n- **云同步全覆盖**：所有应用都可以拉取共享名单。\n- **文件"···
//   Found:  "所有应用都可以"
//      at ...LocalFirstCopyTests.ModuleMatrixCopyIsHonest() ...:line 319
//   失败! - 失败: 1，通过: 0，总计: 1
// 绿（还原后实际输出）：
//   已通过! - 失败: 0，通过: 1，总计: 1
//
// ---- 还原复核（grep，全部命中原文 / 未命中注入串）----------------------------
//   CloudWiring.cs:26       命中 "if (isWalkthrough ||"
//   MainWindow.axaml:450    命中 "联网仅限更新、账号与集控"
//   OobeContent.cs:93       命中 "联网只有三处"
//   README.md               未命中 "所有应用都可以"
//
// ---- 备注 --------------------------------------------------------------------
// 除上述四条外，其余 T9a/T9b 结构守护（默认关闭、最小披露、无后台定时器/socket、
// 不写日志、识别路径无云调用、作业整理只读桥等）针对的是当前已落地代码的结构事实，
// 本轮未逐条构造对应的旧实现做红捕获；它们的红证据由 T9a 落地时各自的对照测试覆盖。
namespace TeachCraft.Tests;

/// <summary>
/// T9b 修复前必红证据归档。本类型刻意为空：证据全部记录在上面的文件级注释里，
/// 不注册任何 [Fact]，避免把「文档」误当成可执行断言。
/// </summary>
internal static class CloudGuardEvidence
{
}
