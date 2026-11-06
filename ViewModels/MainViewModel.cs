using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DesktopNotes.Models;
using DesktopNotes.Services;
using DesktopNotes.Views;

namespace DesktopNotes.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<NoteViewModel> Notes { get; } = new();

    private MainWindow? _mainWindow;
    private int _nextPos = 0;

    public string StatusText => $"共 {Notes.Count} 张便笺 · 显示 {Notes.Count(n => n.Window is { IsVisible: true })} 张";

    private string _importStatus = "";
    public string ImportStatus
    {
        get => _importStatus;
        private set { _importStatus = value; Raise(); }
    }

    /// <summary>开机自启开关（状态直接来自注册表）</summary>
    public bool AutoStartEnabled
    {
        get => AutoStart.IsEnabled;
        set
        {
            if (!AutoStart.SetEnabled(value, out var error))
            {
                ImportStatus = $"开机自启设置失败：{error}";
            }
            Raise();
        }
    }

    public MainViewModel()
    {
        NoteViewModel.SaveRequested = SaveAll;
    }

    public void LoadFromStore()
    {
        foreach (var model in NoteStore.Load())
        {
            var vm = new NoteViewModel(model);
            Notes.Add(vm);
        }
        _nextPos = Notes.Count;
        Raise(nameof(StatusText));
    }

    public void SaveAll()
    {
        NoteStore.Save(Notes.Select(n => n.Model).ToList());
    }

    /// <summary>启动时恢复桌面上未收起的便笺窗口</summary>
    public void RestoreNotes()
    {
        foreach (var vm in Notes.Where(n => !n.Model.Collapsed).ToList())
        {
            vm.Model.Collapsed = false;
            OpenNote(vm, promote: false);   // 启动恢复时保持桌面层，不浮出
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow { DataContext = this };
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.Activate();
        }
    }

    public void RegisterMainWindow(MainWindow window) => _mainWindow = window;

    public void NotifyChanged() => Raise(nameof(StatusText));

    public void CreateNote(NoteType type)
    {
        var model = new Note
        {
            Type = type,
            Title = type switch
            {
                NoteType.Note => "新建便签",
                NoteType.Todo => "新建待办",
                NoteType.Timeline => "新建时间轴",
                _ => "便笺",
            },
            X = 120 + (_nextPos % 8) * 36,
            Y = 120 + (_nextPos % 8) * 36,
            Color = NoteViewModel.Palette[_nextPos % NoteViewModel.Palette.Length],
        };
        if (type == NoteType.Timeline)
            model.Entries.Add(new TimelineEntry { Time = DateTime.Now, Text = "创建时间轴" });

        _nextPos++;
        var vm = new NoteViewModel(model);
        Notes.Add(vm);
        SaveAll();
        OpenNote(vm);
        Raise(nameof(StatusText));
    }

    /// <summary>
    /// 导入/同步 Windows 自带便笺。按 SourceId 去重：已导入过的只更新文字，不会重复创建。
    /// 返回新增 + 更新的数量；结果同时写入 ImportStatus 供界面显示。
    /// </summary>
    public int ImportStickyNotes(bool openWindows)
    {
        if (!StickyNotesImporter.IsAvailable)
        {
            ImportStatus = "未找到 Windows 便笺数据";
            return 0;
        }

        List<StickyNoteRecord> records;
        try
        {
            records = StickyNotesImporter.ReadAll();
        }
        catch (Exception ex)
        {
            ImportStatus = $"读取 Windows 便笺失败：{ex.Message}";
            return 0;
        }

        if (records.Count == 0)
        {
            ImportStatus = "Windows 便笺里没有内容";
            return 0;
        }

        int added = 0, updated = 0;
        foreach (var record in records)
        {
            var key = "stickynotes:" + record.Id;
            var (title, content) = SplitStickyText(record.Text);

            var existing = Notes.FirstOrDefault(n => n.Model.SourceId == key);
            if (existing != null)
            {
                if (existing.Model.Title == title && existing.Model.Content == content) continue;
                existing.Model.Title = title;
                existing.Model.Content = content;
                existing.Model.UpdatedAt = DateTime.Now;
                existing.RefreshAll();
                updated++;
                continue;
            }

            var note = new Note
            {
                Type = NoteType.Note,
                Title = title,
                Content = content,
                Color = StickyNotesImporter.ThemeToColor(record.Theme),
                AlwaysOnTop = record.AlwaysOnTop,
                SourceId = key,
                CreatedAt = record.CreatedAt,
                Width = 320,
                Height = 260,
                X = record.X ?? 120 + (_nextPos % 8) * 36,
                Y = record.Y ?? 120 + (_nextPos % 8) * 36,
            };
            _nextPos++;

            var vm = new NoteViewModel(note);
            Notes.Add(vm);
            added++;
            if (openWindows) OpenNote(vm);
        }

        SaveAll();
        Raise(nameof(StatusText));
        ImportStatus = added + updated == 0
            ? "Windows 便笺已是最新"
            : $"已导入 {added} 张，更新 {updated} 张";
        return added + updated;
    }

    /// <summary>
    /// 把便笺写回 Windows 自带便笺。已从系统便笺导入过的会原地更新，其余新增；
    /// 写入前自动备份数据库；写入后重新打开便笺程序，让它把这些便笺同步到云端。
    /// </summary>
    public int ExportStickyNotes()
    {
        if (!StickyNotesImporter.IsAvailable)
        {
            ImportStatus = "未找到 Windows 便笺数据库";
            return 0;
        }

        var targets = Notes
            .Where(n => !string.IsNullOrWhiteSpace(n.Model.Title)
                        || !string.IsNullOrWhiteSpace(n.Model.Content)
                        || n.Model.TodoItems.Count > 0
                        || n.Model.Entries.Count > 0)
            .Select(n => n.Model)
            .ToList();

        if (targets.Count == 0)
        {
            ImportStatus = "没有可导出的便笺";
            return 0;
        }

        // 便笺程序运行时会用内存缓存覆盖我们写库的结果，所以先关掉它
        var canControlApp = StickyNotesApp.ControlEnabled;
        var wasRunning = canControlApp && StickyNotesApp.IsRunning;
        var closed = true;
        if (wasRunning) closed = StickyNotesApp.Close();

        StickyNotesExporter.ExportResult result;
        try
        {
            result = StickyNotesExporter.Export(targets);
        }
        catch (Exception ex)
        {
            if (canControlApp) StickyNotesApp.Launch();   // 失败也把便笺程序还原
            ImportStatus = $"导出到 Windows 便笺失败：{ex.Message}";
            return 0;
        }

        // 重新打开便笺程序：它会加载新数据并交给自己的同步引擎上传（云同步就靠这一步）
        var launched = canControlApp && StickyNotesApp.Launch();

        SaveAll();   // SourceId 有更新，落盘

        var message = $"已导出 {result.Added} 张，更新 {result.Updated} 张";
        if (!canControlApp)
            message += "；已跳过便笺程序控制";
        else if (wasRunning && !closed)
            message += "；便笺程序未关闭，内容可能被其覆盖，请手动重开便笺";
        else if (!launched)
            message += "；未能打开便笺，请手动打开以触发云同步";
        else
            message += "；已重新打开便笺，正在同步到云端";

        ImportStatus = message;
        return result.Added + result.Updated;
    }

    /// <summary>便笺正文拆分：首行作标题，其余作正文；单行内容整段作为正文以免丢字</summary>
    private static (string Title, string Content) SplitStickyText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var first = lines[0].Trim();

        if (lines.Length == 1)
        {
            var shortTitle = first.Length > 30 ? first[..30] + "…" : first;
            return (shortTitle, first);
        }

        var title = first.Length > 40 ? first[..40] + "…" : first;
        var content = string.Join("\n", lines.Skip(1)).Trim();
        return (title, content);
    }

    public void OpenNote(NoteViewModel vm, bool promote = true)
    {
        if (vm.Window == null)
        {
            var win = new NoteWindow { DataContext = vm };
            vm.Window = win;
            win.Show();
            vm.SyncEmbedStateOnShow(win);
            if (promote) win.PromoteForEditing();
        }
        else
        {
            vm.Window.Show();
            if (promote) vm.Window.PromoteForEditing();
            vm.Window.Activate();
        }
        Raise(nameof(StatusText));
    }

    public void HideNote(NoteViewModel vm)
    {
        vm.Window?.Hide();
        Raise(nameof(StatusText));
    }

    public void DeleteNote(NoteViewModel vm)
    {
        vm.Window?.Close();
        vm.Window = null;
        Notes.Remove(vm);
        SaveAll();
        Raise(nameof(StatusText));
    }

    /// <summary>全部嵌入 / 全部退出桌面嵌入</summary>
    public void EmbedAll(bool embed)
    {
        foreach (var vm in Notes)
        {
            if (vm.Window != null)
            {
                if (vm.IsEmbeddedNow != embed)
                    vm.ToggleEmbed(vm.Window);
            }
            else
            {
                vm.Model.Embedded = embed;
            }
        }
        SaveAll();
    }

    /// <summary>检查到期的提醒，弹出对应便笺窗口并置顶</summary>
    public void CheckReminders()
    {
        bool changed = false;
        foreach (var vm in Notes.ToList())
        {
            if (vm.Model.Reminder is { } due && !vm.Model.ReminderFired && due <= DateTime.Now)
            {
                vm.Model.ReminderFired = true;
                changed = true;
                OpenNote(vm);   // 内部会把嵌入态便笺浮出，提醒才看得见
                if (vm.Window != null)
                {
                    vm.Window.Topmost = true;
                    Services.Win32.FlashWindow(vm.Window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, true);
                }
            }
        }
        if (changed) SaveAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

