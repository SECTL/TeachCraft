using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using GradeWeave.Core.Models;

namespace GradeWeave.Core.Export;

/// <summary>
/// 希沃全校课表预设：把课表填进希沃官方的「全校课表导入模板（行政班）」，供老师直接上传希沃平台。
/// <para>
/// 刻意**不用 ClosedXML 重建工作簿**。那份模板是希沃自己发的，里面除了单元格还有批注（D4「课程名称请控制在
/// 20 字以内」、E4「老师姓名需与系统库匹配」）、外部链接与四个命名区域（CLASS / COURSE1 …），
/// 平台按列位与表名识别。ClosedXML 往返会重排 styles.xml、改写 namespace 前缀、丢掉行高，
/// 虽然内容读得对，但已经不再是「那份模板」了。这里直接改包：复制原 xlsx 的全部部件，
/// 只替换 sheet1.xml 里数据区的单元格值，其余字节原样带过去。
/// </para>
/// <para>
/// 模板约定（来自模板自带的填写须知与表头）：
/// 第 1 行标题、第 2 行须知、第 3–4 行表头、第 5 行是示例行（须知第 5 条要求正式填写时删掉）。
/// A 列年级、B 列班级、C 列课节（必填），之后每天两列：课程名称 / 老师姓名或手机号。
/// 没有排课的格子留空——须知第 4 条说可以不填。
/// </para>
/// </summary>
public static class WeaveSeewoExporter
{
    /// <summary>模板里的表名；找不到就说明这不是希沃模板，必须拒绝而不是硬填。</summary>
    private const string SheetName = "班级课表计划（希沃）";

    /// <summary>随程序内置的希沃模板在程序集里的资源名（见 GradeWeave.Core.csproj）。</summary>
    private const string TemplateResourceName = "GradeWeave.SeewoTemplate.xlsx";

    /// <summary>表头占 1–4 行，第 5 行是示例行，数据从第 5 行开始写（示例行被真实数据覆盖）。</summary>
    private const int FirstDataRow = 5;

    /// <summary>数据列：A 年级、B 班级、C 课节，D–Q 为七天 × 两列。</summary>
    private const int LastDataColumn = 17;

    /// <summary>一周七天；模板里排在 D–Q，周末也留着（没排课就空着）。</summary>
    private const int TemplateDayCount = 7;

    /// <summary>
    /// 把工程的课表填进模板并写到 <paramref name="path"/>。
    /// </summary>
    /// <param name="project">课表工程。</param>
    /// <param name="path">输出路径；与模板或导入源文件相同时拒绝。</param>
    /// <param name="warnings">需要如实告诉老师的提醒（未排满、班级名拆不出年级等）。</param>
    /// <param name="templatePath">指定模板文件；为空时使用随程序内置的希沃模板。</param>
    public static void Export(
        WeaveProject project,
        string path,
        ICollection<string>? warnings = null,
        string? templatePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(path))
            throw new GradeWeaveException("未指定导出路径，请选择保存位置。");
        if (project.Classes.Count == 0)
            throw new GradeWeaveException("工程里还没有班级，请先导入或新建工程后再导出。");

        var useBundled = string.IsNullOrWhiteSpace(templatePath);
        if (!useBundled && !File.Exists(templatePath))
            throw new GradeWeaveException("找不到希沃模板文件，请重新选择模板后再导出。");

        if (!useBundled && WeaveExportSupport.SamePath(templatePath!, path))
            throw new GradeWeaveException("导出路径与希沃模板相同，请另选保存位置，避免覆盖模板。");
        if (!string.IsNullOrWhiteSpace(project.SourceFile) &&
            WeaveExportSupport.SamePath(project.SourceFile, path))
            throw new GradeWeaveException("导出路径与导入的课表文件相同，请另选保存位置，避免覆盖源文件。");

