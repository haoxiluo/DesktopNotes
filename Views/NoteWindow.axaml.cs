using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNotes.Models;
using DesktopNotes.Services;
using DesktopNotes.ViewModels;

namespace DesktopNotes.Views;

public partial class NoteWindow : Window
{
    private NoteViewModel? _vm;

    private bool _resizing;
    private Win32.POINT _resizeStartCursor;
    private DispatcherTimer? _timelineStatusTimer;
    private DispatcherTimer? _reminderCountdownTimer;

    public NoteWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyType();
        HookDragHandlers();
        Deactivated += OnWindowDeactivated;
    }

    private void ApplyType()
    {
        _vm = DataContext as NoteViewModel;
        if (_vm == null) return;

        NotePanel.IsVisible = _vm.Type == NoteType.Note;
        TodoPanel.IsVisible = _vm.Type == NoteType.Todo;
        TimelinePanel.IsVisible = _vm.Type == NoteType.Timeline;
        if (_vm.Type == NoteType.Timeline)
        {
            _timelineStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _timelineStatusTimer.Tick += (_, _) => _vm.RefreshTimelineTimeStatus();
            _timelineStatusTimer.Start();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var vm = _vm ??= DataContext as NoteViewModel;
        if (vm == null) return;

        Position = new PixelPoint(vm.Model.X, vm.Model.Y);
        Width = vm.Model.Width;
        Height = vm.Model.Height;
        Topmost = vm.AlwaysOnTop;

        if (_reminderCountdownTimer == null)
        {
            _reminderCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _reminderCountdownTimer.Tick += (_, _) => vm.RefreshReminderCountdown();
        }
        _reminderCountdownTimer.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveGeometry();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timelineStatusTimer?.Stop();
        _reminderCountdownTimer?.Stop();
        base.OnClosed(e);
        if (_vm != null)
        {
            _vm.Window = null;
            App.MainVM?.NotifyChanged();
        }
    }

    // ============ 几何 ============
    private IntPtr Hwnd => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>点击便笺时确保窗口被激活，键盘输入才会落到便笺上</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        EnsureActive();
    }

    private bool _editingPromoted;

    private void EnsureActive()
    {
        var vm = _vm;
        // 嵌入态的便笺是桌面子窗口，收不到键盘焦点：点击时先临时浮出
        if (vm != null && vm.IsEmbeddedNow && vm.SuspendEmbedForEditing(this))
        {
            _editingPromoted = true;
        }

        if (!IsActive)
        {
            Activate();
            if (!IsActive) Win32.BringToFront(Hwnd);
        }
    }

    /// <summary>
    /// 嵌入态的便笺位于桌面图标层下面，点击会被图标层吃掉，因此从主面板/托盘打开时主动浮出编辑，
    /// 失焦后自动回到桌面层。
    /// </summary>
    public void PromoteForEditing()
    {
        var vm = _vm;
        if (vm == null || !vm.IsEmbeddedNow) return;
        if (vm.SuspendEmbedForEditing(this))
        {
            _editingPromoted = true;
        }
        Activate();
        if (!IsActive) Win32.BringToFront(Hwnd);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // 编辑结束：回到桌面层（图标之下）
        if (_editingPromoted)
        {
            _editingPromoted = false;
            _vm?.ResumeEmbed(this);
        }
    }

    private void SaveGeometry()
    {
        var vm = _vm;
        if (vm == null || Hwnd == IntPtr.Zero) return;
        if (Win32.TryGetWindowRect(Hwnd, out var r))
        {
            vm.Model.X = r.Left;
            vm.Model.Y = r.Top;
            vm.Model.Width = (r.Right - r.Left) / RenderScaling;
            vm.Model.Height = (r.Bottom - r.Top) / RenderScaling;
            vm.Save();
        }
    }

    // ============ 标题栏拖动 ============
    // 标题栏几乎被标题文本框占满，直接给 Border 挂事件会被 TextBox 吞掉；
    // 这里在隧道阶段监听：按住标题栏（按钮除外）移动超过阈值才拖动窗口，单击仍是编辑标题。
    // 位移用屏幕光标坐标计算，窗口移动不会反过来影响位移，因此不会抖动或跟不上。
    private bool _dragCandidate;
    private bool _dragStarted;
    private Win32.POINT _pressCursor;
    private Win32.RECT _startRect;

    private void HookDragHandlers()
    {
        AddHandler(PointerPressedEvent, OnDragPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (Hwnd == IntPtr.Zero) return;

        var pos = e.GetPosition(HeaderBar);
        if (pos.X < 0 || pos.Y < 0 || pos.X > HeaderBar.Bounds.Width || pos.Y > HeaderBar.Bounds.Height) return;
        if (IsInsideButton(e.Source)) return;   // 🎨/⏰/📌/嵌入/— 仍是普通按钮

        Win32.GetCursorPos(out _pressCursor);
        Win32.TryGetWindowRect(Hwnd, out _startRect);
        _dragCandidate = true;
        _dragStarted = false;
    }

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragCandidate || Hwnd == IntPtr.Zero) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        Win32.GetCursorPos(out var cursor);
        int dx = cursor.X - _pressCursor.X;
        int dy = cursor.Y - _pressCursor.Y;

        if (!_dragStarted)
        {
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;  // 阈内视为点击（编辑标题）
            _dragStarted = true;
            e.Pointer.Capture(HeaderBar);
        }

        Win32.MoveWindowTo(Hwnd, _startRect.Left + dx, _startRect.Top + dy,
            _startRect.Right - _startRect.Left, _startRect.Bottom - _startRect.Top);
        e.Handled = true;
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragStarted) { _dragCandidate = false; return; }
        EndDrag(e.Pointer);
        SaveGeometry();
    }

    private void EndDrag(IPointer pointer)
    {
        _dragCandidate = false;
        _dragStarted = false;
        pointer.Capture(null);
    }

    private static bool IsInsideButton(object? source)
    {
        var visual = source as Visual;
        while (visual != null)
        {
            if (visual is Button) return true;
            if (visual is Window) return false;
            visual = visual.GetVisualParent();
        }
        return false;
    }

    // ============ 右下角缩放 ============
    private void Resize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EnsureActive();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Hwnd == IntPtr.Zero) return;
        Win32.GetCursorPos(out _resizeStartCursor);
        Win32.TryGetWindowRect(Hwnd, out _startRect);
        _resizing = true;
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }

    private void Resize_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || Hwnd == IntPtr.Zero) return;
        Win32.GetCursorPos(out var cursor);
        int dx = cursor.X - _resizeStartCursor.X;
        int dy = cursor.Y - _resizeStartCursor.Y;
        int minW = (int)(MinWidth * RenderScaling);
        int minH = (int)(MinHeight * RenderScaling);
        int cx = Math.Max(minW, _startRect.Right - _startRect.Left + dx);
        int cy = Math.Max(minH, _startRect.Bottom - _startRect.Top + dy);
        Win32.MoveWindowTo(Hwnd, _startRect.Left, _startRect.Top, cx, cy);
        e.Handled = true;
    }

    private void Resize_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        SaveGeometry();
    }

    // ============ 标题栏按钮 ============
    private void CycleColor_Click(object? sender, RoutedEventArgs e) => _vm?.CycleColor();

    private async void Reminder_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        var editor = new ReminderEditWindow(_vm.Reminder);
        if (!await editor.ShowDialog<bool>(this)) return;

        _vm.Reminder = editor.ClearRequested ? null : editor.SelectedTime;
    }

    private void Pin_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.AlwaysOnTop = !_vm.AlwaysOnTop;
        Topmost = _vm.AlwaysOnTop;
    }

    private void Embed_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.ToggleEmbed(this);
    }

    private void Hide_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.Model.Collapsed = true;
        _vm.Save();
        Hide();
        App.MainVM?.NotifyChanged();
    }

    // ============ 待办 / 时间轴输入 ============
    private void TodoInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm != null)
        {
            _vm.AddTodo(TodoInput.Text ?? "");
            TodoInput.Text = "";
            e.Handled = true;
        }
    }

    private void TodoDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is Button { DataContext: TodoItemViewModel item })
            _vm.RemoveTodo(item);
    }

    private async void TimelineAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;

        var editor = new TimelineEntryEditWindow(DateTime.Now, "", isNew: true);
        if (await editor.ShowDialog<bool>(this))
            _vm.AddTimelineEntry(editor.SelectedTime, editor.EntryText);
    }

    private void TimelineDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && sender is Button { DataContext: TimelineEntryViewModel entry })
            _vm.RemoveTimelineEntry(entry);
    }

    private async void TimelineEntry_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button) return;
        if (sender is not Control { DataContext: TimelineEntryViewModel entry }) return;

        e.Handled = true;
        var editor = new TimelineEntryEditWindow(entry.Time, entry.Text);
        if (await editor.ShowDialog<bool>(this))
            entry.Update(editor.SelectedTime, editor.EntryText);
    }
}
