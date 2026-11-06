using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DesktopNotes.Models;
using DesktopNotes.Services;
using DesktopNotes.Views;

namespace DesktopNotes.ViewModels;

/// <summary>便笺视图模型：包装 Note 模型，任何修改即时保存。</summary>
public class NoteViewModel : INotifyPropertyChanged
{
    public static readonly string[] Palette =
    {
        "#FFF6B3", // 黄
        "#C8F7C5", // 绿
        "#BDE3FF", // 蓝
        "#FFD1DC", // 粉
        "#E3CCFF", // 紫
        "#FFE0B2", // 橙
    };

    public Note Model { get; }
    public NoteType Type => Model.Type;

    public ObservableCollection<TodoItemViewModel> TodoItems { get; } = new();
    public ObservableCollection<TimelineEntryViewModel> Entries { get; } = new();

    private NoteWindow? _window;
    public NoteWindow? Window { get => _window; set { _window = value; Raise(); } }
    private uint _originalStyle;
    private bool _embeddedNow;
    /// <summary>窗口当前是否已嵌入 WorkerW（运行时状态）</summary>
    public bool IsEmbeddedNow => _embeddedNow;

    public NoteViewModel(Note model)
    {
        Model = model;
        foreach (var item in model.TodoItems) TodoItems.Add(new TodoItemViewModel(item, Save));
        foreach (var entry in model.Entries) Entries.Add(new TimelineEntryViewModel(entry, OnTimelineEntryChanged));
        SortTimelineEntries();
    }

    public string Title
    {
        get => Model.Title;
        set { Model.Title = value; Model.UpdatedAt = DateTime.Now; Raise(); Save(); }
    }

    public string Content
    {
        get => Model.Content;
        set { Model.Content = value; Model.UpdatedAt = DateTime.Now; Save(); }
    }

    public string Color
    {
        get => Model.Color;
        set { Model.Color = value; Raise(); Save(); }
    }

    public void CycleColor()
    {
        int idx = Array.IndexOf(Palette, Model.Color);
        Color = Palette[(idx + 1) % Palette.Length];
    }

    public bool AlwaysOnTop
    {
        get => Model.AlwaysOnTop;
        set
        {
            Model.AlwaysOnTop = value;
            Raise();
            Raise(nameof(PinMenuText));
            Save();
        }
    }

    public bool Embedded
    {
        get => Model.Embedded;
        set
        {
            Model.Embedded = value;
            Raise();
            Raise(nameof(EmbedMenuText));
            Save();
        }
    }

    public string PinMenuText => AlwaysOnTop ? "取消置顶" : "窗口置顶";
    public string EmbedMenuText => Embedded ? "还原桌面" : "嵌入桌面";

    public DateTime? Reminder
    {
        get => Model.Reminder;
        set
        {
            Model.Reminder = value;
            Model.ReminderFired = false;
            Raise();
            Raise(nameof(ReminderText));
            Raise(nameof(HasReminder));
            Raise(nameof(ReminderCountdown));
            Save();
        }
    }

    public bool HasReminder => Model.Reminder.HasValue;

    public string ReminderText => Model.Reminder is null
        ? "定时提醒"
        : $"提醒时间 {Model.Reminder:MM-dd HH:mm}";

    public string ReminderCountdown
    {
        get
        {
            if (Model.Reminder is not { } due) return "";
            var remaining = due - DateTime.Now;
            if (Model.ReminderFired || remaining <= TimeSpan.Zero) return "提醒已到期";

            var countdown = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
            var time = countdown.ToString(@"hh\:mm\:ss");
            return countdown.Days > 0 ? $"⏳ {countdown.Days}天 {time}" : $"⏳ {time}";
        }
    }

    public void RefreshReminderCountdown() => Raise(nameof(ReminderCountdown));

    public string TypeIcon => Type switch
    {
        NoteType.Note => "📄",
        NoteType.Todo => "✅",
        NoteType.Timeline => "🕐",
        _ => "📄",
    };

    public string TypeLabel => Type switch
    {
        NoteType.Note => "便签",
        NoteType.Todo => "待办",
        NoteType.Timeline => "时间轴",
        _ => "便签",
    };

    public int TodoDoneCount => TodoItems.Count(t => t.Done);
    public string TodoSummary => $"已完成 {TodoDoneCount}/{TodoItems.Count}";

    // ---- 待办操作 ----
    public void AddTodo(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var item = new TodoItem { Text = text.Trim() };
        Model.TodoItems.Add(item);
        TodoItems.Add(new TodoItemViewModel(item, OnTodoChanged));
        Save();
        Raise(nameof(TodoSummary));
    }

    public void RemoveTodo(TodoItemViewModel vm)
    {
        Model.TodoItems.Remove(vm.Item);
        TodoItems.Remove(vm);
        Save();
        Raise(nameof(TodoSummary));
    }

    private void OnTodoChanged()
    {
        Save();
        Raise(nameof(TodoSummary));
    }

    // ---- 时间轴操作 ----
    public void AddTimelineEntry(string text)
        => AddTimelineEntry(DateTime.Now, text);