        var rows = BuildRows(project, warnings);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        try
        {
            // 先写临时文件再替换：写到一半失败时不会留下一个半截的 xlsx。
            var temp = fullPath + ".tmp";
            if (useBundled)
                WriteBundledTemplate(temp);
            else
                File.Copy(templatePath!, temp, overwrite: true);

            try
            {
                FillWorkbook(temp, rows);
                File.Move(temp, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (GradeWeaveException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GradeWeaveException("填写希沃课表失败，请确认模板文件完整后重试。", ex);
        }
    }

    /// <summary>把内置模板解出到临时路径。</summary>
    private static void WriteBundledTemplate(string path)
    {
        using var resource = typeof(WeaveSeewoExporter).Assembly
            .GetManifestResourceStream(TemplateResourceName);
        if (resource is null)
            throw new GradeWeaveException("程序里的希沃模板缺失，请重新安装后再试。");

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
    }


    // ----------------------------- 行数据 -----------------------------

    /// <summary>一行：A 年级、B 班级、C 课节，以及七天各一对（课程 / 老师）。</summary>
    public sealed class SeewoRow
    {
        public required string Grade { get; init; }
        public required string ClassName { get; init; }
        public required int Period { get; init; }
        public required string[] Subjects { get; init; }
        public required string[] Teachers { get; init; }
    }

    /// <summary>
    /// 把工程摊平成「每班 × 每个正课节次」的行。
    /// 扩展时段（课后服务 / 晚修）不进希沃模板：模板的「课节（必填）」对应希沃自己的作息表节次，
    /// 课服与晚修不在其列，硬塞会让平台把课节数判成不符。
    /// </summary>
    public static List<SeewoRow> BuildRows(WeaveProject project, ICollection<string>? warnings)
    {
        var periods = WeaveExportSupport.TeachingPeriodsOf(project);
        var rows = new List<SeewoRow>();
        var gradeFallback = NormalizeGradeName(project.GradeName);
        var missingGradeClasses = new List<string>();

        foreach (var cls in project.Classes)
        {
            var grade = SplitGrade(cls.Name);
            if (grade.Length == 0)
            {
                grade = gradeFallback;
                if (grade.Length == 0) missingGradeClasses.Add(cls.Name);
            }

            foreach (var slot in periods)
            {
                var subjects = new string[TemplateDayCount];
                var teachers = new string[TemplateDayCount];
                for (var day = 1; day <= TemplateDayCount; day++)
                {
                    var (subject, teacher) = CellText(project, cls.Id, day, slot.Index);
                    subjects[day - 1] = subject;
                    teachers[day - 1] = teacher;
                }

                rows.Add(new SeewoRow
                {
                    Grade = grade,
                    ClassName = cls.Name,
                    Period = slot.Index,
                    Subjects = subjects,
                    Teachers = teachers,
                });
            }
        }

        if (missingGradeClasses.Count > 0)
        {
            warnings?.Add(
                $"{string.Join("、", missingGradeClasses.Take(3))}"
                + (missingGradeClasses.Count > 3 ? " 等班级" : string.Empty)
                + " 的名称里没有年级，工程的年级名也为空，A 列只能留空——请在工程里补上年级后重新导出。");
        }

        if (project.Classes.Count * periods.Count > 0)
        {
            var teachingPeriods = periods.Select(period => period.Index).ToHashSet();
            var placed = project.Lessons.Count(lesson =>
                !lesson.IsActivity && teachingPeriods.Contains(lesson.Period));
            var required = project.Assignments.Sum(assignment => assignment.WeeklyHours);
            if (required > 0 && placed < required)
            {
                warnings?.Add(
                    $"课表还差 {required - placed} 节没排（应排 {required}，已排 {placed}），"
                    + "希沃模板里这些格子会空着。");
            }
        }

        return rows;
    }

    /// <summary>
    /// 一个格子里要写的「课程名称」与「老师」。走班按层合成一格：
    /// 希沃的格位是「行政班这一节的课」，分层走班的 A/B 层都发生在同一时段，
    /// 写成「数学A层/B层」比拆成两行更接近行政班课表的实际。
    /// </summary>
    private static (string Subject, string Teacher) CellText(
        WeaveProject project, string classId, int day, int period)
    {
        var lessons = project.Lessons
            .Where(lesson => lesson.ClassId == classId && lesson.DayIndex == day && lesson.Period == period)
            .ToList();
        if (lessons.Count == 0) return (string.Empty, string.Empty);

        var activity = lessons.FirstOrDefault(lesson => lesson.IsActivity);
        if (activity is not null)
        {
            var name = string.IsNullOrWhiteSpace(activity.ActivityName) ? "活动" : activity.ActivityName;
            var teacher = project.FindTeacher(activity.TeacherId)?.Name ?? string.Empty;
            return (name, teacher);
        }

        var normal = lessons.Where(lesson => !lesson.IsActivity).ToList();
        if (normal.Count == 0) return (string.Empty, string.Empty);

        // 非走班的课直接取；走班（同班同节多条分层记录）合成一格。
        var primary = normal.FirstOrDefault(lesson => !WeaveExportSupport.IsLayered(lesson)) ?? normal[0];
        var subject = project.FindSubject(primary.SubjectId)?.Name ?? string.Empty;

        var layered = normal.Where(WeaveExportSupport.IsLayered).ToList();
        if (layered.Count == 0)
            return (subject, project.FindTeacher(primary.TeacherId)?.Name ?? string.Empty);

        var layerNames = layered
            .Select(lesson => WeaveExportSupport.TeacherLayerName(project, lesson))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var layerTeachers = layered
            .Select(lesson => project.FindTeacher(lesson.TeacherId)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var subjectText = subject.Length == 0 ? "走班" : subject;
        if (layerNames.Count > 0) subjectText += string.Join("/", layerNames);
        return (subjectText, string.Join("/", layerTeachers));
    }

    // ----------------------------- 填表 -----------------------------

    /// <summary>
    /// 打开 xlsx 包，替换 sheet XML 里的数据区，其余部件原样写回。
    /// <para>
    /// 关键约束：**除了 &lt;sheetData&gt; 的内容与 dimension 的末行，工作表 XML 其余字节一律不动**。
    /// 用 XDocument 解析再序列化会把表头须知里的 `&amp;#10;` 实体化成 CRLF、把 `&lt;b/&gt;` 写成 `&lt;b /&gt;`，
    /// 那就不是希沃发的那张表了。这里只在文本层做定位与拼接。
    /// </para>
    /// </summary>
    private static void FillWorkbook(string path, IReadOnlyList<SeewoRow> rows)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);

        var sheetEntry = FindSheetEntry(archive)
                         ?? throw new GradeWeaveException("希沃模板里找不到工作表，文件可能已损坏。");
        EnsureTemplateSheetName(archive, sheetEntry);

        string xml;
        using (var reader = new StreamReader(sheetEntry.Open(), Encoding.UTF8))
            xml = reader.ReadToEnd();

        // 只读解析：用来读样式号与命名空间前缀，原始字符串不被序列化回去。
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new GradeWeaveException("希沃模板的工作表内容无法读取。");
        XNamespace ns = root.Name.Namespace;
        var sheetData = root.Element(ns + "sheetData")
                        ?? throw new GradeWeaveException("希沃模板里没有单元格数据，文件可能已损坏。");

        // 记下模板里数据行的样式号：写入的单元格沿用同一套（C 列课节与 D–Q 的样式不同）。
        var styles = ReadTemplateStyles(sheetData, ns);
        var prefix = sheetData.Name.Namespace == XNamespace.None ? string.Empty : GetPrefix(xml, sheetData.Name.LocalName);

        var strings = new SharedStringTable(archive);
        var dataRows = new StringBuilder();
        var rowIndex = FirstDataRow;
        foreach (var item in rows)
        {
            dataRows.Append(BuildRowXml(prefix, rowIndex, item, styles, strings));
            rowIndex++;
        }

        var lastRow = Math.Max(rowIndex - 1, FirstDataRow);
        var updated = ReplaceSheetData(xml, dataRows.ToString(), prefix);
        updated = UpdateDimensionText(updated, lastRow);

        using (var output = sheetEntry.Open())
        {
            output.SetLength(0);
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.Write(updated);
        }

        strings.Save();
    }

    /// <summary>
    /// 把 &lt;sheetData&gt; 的内部换成新的数据行；表头行（1–4）原样保留。
    /// 找不到标签时抛出可读错误，而不是产出一份没填上数据的文件。
    /// </summary>
    private static string ReplaceSheetData(string xml, string dataRows, string prefix)
    {
        var tag = prefix.Length > 0 ? $"{prefix}:sheetData" : "sheetData";
        var open = System.Text.RegularExpressions.Regex.Match(
            xml, $@"<{System.Text.RegularExpressions.Regex.Escape(tag)}(\s[^>]*)?>");
        if (!open.Success)
            throw new GradeWeaveException("希沃模板里没有单元格数据区，文件可能已损坏。");

        var closeIndex = xml.IndexOf($"</{tag}>", open.Index + open.Length, StringComparison.Ordinal);
        if (closeIndex < 0)
            throw new GradeWeaveException("希沃模板的工作表结构不完整，文件可能已损坏。");

        var innerStart = open.Index + open.Length;
        var inner = xml[innerStart..closeIndex];

        // 表头行原样留下：只保留 row 序号小于数据起始行的那些。
        var header = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match row in
                 System.Text.RegularExpressions.Regex.Matches(inner, @"<(?:" + System.Text.RegularExpressions.Regex.Escape(prefix.Length > 0 ? prefix + ":" : string.Empty) + @"row)(\s[^>]*?)?(?:/>|>.*?</(?:" + System.Text.RegularExpressions.Regex.Escape(prefix.Length > 0 ? prefix + ":" : string.Empty) + @"row)>)",
                     System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var indexMatch = System.Text.RegularExpressions.Regex.Match(row.Value, @"\br=""(\d+)""");
            if (!indexMatch.Success) continue;
            if (int.Parse(indexMatch.Groups[1].Value) >= FirstDataRow) continue;
            header.Append(row.Value);
        }

        return xml[..innerStart] + header + dataRows + xml[closeIndex..];
    }

    /// <summary>只改 dimension 的末行，其余属性与前后内容不动。</summary>
    private static string UpdateDimensionText(string xml, int lastRow) =>
        System.Text.RegularExpressions.Regex.Replace(
            xml,
            @"(<dimension\b[^>]*?\bref=""[A-Z]+\d+:)([A-Z]+)(\d+)("")",
            match => $"{match.Groups[1].Value}{match.Groups[2].Value}{lastRow}{match.Groups[4].Value}");

    /// <summary>取某个元素在文档里实际使用的前缀（模板用默认命名空间时返回空串）。</summary>
    private static string GetPrefix(string xml, string localName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            xml, $@"<([A-Za-z_][\w.-]*):{System.Text.RegularExpressions.Regex.Escape(localName)}\b");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>把 XML 写回工作表部件；保留原来的 XML 声明。</summary>
    private static void WriteSheetXml(ZipArchiveEntry entry, XDocument document)
    {
        using var output = entry.Open();
        output.SetLength(0);
        using var writer = new StreamWriter(output, new UTF8Encoding(false));
        writer.Write(document.Declaration is null
            ? document.ToString(SaveOptions.DisableFormatting)
            : document.Declaration + document.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>按 workbook.xml 的第一个 sheet 找到对应的工作表部件；找不到时退回唯一的 sheet XML。</summary>
    private static ZipArchiveEntry? FindSheetEntry(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry is not null)
        {
            using var reader = new StreamReader(workbookEntry.Open(), Encoding.UTF8);
            var document = XDocument.Parse(reader.ReadToEnd());
            XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
            var first = document.Descendants(ns + "sheet").FirstOrDefault();
            var relationId = first?.Attribute(XNamespace.Xmlns + "r")?.Value
                             ?? first?.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (relationId is not null)
            {
                var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
                if (relsEntry is not null)
                {
                    using var relsReader = new StreamReader(relsEntry.Open(), Encoding.UTF8);
                    var rels = XDocument.Parse(relsReader.ReadToEnd());
                    var target = rels.Descendants()
                        .FirstOrDefault(element => element.Attribute("Id")?.Value == relationId)
                        ?.Attribute("Target")?.Value;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var normalized = target.Replace('\\', '/').TrimStart('/');
                        var path = normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                            ? normalized
                            : "xl/" + normalized;
                        var entry = archive.GetEntry(path);
                        if (entry is not null) return entry;
                    }
                }
            }
        }

        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
            entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>确认这是希沃的模板：表名含「课表计划」。认不出就拒绝，绝不往不相干的表里填课表。</summary>
    private static void EnsureTemplateSheetName(ZipArchive archive, ZipArchiveEntry sheetEntry)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry is null) return;

        using var reader = new StreamReader(workbookEntry.Open(), Encoding.UTF8);
        var document = XDocument.Parse(reader.ReadToEnd());
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var name = document.Descendants(ns + "sheet").FirstOrDefault()?.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, SheetName, StringComparison.Ordinal) ||
            name.Contains("课表计划", StringComparison.Ordinal) ||
            name.Contains("希沃", StringComparison.Ordinal))
            return;

        throw new GradeWeaveException(
            $"这份工作簿的表名是「{name}」，看起来不是希沃的全校课表模板。请改用希沃下发的模板文件。");
    }

    /// <summary>
    /// 模板数据行的样式号：C 列（课节）与其余数据列不同。
    /// 必须从原模板的第 5 行（示例行）读，而不是表头——表头是合并标题样式，数据行是另一套。
    /// </summary>
    private static (uint Period, uint Text) ReadTemplateStyles(XElement sheetData, XNamespace ns)
    {
        uint periodStyle = 0;
        uint textStyle = 0;

        var sample = sheetData.Elements(ns + "row")
            .FirstOrDefault(row => ParseRowIndex(row) == FirstDataRow);
        if (sample is null) return (0, 0);

        foreach (var cell in sample.Elements(ns + "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            if (reference is null) continue;
            var column = ColumnName(reference);
            if (column != "C" && column != "D") continue;

            if (cell.Attribute("s")?.Value is { } style && uint.TryParse(style, out var value))
            {
                if (column == "C") periodStyle = value;
                else textStyle = value;
            }
        }

        return (periodStyle, textStyle);
    }

    /// <summary>按模板的行结构写一行：A 年级、B 班级、C 课节，D–Q 依次是七天两列。</summary>
    private static string BuildRowXml(
        string prefix, int rowIndex, SeewoRow item,
        (uint Period, uint Text) styles, SharedStringTable strings)
    {
        var p = prefix.Length > 0 ? prefix + ":" : string.Empty;
        var builder = new StringBuilder(512);
        builder.Append($"<{p}row r=\"{rowIndex}\" spans=\"1:{LastDataColumn}\" customHeight=\"1\" ht=\"61\">");

        AppendTextCell(builder, p, $"A{rowIndex}", item.Grade, styles.Text, strings);
        AppendTextCell(builder, p, $"B{rowIndex}", item.ClassName, styles.Text, strings);

        builder.Append($"<{p}c r=\"C{rowIndex}\"");
        if (styles.Period > 0) builder.Append($" s=\"{styles.Period}\"");
        builder.Append($" t=\"n\"><{p}v>{item.Period}</{p}v></{p}c>");

        for (var day = 0; day < TemplateDayCount; day++)
        {
            var subjectColumn = ColumnLetter(4 + day * 2);
            var teacherColumn = ColumnLetter(5 + day * 2);
            AppendTextCell(builder, p, $"{subjectColumn}{rowIndex}", item.Subjects[day], styles.Text, strings);
            AppendTextCell(builder, p, $"{teacherColumn}{rowIndex}", item.Teachers[day], styles.Text, strings);
        }

        builder.Append($"</{p}row>");
        return builder.ToString();
    }

    /// <summary>
    /// 写一个文本单元格；空串就留空单元格（保留样式，不写值）。
    /// 文本走 sharedStrings 而不是 inlineStr：希沃与部分国产平台按共享字符串表解析单元格，
    /// inlineStr 虽然合法，但遇到不认的实现会读成空白。
    /// </summary>
    private static void AppendTextCell(
        StringBuilder builder, string prefix, string reference, string text, uint style, SharedStringTable strings)
    {
        builder.Append($"<{prefix}c r=\"{reference}\"");
        if (style > 0) builder.Append($" s=\"{style}\"");

        if (string.IsNullOrEmpty(text))
        {
            builder.Append(" />");
            return;
        }

        builder.Append($" t=\"s\"><{prefix}v>{strings.IndexOf(text)}</{prefix}v></{prefix}c>");
    }

    private static int? ParseRowIndex(XElement row) =>
        int.TryParse(row.Attribute("r")?.Value, out var index) ? index : null;

    private static string ColumnName(string reference) =>
        new(reference.TakeWhile(char.IsLetter).ToArray());

    /// <summary>1 → A，2 → B …；模板最多到 Q，这里只用到 D–Q。</summary>
    private static string ColumnLetter(int index)
    {
        var letter = string.Empty;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            letter = (char)('A' + remainder) + letter;
            index = (index - 1) / 26;
        }

        return letter;
    }

    // ----------------------------- 班级名 -----------------------------

    /// <summary>
    /// 从班级名拆年级：「九（1）班」→「九年级」、「九年级1班」→「九年级」、「高一8班」→「高一年级」。
    /// 拆不出来时返回空串，由调用方回落到工程的年级名。
    /// </summary>
    public static string SplitGrade(string className)
    {
        var text = (className ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        foreach (var (token, full) in GradeTokens)
        {
            if (text.StartsWith(token, StringComparison.Ordinal)) return full;
        }

        // 「2026级1班」这类：取「级」前两位数字。
        var level = System.Text.RegularExpressions.Regex.Match(text, @"^(\d{2,4})级");
        if (level.Success) return level.Groups[1].Value + "级";

        return string.Empty;
    }

    private static readonly (string Token, string Full)[] GradeTokens =
    [
        ("七年级", "七年级"), ("八年级", "八年级"), ("九年级", "九年级"),
        ("高一年级", "高一年级"), ("高二年级", "高二年级"), ("高三年级", "高三年级"),
        ("高一", "高一年级"), ("高二", "高二年级"), ("高三", "高三年级"),
        ("初一", "七年级"), ("初二", "八年级"), ("初三", "九年级"),
        ("七", "七年级"), ("八", "八年级"), ("九", "九年级"),
    ];

    /// <summary>工程的年级名规范化：「九」→「九年级」，「九年级」原样；空串保持空。</summary>
    private static string NormalizeGradeName(string? gradeName)
    {
        var text = (gradeName ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        if (text.EndsWith("年级", StringComparison.Ordinal)) return text;
        if (text.EndsWith("级", StringComparison.Ordinal)) return text;
        return text + "年级";
    }

    /// <summary>
    /// 共享字符串表的追加器。模板原有的字符串**一个字节都不改**（索引与内容原样保留，
    /// 表头与须知引用的条目一个都不能动），新文本只在文本层追加到 &lt;/sst&gt; 之前；
    /// 相同文本只写一次。
    /// <para>
    /// 刻意不用 XDocument 解析：XML 往返会把 `&amp;#10;` 实体化成真的 CRLF、
    /// 把 `&lt;b/&gt;` 改写成 `&lt;b /&gt;`，须知那段多行文本就悄悄变了。
    /// </para>
    /// </summary>
    private sealed class SharedStringTable
    {
        private const string EntryName = "xl/sharedStrings.xml";
        private static readonly XNamespace Ns =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private readonly ZipArchive _archive;
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<string> _added = [];
        private readonly List<string> _pending = [];
        private string _original = string.Empty;
        private int _next;
        private int _count;

        public SharedStringTable(ZipArchive archive)
        {
            _archive = archive;
            var entry = archive.GetEntry(EntryName);
            if (entry is null) return;

            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                _original = reader.ReadToEnd();

            // 只读不改：解析成 XDocument 仅仅是为了建索引，原始字符串仍原样留着。
            var document = XDocument.Parse(_original, LoadOptions.PreserveWhitespace);
            var items = document.Root?.Elements(Ns + "si").ToList() ?? [];
            _count = items.Count;
            _next = items.Count;
            for (var index = 0; index < items.Count; index++)
            {
                var text = ReadText(items[index]);
                if (text.Length > 0) _index.TryAdd(text, index);
            }
        }

        /// <summary>文本在共享字符串表里的索引；没有就排队追加一条。</summary>
        public int IndexOf(string text)
        {
            if (_index.TryGetValue(text, out var existing)) return existing;

            var index = _next++;
            _index[text] = index;
            _pending.Add(text);
            return index;
        }

        /// <summary>把追加的条目拼到 &lt;/sst&gt; 之前；没有任何新文本时不动文件。</summary>
        public void Save()
        {
            if (_pending.Count == 0 || _original.Length == 0) return;

            var closeIndex = _original.LastIndexOf("</sst>", StringComparison.Ordinal);
            if (closeIndex < 0) return;

            var builder = new StringBuilder(_original.Length + _pending.Count * 32);
            builder.Append(_original, 0, closeIndex);
            foreach (var text in _pending)
            {
                builder.Append("<si><t xml:space=\"preserve\">");
                builder.Append(Escape(text));
                builder.Append("</t></si>");
            }

            builder.Append(_original, closeIndex, _original.Length - closeIndex);

            var updated = builder.ToString();
            // count / uniqueCount 都按追加后的总数改写；只动 <sst ...> 这一个标签里的属性。
            // 替换串里必须写成 ${1}：`$1` 紧跟数字会被当成组号 $1149。
            var total = _count + _pending.Count;
            updated = System.Text.RegularExpressions.Regex.Replace(
                updated, @"(<sst\b[^>]*?\bcount="")[^""]*(?="")", "${1}" + total);
            updated = System.Text.RegularExpressions.Regex.Replace(
                updated, @"(<sst\b[^>]*?\buniqueCount="")[^""]*(?="")", "${1}" + total);

            var entry = _archive.GetEntry(EntryName);
            if (entry is null) return;

            using var output = entry.Open();
            output.SetLength(0);
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.Write(updated);
        }

        private static string ReadText(XElement item) =>
            item.Element(Ns + "t")?.Value
            ?? string.Concat(item.Elements(Ns + "r").Select(run => run.Element(Ns + "t")?.Value ?? string.Empty));

        private static string Escape(string text) =>
            text.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
