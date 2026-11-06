using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNotes.Models;

namespace DesktopNotes.Services;

/// <summary>
/// 便笺数据存储：默认 JSON 持久化到 %AppData%\DesktopNotes\notes.json。
/// 设置环境变量 DESKTOPNOTES_DATA_DIR 可改用指定目录（便携模式 / 自动化测试隔离）。
/// </summary>
public static class NoteStore
{
    private static readonly string Dir = ResolveDirectory();
    private static readonly string FilePath = Path.Combine(Dir, "notes.json");

    private static string ResolveDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("DESKTOPNOTES_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopNotes");
    }

    /// <summary>当前使用的数据目录（便于诊断）</summary>
    public static string DataDirectory => Dir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool HasFile => File.Exists(FilePath);

    public static List<Note> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Note>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<Note>>(json, JsonOptions) ?? new List<Note>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"加载便笺失败: {ex.Message}");
            return new List<Note>();
        }
    }

    public static void Save(IReadOnlyList<Note> notes)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(notes, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"保存便笺失败: {ex.Message}");
        }
    }
}
