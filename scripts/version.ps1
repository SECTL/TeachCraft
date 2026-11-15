$ErrorActionPreference = 'Stop'

# 版本号唯一来源：Directory.Build.props 的 <Version>
#
# 三种触发，三种身份：
#   - 推送 tag（v1.30.0）      → 正式版，标签即版本号
#   - 手动触发 workflow_dispatch → v{Version}-preview.{run_number}，创建预发布
#   - push main                → 只跑测试与构建，**不创建 Release**
#
# 第三种是本轮改动：此前每次 push main 都发一个预览版，导致发布页被淹没，
# 且编号复用 run_number 而不连续（失败或跳过的运行会占号）。现在 push main
# 的产物只作为 CI artifact 留存，用来证明 14 个平台都构建得出来。
$isTag = $env:GITHUB_REF_TYPE -eq 'tag'
$isManual = $env:GITHUB_EVENT_NAME -eq 'workflow_dispatch'

if ($isTag -and $env:GITHUB_REF_NAME -match '^v\d+\.\d+\.\d+([-.].*)?$') {
    $tag = $env:GITHUB_REF_NAME
} else {
    [xml]$props = Get-Content (Join-Path $PSScriptRoot '..\Directory.Build.props')
    # PropertyGroup 可能不止一个（带 Condition 的分组很常见），此时属性访问会返回数组。
    # 直接取 .Version 再判空会因「数组 vs 字符串」抛异常，因此显式挑第一个非空值。
    $baseVersion = @($props.Project.PropertyGroup.Version) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($baseVersion)) {
        throw 'Directory.Build.props 缺少 <Version> 节点。'
    }

    $runNumber = if ($env:GITHUB_RUN_NUMBER) { $env:GITHUB_RUN_NUMBER } else { '0' }
    if ($isManual) {
        $tag = "v${baseVersion}-preview.${runNumber}"
    } else {
        # push main：不发布，但版本串仍要带 preview 标记——
        # 否则产物身份会显示成正式版号，有人手动下载 artifact 装上会被「检查更新」误判。
        $tag = "v${baseVersion}-build.${runNumber}"
    }
}

$isRelease = if ($tag -match '-') { 'false' } else { 'true' }

# SHOULD_PUBLISH 决定 publish-release job 是否创建 Release。
# 只有正式版与手动触发的预览版会发布；push main 一律不发。
$shouldPublish = if ($isTag -or $isManual) { 'true' } else { 'false' }

"VERSION_TAG=$tag" >> $env:GITHUB_ENV
"IS_RELEASE=$isRelease" >> $env:GITHUB_ENV
"SHOULD_PUBLISH=$shouldPublish" >> $env:GITHUB_ENV

# GITHUB_ENV 只对**后续 step** 生效，同一 step 里后面的命令读 $env:VERSION_TAG
# 仍是空的（release-draft.yml 就用它拼 BUILD_IDENTITY，会拼出 ".sc.win-x64"）。
# 因此本进程也写一份；直接运行时（非 CI）$env:GITHUB_ENV 为空，跳过即可。
$env:VERSION_TAG = $tag
$env:IS_RELEASE = $isRelease
$env:SHOULD_PUBLISH = $shouldPublish

Write-Host "Version: $tag (full-release=$isRelease, publish=$shouldPublish)"
