$ErrorActionPreference = 'Stop'

# 版本号唯一来源：Directory.Build.props 的 <Version>
# - 推送 tag（v1.2.3）→ 正式版，标签即版本号
# - push main / 手动触发 → v{Version}-preview.{run_number}
$isTag = $env:GITHUB_REF_TYPE -eq 'tag'

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
    $tag = "v${baseVersion}-preview.${runNumber}"
}

$isRelease = if ($tag -match '-') { 'false' } else { 'true' }

"VERSION_TAG=$tag" >> $env:GITHUB_ENV
"IS_RELEASE=$isRelease" >> $env:GITHUB_ENV

# GITHUB_ENV 只对**后续 step** 生效，同一 step 里后面的命令读 $env:VERSION_TAG
# 仍是空的（release-draft.yml 就用它拼 BUILD_IDENTITY，会拼出 ".sc.win-x64"）。
# 因此本进程也写一份；直接运行时（非 CI）$env:GITHUB_ENV 为空，跳过即可。
$env:VERSION_TAG = $tag
$env:IS_RELEASE = $isRelease

Write-Host "Version: $tag (full-release=$isRelease)"