    public void AddTimelineEntry(DateTime time, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var entry = new TimelineEntry { Time = time, Text = text.Trim() };
        Model.Entries.Add(entry);
        Entries.Add(new TimelineEntryViewModel(entry, OnTimelineEntryChanged));
        SortTimelineEntries();
        Model.UpdatedAt = DateTime.Now;
        Save();
    }

    public void RemoveTimelineEntry(TimelineEntryViewModel vm)
    {
        Model.Entries.Remove(vm.Entry);
        Entries.Remove(vm);
        Save();
    }

    public void RefreshTimelineTimeStatus()
    {
        foreach (var entry in Entries)
            entry.RefreshTimeStatus();
    }

    private void OnTimelineEntryChanged(TimelineEntryViewModel entry)
    {
        SortTimelineEntries();
        Model.UpdatedAt = DateTime.Now;
        Save();
    }

    private void SortTimelineEntries()
    {
        var sorted = Entries.OrderBy(entry => entry.Time).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            var currentIndex = Entries.IndexOf(sorted[index]);
            if (currentIndex != index)
                Entries.Move(currentIndex, index);
        }

        Model.Entries.Clear();
        Model.Entries.AddRange(Entries.Select(entry => entry.Entry));
    }

    /// <summary>由 MainViewModel 注入的全局保存回调</summary>
    public static Action? SaveRequested;

    public void Save() => SaveRequested?.Invoke();

    /// <summary>模型被外部改写后刷新全部绑定</summary>
    public void RefreshAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    // ---- 窗口嵌入控制 ----
    public void ToggleEmbed(NoteWindow window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (Embedded)
        {
            // 想退出桌面层（无论当前是真正嵌入还是编辑时临时浮出）
            if (_embeddedNow)
            {
                Win32.UnembedFromDesktop(hwnd, _originalStyle);
                _embeddedNow = false;
            }
            Embedded = false;
            if (AlwaysOnTop) window.Topmost = true;
        }
        else
        {
            Embedded = true;
            ResumeEmbed(window);
        }
    }

    public void SyncEmbedStateOnShow(NoteWindow window)
    {
        if (Embedded && !_embeddedNow)
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (Win32.EmbedIntoDesktop(hwnd, out _originalStyle))
            {
                _embeddedNow = true;
                window.Topmost = false;
            }
            // 嵌入失败时保留用户的设置，下次打开窗口时再尝试
        }
    }

    /// <summary>
    /// 嵌入态的便笺收不到键盘焦点（它是桌面子窗口），点击时临时浮出为普通窗口以便编辑/拖动。
    /// 只改运行时状态，用户设置 Embedded 保持为 true，失焦后自动回到桌面层。
    /// </summary>
    public bool SuspendEmbedForEditing(NoteWindow window)
    {
        if (!_embeddedNow) return false;
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Win32.UnembedFromDesktop(hwnd, _originalStyle);
        _embeddedNow = false;
        return true;
    }

    /// <summary>编辑结束（失焦）后重新回到桌面层</summary>
    public void ResumeEmbed(NoteWindow window)
    {
        if (!Embedded || _embeddedNow) return;
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (Win32.EmbedIntoDesktop(hwnd, out _originalStyle))
        {
            _embeddedNow = true;
            window.Topmost = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>待办条目视图模型</summary>
public class TodoItemViewModel : INotifyPropertyChanged
{
    public TodoItem Item { get; }
    private readonly Action _onChanged;

    public TodoItemViewModel(TodoItem item, Action onChanged)
    {
        Item = item;
        _onChanged = onChanged;
    }

    public string Text
    {
        get => Item.Text;
        set { Item.Text = value; _onChanged(); }
    }

    public bool Done
    {
        get => Item.Done;
        set { Item.Done = value; _onChanged(); Raise(nameof(Done)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>时间轴条目视图模型：编辑即保存</summary>
public class TimelineEntryViewModel : INotifyPropertyChanged
{
    public TimelineEntry Entry { get; }
    private readonly Action<TimelineEntryViewModel> _onChanged;

    public TimelineEntryViewModel(TimelineEntry entry, Action<TimelineEntryViewModel> onChanged)
    {
        Entry = entry;
        _onChanged = onChanged;
    }

    public DateTime Time
    {
        get => Entry.Time;
        set => Update(value, Entry.Text);
    }

    public bool IsFuture => Time > DateTime.Now;

    public void RefreshTimeStatus() => Raise(nameof(IsFuture));

    public string Text
    {
        get => Entry.Text;
        set => Update(Time, value);
    }

    public void Update(DateTime time, string text)
    {
        Entry.Time = time;
        Entry.Text = text;
        Raise(nameof(Time));
        Raise(nameof(Text));
        Raise(nameof(IsFuture));
        _onChanged(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>简单的 ObservableCollection 引入（避免额外依赖 CommunityToolkit）</summary>
public class ObservableCollection<T> : System.Collections.ObjectModel.ObservableCollection<T> { }
