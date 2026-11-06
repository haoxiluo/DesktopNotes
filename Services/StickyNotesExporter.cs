using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using DesktopNotes.Models;

namespace DesktopNotes.Services;

/// <summary>
/// 把本应用的便笺写回 Windows 自带便笺（Microsoft Sticky Notes）的 plum.sqlite。
/// 说明：系统便笺没有公开 API，只能写入它的 SQLite 数据库。
/// - 写入前自动备份数据库（保留最近 10 份）；
/// - 已带 SourceId 的便笺（从系统便笺导入过的）执行更新，其余新增；
/// - 新增后会回写 SourceId，再次导出不会重复。
/// 若便笺程序正在运行，新内容可能要在它重启后才显示。
/// </summary>
public static class StickyNotesExporter
{
    public sealed class ExportResult
    {
        public int Added { get; init; }
        public int Updated { get; init; }
        public string? BackupPath { get; init; }
    }

    /// <summary>本机便笺程序是否在运行（运行中写入需要提示用户重启它）</summary>
    public static bool StickyNotesRunning =>
        System.Diagnostics.Process.GetProcesses()
            .Any(p =>
            {
                try { return p.ProcessName.Contains("StickyNotes", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
                finally { p.Dispose(); }
            });

    public static ExportResult Export(IReadOnlyList<Note> notes)
    {
        var dbPath = StickyNotesImporter.DatabasePath;
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("没有找到 Windows 便笺的数据库（plum.sqlite）");

        var backup = BackupDatabase(dbPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }

        int added = 0, updated = 0;
        using var transaction = connection.BeginTransaction();

        var parentId = FindParentId(connection, transaction);
        long nowTicks = DateTime.UtcNow.Ticks;

        foreach (var note in notes)
        {
            var text = BuildText(note);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var theme = ColorToTheme(note.Color);
            var stickyId = ExtractStickyId(note.SourceId);

            if (stickyId != null && RowExists(connection, transaction, stickyId))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE Note SET Text = $text, Theme = $theme, IsAlwaysOnTop = $top, UpdatedAt = $updated " +
                    "WHERE Id = $id";
                update.Parameters.AddWithValue("$text", text);
                update.Parameters.AddWithValue("$theme", theme);
                update.Parameters.AddWithValue("$top", note.AlwaysOnTop ? 1 : 0);
                update.Parameters.AddWithValue("$updated", nowTicks);
                update.Parameters.AddWithValue("$id", stickyId);
                updated += update.ExecuteNonQuery() > 0 ? 1 : 0;
                continue;
            }

            var newId = Guid.NewGuid().ToString();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO Note (Id, ParentId, Text, Theme, WindowPosition, IsOpen, IsAlwaysOnTop, " +
                "IsFutureNote, PendingInsightsScan, CreatedAt, UpdatedAt) " +
                "VALUES ($id, $parent, $text, $theme, $pos, 1, $top, 0, 0, $created, $updated)";
            insert.Parameters.AddWithValue("$id", newId);
            insert.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$text", text);
            insert.Parameters.AddWithValue("$theme", theme);
            insert.Parameters.AddWithValue("$pos", "");
            insert.Parameters.AddWithValue("$top", note.AlwaysOnTop ? 1 : 0);
            insert.Parameters.AddWithValue("$created", nowTicks);
            insert.Parameters.AddWithValue("$updated", nowTicks);
            insert.ExecuteNonQuery();

            note.SourceId = "stickynotes:" + newId;
            added++;
        }

        transaction.Commit();
        return new ExportResult { Added = added, Updated = updated, BackupPath = backup };
    }

    /// <summary>便笺正文：标题 + 正文/待办/时间轴，每个段落按系统便笺格式加 \id= 前缀</summary>
    public static string BuildText(Note note)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(note.Title)) lines.Add(note.Title.Trim());

        switch (note.Type)
        {
            case NoteType.Todo:
                foreach (var item in note.TodoItems)
                    lines.Add((item.Done ? "☑ " : "☐ ") + item.Text);
                break;
            case NoteType.Timeline:
                foreach (var entry in note.Entries)
                    lines.Add($"{entry.Time:yyyy-MM-dd HH:mm} {entry.Text}");
                break;
            default:
                if (!string.IsNullOrWhiteSpace(note.Content))
                {
                    foreach (var line in note.Content.Replace("\r\n", "\n").Split('\n'))
                        lines.Add(line);
                }
                break;
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(@"\id=").Append(Guid.NewGuid()).Append(' ').Append(line);
        }
        return builder.ToString();
    }

    public static string ColorToTheme(string? color) => color?.ToUpperInvariant() switch
    {
        "#FFF6B3" => "Yellow",
        "#C8F7C5" => "Green",
        "#BDE3FF" => "Blue",
        "#FFD1DC" => "Pink",
        "#E3CCFF" => "Purple",
        "#D7DBE0" => "Charcoal",
        _ => "Yellow",
    };

    private static string? ExtractStickyId(string? sourceId)
    {
        const string prefix = "stickynotes:";
        if (sourceId == null || !sourceId.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = sourceId.Substring(prefix.Length);
        return Guid.TryParse(id, out _) ? id : null;
    }

    private static bool RowExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM Note WHERE Id = $id AND DeletedAt IS NULL LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() != null;
    }

    /// <summary>新便笺挂在已有便笺同一个父节点下，保证便笺程序能正常显示</summary>
    private static string? FindParentId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT ParentId FROM Note WHERE ParentId IS NOT NULL AND ParentId <> '' LIMIT 1";
        var result = command.ExecuteScalar() as string;
        if (!string.IsNullOrEmpty(result)) return result;

        command.CommandText = "SELECT Id FROM User LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    /// <summary>备份数据库（含 -wal/-shm）到 %AppData%\DesktopNotes\backups，保留最近 10 份</summary>
    private static string BackupDatabase(string dbPath)
    {
        var overrideDir = Environment.GetEnvironmentVariable("DESKTOPNOTES_BACKUP_DIR");
        var dir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(NoteStore.DataDirectory, "backups")
            : overrideDir;
        Directory.CreateDirectory(dir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(dir, $"plum-{stamp}.sqlite");
        File.Copy(dbPath, backup, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var src = dbPath + suffix;
            if (File.Exists(src))
            {
                try { File.Copy(src, backup + suffix, overwrite: true); } catch { /* 忽略 */ }
            }
        }

        // 只保留最近 10 份备份。注意：复制出来的文件保留的是源文件的修改时间，
        // 因此按文件名里的时间戳排序，并连同 -wal/-shm 一起删除，避免留下孤立文件。
        try
        {
            var stamps = Directory.GetFiles(dir, "plum-*.sqlite")
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct()
                .OrderByDescending(s => s, StringComparer.Ordinal)
                .Skip(10);

            foreach (var oldStamp in stamps)
            {
                foreach (var file in Directory.GetFiles(dir, oldStamp + ".*"))
                {
                    File.Delete(file);
                }
            }
        }
        catch { /* 清理失败不影响导出 */ }

        return backup;
    }
}


