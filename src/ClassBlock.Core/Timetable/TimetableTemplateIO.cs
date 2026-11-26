using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassBlock.Core.Models;
using TeachCraft.Shared;

namespace ClassBlock.Core.Timetable;

public static class TimetableTemplateIO
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(TimetableTemplate template) =>
        JsonSerializer.Serialize(template, JsonOptions);

    public static TimetableTemplate FromJson(string json)
    {
        try
        {
            var template = JsonSerializer.Deserialize<TimetableTemplate>(json, JsonOptions)
                ?? throw new ClassBlock.Core.ClassBlockException("模板内容为空，无法读取。");

            if (template.Rows.Count == 0)
                throw new ClassBlock.Core.ClassBlockException("模板里没有任何行，无法使用。");

            return template;
        }
        catch (ClassBlock.Core.ClassBlockException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // JsonException 的通用文案说的是「工程文件」，对模板不对；这里给模板专用话术。
            var message = ex is JsonException
                ? "模板文件格式不正确，可能已损坏；请重新选择模板。"
                : ErrorText.Describe(ex, "读取模板文件");
            throw new ClassBlock.Core.ClassBlockException(message, ex);
        }
    }

    // ----------------------------- 自定义模板目录 -----------------------------

    public static string GetCustomDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TeachCraft", "ClassBlock", "templates");

    public static (IReadOnlyList<TimetableTemplate> Templates, IReadOnlyList<string> Warnings) LoadCustomTemplates(
        string? directory = null)
    {
        var dir = directory ?? GetCustomDirectory();
        var templates = new List<TimetableTemplate>();
        var warnings = new List<string>();

        if (!Directory.Exists(dir)) return (templates, warnings);

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                templates.Add(FromJson(File.ReadAllText(file)));
            }
            catch (ClassBlock.Core.ClassBlockException)
            {
                warnings.Add($"自定义模板 {Path.GetFileNameWithoutExtension(file)} 无法读取，已忽略。");
            }
        }

        return (templates, warnings);
    }

    public static string SaveCustom(TimetableTemplate template, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(template.Id))
            throw new ClassBlock.Core.ClassBlockException("自定义模板缺少编号，无法保存。");

        var dir = directory ?? GetCustomDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{template.Id}.json");
        File.WriteAllText(path, ToJson(template));
        return path;
    }

    public static void DeleteCustom(string id, string? directory = null, bool backup = false)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        var path = Path.Combine(directory ?? GetCustomDirectory(), $"{id}.json");
        if (!File.Exists(path)) return;
        if (backup) File.Copy(path, path + ".bak", overwrite: true);
        File.Delete(path);
    }
}

/// <summary>每个年级最近一次使用的模板编号，随窗口关闭前后的选择保存。</summary>
public sealed class ClassBlockPrefs
{
    public Dictionary<string, string> LastTemplateByGrade { get; set; } = new();
}

public static class ClassBlockPrefsStore
{
    private static string GetFilePath(string? directory) => Path.Combine(
        directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TeachCraft", "ClassBlock"),
        "settings.json");

    public static ClassBlockPrefs Load(string? directory = null)
    {
        var path = GetFilePath(directory);
        if (!File.Exists(path)) return new ClassBlockPrefs();

        try
        {
            return JsonSerializer.Deserialize<ClassBlockPrefs>(File.ReadAllText(path)) ?? new ClassBlockPrefs();
        }
        catch
        {
            return new ClassBlockPrefs();
        }
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件，再替换目标，避免中途崩溃留下半个 JSON。
    /// 直接 WriteAllText 会在写入途中被截断时丢掉全部年级的模板记忆，且下次读取静默回落成空（审计 P3-31）。
    /// </summary>
    public static void Save(ClassBlockPrefs prefs, string? directory = null)
    {
        var path = GetFilePath(directory);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(prefs));
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
