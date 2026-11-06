using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using DesktopNotes.Models;
using DesktopNotes.Services;
using DesktopNotes.ViewModels;
using DesktopNotes.Views;

namespace DesktopNotes;

public class App : Application
{
    public static MainViewModel? MainVM { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainVM = new MainViewModel();
            MainVM.LoadFromStore();

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 先建托盘：主面板关闭后要靠它重新打开
            SetupTrayIcon();

            var mainWindow = new MainWindow { DataContext = MainVM };
            MainVM.RegisterMainWindow(mainWindow);
            if (!IsAutoStartLaunch)
            {
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            }
            else
            {
                // 开机自启：不设 MainWindow，Avalonia 就不会自动显示主面板；
                // 之后可从托盘菜单唤出（MainVM.RegisterMainWindow 已保留引用）
                Console.WriteLine("以开机自启方式启动：仅恢复桌面便笺");
            }

            // 首次运行：把 Windows 自带便笺的内容搬过来
            if (!NoteStore.HasFile && StickyNotesImporter.IsAvailable)
            {
                var imported = MainVM.ImportStickyNotes(openWindows: true);
                Console.WriteLine($"首次运行：已从 Windows 便笺导入 {imported} 张");
            }

            // 恢复桌面上已有的便笺窗口
            MainVM.RestoreNotes();

            // 定时检查提醒
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
                (_, _) => MainVM.CheckReminders());
            timer.Start();

            desktop.ShutdownRequested += (_, _) => MainVM.SaveAll();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>是否由开机自启拉起：只恢复便笺，不弹主面板</summary>
    private static bool IsAutoStartLaunch =>
        Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

    /// <summary>托盘是否可用；不可用时主面板不隐藏，避免用户无法唤回</summary>
    public static bool TrayAvailable { get; private set; }

    private TrayIcon? _trayIcon;

    private void SetupTrayIcon()
    {
        try
        {
            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("显示主面板");
            showItem.Click += OnTrayShow;
            var newItem = new NativeMenuItem("新建便签");
            newItem.Click += OnTrayNewNote;
            var importItem = new NativeMenuItem("导入 Windows 便笺");
            importItem.Click += OnTrayImportSticky;
            var exportItem = new NativeMenuItem("导出到 Windows 便笺");
            exportItem.Click += OnTrayExportSticky;
            var autoStartItem = new NativeMenuItem("开机自启")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = AutoStart.IsEnabled,
            };
            autoStartItem.Click += OnTrayAutoStart;
            _trayAutoStart = autoStartItem;

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += OnTrayExit;

            menu.Items.Add(showItem);
            menu.Items.Add(newItem);
            menu.Items.Add(importItem);
            menu.Items.Add(exportItem);
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DesktopNotes/Assets/app.ico")));
            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "桌面便笺",
                Menu = menu,
            };
            _trayIcon.Clicked += (_, _) => MainVM?.ShowMainWindow();
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
            TrayAvailable = true;
        }
        catch (Exception ex)
        {
            TrayAvailable = false;
            Console.Error.WriteLine($"创建托盘图标失败: {ex.Message}");
        }
    }

    // ---- 托盘菜单 ----
    private NativeMenuItem? _trayAutoStart;

    private void OnTrayShow(object? sender, EventArgs e) => MainVM?.ShowMainWindow();

    private void OnTrayNewNote(object? sender, EventArgs e) => MainVM?.CreateNote(NoteType.Note);

    private void OnTrayImportSticky(object? sender, EventArgs e)
    {
        if (MainVM == null) return;
        MainVM.ImportStickyNotes(openWindows: true);
        MainVM.ShowMainWindow();
    }

    private void OnTrayExportSticky(object? sender, EventArgs e)
    {
        if (MainVM == null) return;
        MainVM.ExportStickyNotes();
        MainVM.ShowMainWindow();
    }

    private void OnTrayAutoStart(object? sender, EventArgs e)
    {
        if (MainVM == null) return;
        var next = !AutoStart.IsEnabled;
        MainVM.AutoStartEnabled = next;
        if (_trayAutoStart != null) _trayAutoStart.IsChecked = AutoStart.IsEnabled;
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        MainVM?.SaveAll();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
