namespace TeachCraft.Core.Updating;

/// <summary>「可以更新到某个版本」的完整提案——界面拿它就能直接显示与下载。</summary>
public sealed record UpdateProposal(
    ReleaseEntry Release,
    ReleaseAsset Asset,
    InstallKind Kind,
    bool NeedsModeExplanation)
{
    public string VersionText => Release.Version.ToString();
}
