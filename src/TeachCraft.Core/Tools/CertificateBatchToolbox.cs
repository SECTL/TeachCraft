namespace TeachCraft.Core.Tools;

public static class CertificateBatchToolbox
{
    public static CertificateInputRole? ClassifyInput(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" => CertificateInputRole.TemplateImage,
            ".xlsx" or ".xls" => CertificateInputRole.RosterWorkbook,
            _ => null,
        };

    public static string Generate(CertificateBatchRequest request, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var templateRole = ClassifyInput(request.TemplatePath);
        var rosterRole = ClassifyInput(request.RosterPath);
        if (templateRole != CertificateInputRole.TemplateImage)
            throw new ToolboxException("请选择 PNG、JPG、WebP 或 BMP 奖状底图。");
        if (rosterRole != CertificateInputRole.RosterWorkbook)
            throw new ToolboxException("请选择 XLSX 或 XLS 获奖名单工作簿。");
        if (!Path.GetExtension(request.OutputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ToolboxException("奖状批量输出格式为 PDF。");
        if (File.Exists(request.OutputPath))
            throw new ToolboxException($"输出文件夹中已有「{Path.GetFileName(request.OutputPath)}」，请另选文件名。");

        cancellationToken.ThrowIfCancellationRequested();
        var records = CertificateRosterReader.Read(request);
        return CertificateRenderer.RenderPdf(new CertificateRenderRequest(
            request.TemplatePath, request.RosterPath, records, request.Placement.Clamp(), request.OutputPath),
            progress, cancellationToken);
    }
}
