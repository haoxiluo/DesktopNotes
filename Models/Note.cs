using System;
using System.Collections.Generic;

namespace DesktopNotes.Models;

public enum NoteType
{
    Note = 0,     // 普通便签
    Todo = 1,     // 待办事项
    Timeline = 2, // 时间轴
}

public class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public class TimelineEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
}

public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NoteType Type { get; set; } = NoteType.Note;
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";                       // 普通便签正文
    public List<TodoItem> TodoItems { get; set; } = new();          // 待办事项
    public List<TimelineEntry> Entries { get; set; } = new();       // 时间轴条目

    // 位置与外观：X/Y 为物理像素，W/H 为 DIP
    public int X { get; set; } = 120;
    public int Y { get; set; } = 120;
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 260;
    public string Color { get; set; } = "#FFF6B3";

    public bool AlwaysOnTop { get; set; }
    public bool Embedded { get; set; }      // 是否嵌入桌面 (WorkerW)
    public bool Collapsed { get; set; }     // 是否收起（隐藏）

    public DateTime? Reminder { get; set; }         // 提醒时间
    public bool ReminderFired { get; set; }

    /// <summary>外部来源标识（如 "stickynotes:&lt;guid&gt;"），用于重复导入时去重</summary>
    public string? SourceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
