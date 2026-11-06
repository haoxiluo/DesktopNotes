using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DesktopNotes.Services;

/// <summary>一条来自 Windows 便笺（Microsoft Sticky Notes）的数据</summary>
public class StickyNoteRecord
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string Theme { get; init; } = "Yellow";
    public bool AlwaysOnTop { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
}

/// <summary>
/// 读取 Windows 自带便笺的数据：%LocalAppData%\Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState\plum.sqlite。
/// 便笺程序运行时会占用数据库，因此先复制到临时目录再以读写方式打开（SQLite 会据此重放 -wal 日志）。
/// </summary>
public static class StickyNotesImporter
{
    private const string PackageFolder = @"Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState";

    private static readonly Regex ParagraphIdPattern = new(@"^\\id=[0-9a-fA-F\-]{36}\s?", RegexOptions.Compiled);
    private static readonly Regex PositionPattern =
        new(@"Position=(-?\d+),(-?\d+);Size=(\d+),(\d+)", RegexOptions.Compiled);

    public static string DatabasePath
    {
        get
        {
            // 测试或自定义场景可指向别的数据库副本
            var overridePath = Environment.GetEnvironmentVariable("DESKTOPNOTES_STICKY_DB");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                PackageFolder, "plum.sqlite");
        }
    }

    public static bool IsAvailable => File.Exists(DatabasePath);

    /// <summary>读取全部未删除、有文字的便笺；数据库不存在或无数据时返回空列表</summary>
    public static List<StickyNoteRecord> ReadAll()
    {
        var result = new List<StickyNoteRecord>();
        if (!IsAvailable) return result;

        var tempDir = Path.Combine(Path.GetTempPath(), "DesktopNotes-sticky-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var copy = Path.Combine(tempDir, "plum.sqlite");
            File.Copy(DatabasePath, copy, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var src = DatabasePath + suffix;
                if (File.Exists(src))
                {
                    try { File.Copy(src, copy + suffix, overwrite: true); } catch { /* 日志文件可能被占用，忽略 */ }
                }
            }

            using var connection = new SqliteConnection($"Data Source={copy};Mode=ReadWrite");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT Id, Text, WindowPosition, IsAlwaysOnTop, Theme, CreatedAt FROM Note " +
                "WHERE DeletedAt IS NULL AND Text IS NOT NULL AND length(trim(Text)) > 0";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var text = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var position = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var theme = reader.IsDBNull(4) ? "Yellow" : reader.GetString(4);
                var createdAtTicks = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);

                var cleaned = CleanText(text);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;

                int? x = null, y = null, w = null, h = null;
                var m = PositionPattern.Match(position ?? "");
                if (m.Success)
                {
                    x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    w = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    h = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                }

                result.Add(new StickyNoteRecord
                {
                    Id = reader.IsDBNull(0) ? Guid.NewGuid().ToString() : reader.GetString(0),
                    Text = cleaned,
                    Theme = theme,
                    AlwaysOnTop = !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                    CreatedAt = FromTicks(createdAtTicks),
                    X = x, Y = y, Width = w, Height = h,
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取 Windows 便笺失败: {ex.Message}");
            throw;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 忽略清理失败 */ }
        }

        return result;
    }

    /// <summary>把 "\\id=guid 第一行\\n\\id=guid 第二行" 还原成纯文本行</summary>
    public static string CleanText(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var output = new List<string>();
        foreach (var line in lines)
        {
            var cleanedLine = ParagraphIdPattern.Replace(line, "");
            output.Add(cleanedLine.TrimEnd());
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[0])) output.RemoveAt(0);
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1])) output.RemoveAt(output.Count - 1);
        return string.Join("\n", output);
    }

    /// <summary>便笺主题 → 便签颜色</summary>
    public static string ThemeToColor(string theme) => theme?.ToLowerInvariant() switch
    {
        "yellow" => "#FFF6B3",
        "green" => "#C8F7C5",
        "blue" => "#BDE3FF",
        "pink" => "#FFD1DC",
        "purple" => "#E3CCFF",
        "charcoal" => "#D7DBE0",
        _ => "#FFF6B3",
    };

    private static DateTime FromTicks(long ticks)
    {
        if (ticks <= DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return DateTime.Now;
        try { return DateTime.SpecifyKind(new DateTime(ticks), DateTimeKind.Utc).ToLocalTime(); }
        catch { return DateTime.Now; }
    }
}
