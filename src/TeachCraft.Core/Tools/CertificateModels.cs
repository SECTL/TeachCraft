namespace TeachCraft.Core.Tools;

public enum CertificateInputRole
{
    TemplateImage,
    RosterWorkbook,
}

public sealed record CertificateRecord(string Name, string Award);

public sealed record CertificateWorksheetInfo(string Name, int RowCount, int ColumnCount);

public sealed record CertificateWorkbookPreview(IReadOnlyList<CertificateWorksheetInfo> Worksheets);

public sealed record CertificateColumnChoice(int Index, string Letter, string Header)
{
    public string DisplayName => $"{Letter} · {Header}";
}

public sealed record CertificateWorksheetPreview(
    string Worksheet,
    int HeaderRow,
    int RowCount,
    IReadOnlyList<CertificateColumnChoice> Columns);

public sealed record CertificateTextPlacement(
    double NameX,
    double NameY,
    double AwardX,
    double AwardY,
    double NameFontScale,
    double AwardFontScale)
{
    public static CertificateTextPlacement Default { get; } = new(0.50, 0.52, 0.50, 0.66, 0.075, 0.040);

    public CertificateTextPlacement Clamp()
    {
        if (!double.IsFinite(NameX) || !double.IsFinite(NameY)
            || !double.IsFinite(AwardX) || !double.IsFinite(AwardY)
            || !double.IsFinite(NameFontScale) || !double.IsFinite(AwardFontScale))
            throw new ToolboxException("奖状文字位置和字号必须为有效数值，请重新设置。");

        return this with
        {
            NameX = Math.Clamp(NameX, 0, 1),
            NameY = Math.Clamp(NameY, 0, 1),
            AwardX = Math.Clamp(AwardX, 0, 1),
            AwardY = Math.Clamp(AwardY, 0, 1),
            NameFontScale = Math.Clamp(NameFontScale, 0.01, 0.40),
            AwardFontScale = Math.Clamp(AwardFontScale, 0.01, 0.40),
        };
    }
}

public sealed record CertificateBatchRequest(
    string TemplatePath,
    string RosterPath,
    string Worksheet,
    int HeaderRow,
    int NameColumn,
    int AwardColumn,
    CertificateTextPlacement Placement,
    string OutputPath);

public sealed record CertificateRenderRequest(
    string TemplatePath,
    string RosterPath,
    IReadOnlyList<CertificateRecord> Records,
    CertificateTextPlacement Placement,
    string OutputPath);

public sealed record CertificatePreviewPage(byte[] Png, int Width, int Height, string Detail);
