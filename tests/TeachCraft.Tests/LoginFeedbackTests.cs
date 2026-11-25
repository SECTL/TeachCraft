using TeachCraft.App.ViewModels;

namespace TeachCraft.Tests;

/// <summary>登录反馈走查：文案不误导、同意提示有可点击的落地入口。</summary>
public sealed class LoginFeedbackTests
{
    [Fact]
    public void OobeStep4_LabelDoesNotPromiseLogin()
    {
        var step = OobeContent.Steps.Single(s => s.Kind == OobeStepKind.TryFirst);
        var action = step.Actions!.Single(a => a.Kind == OobeActionKind.OpenAccountPanel);

        Assert.DoesNotContain("登录 SECTL 账号", action.Label, StringComparison.Ordinal);
        Assert.Equal("账号与云同步（可选）", action.Label);
    }

    [Fact]
    public void AccountPanelView_HasJumpToConsentEvent()
    {
        var codeBehind = RepoRoot.Read("src/TeachCraft.App/Views/AccountPanelView.axaml.cs");
        var xaml = RepoRoot.Read("src/TeachCraft.App/Views/AccountPanelView.axaml");
        var mainWindow = RepoRoot.Read("src/TeachCraft.App/Views/MainWindow.axaml.cs");

        Assert.Contains("public event EventHandler? JumpToConsentRequested;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnJumpToConsent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowConsentPrompt", xaml, StringComparison.Ordinal);
        Assert.Contains("public void RevealConsent()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AccountPanel.JumpToConsentRequested", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AccountPanel.RevealConsent()", mainWindow, StringComparison.Ordinal);
    }
}
