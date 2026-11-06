using System;
using System.Runtime.InteropServices;

namespace DesktopNotes.Services;

/// <summary>
/// Win32 互操作：把便笺窗口嵌入桌面 WorkerW 层（壁纸引擎同款原理）。
/// 原理：向 Progman 发送 0x052C 消息，系统会在桌面图标层(SHELLDLL_DefView)之后再创建一个 WorkerW，
/// 把窗口 SetParent 到该 WorkerW 上，窗口即位于图标之下、壁纸之上。
/// </summary>
internal static class Win32
{
    private const int GWL_STYLE = -16;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_SHOW = 5;
    private const uint WM_PROGMAN_SPAWN_WORKERW = 0x052C;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const uint WM_CLOSE = 0x0010;

    /// <summary>给指定进程的所有顶层窗口发 WM_CLOSE（优雅关闭），返回是否至少发出一次</summary>
    public static bool CloseWindowsOf(int processId)
    {
        bool posted = false;
        EnumWindows((h, _) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if ((int)pid != processId) return true;
            if (!IsWindowVisible(h)) return true;
            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            posted = true;
            return true;
        }, IntPtr.Zero);
        return posted;
    }

    /// <summary>查找桌面窗口 Progman（顶层窗口，类名 Progman）</summary>
    public static IntPtr FindProgman()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var name = GetClassNameOf(h);
            if (name == "Progman") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 获取承载壁纸、位于桌面图标层之下的窗口：
    /// 1) Windows 11 新版布局：WorkerW 是 Progman 的直接子窗口（与 SHELLDLL_DefView 同级，且在其下方）；
    /// 2) 经典布局：SHELLDLL_DefView 位于某个顶层 WorkerW 内，取它之后的同级 WorkerW；
    /// 3) 兜底：直接使用 Progman。
    /// </summary>
    public static IntPtr GetWorkerWBehindIcons()
    {
        IntPtr progman = FindProgman();
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // 触发系统创建壁纸层 WorkerW（部分系统需要这一条消息）
        SendMessage(progman, WM_PROGMAN_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);

        // 1) 新版布局
        IntPtr workerw = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (workerw != IntPtr.Zero) return workerw;

        // 2) 经典布局
        IntPtr defViewOwner = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                defViewOwner = top;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (defViewOwner != IntPtr.Zero)
        {
            workerw = FindWindowEx(IntPtr.Zero, defViewOwner, "WorkerW", null);
            if (workerw != IntPtr.Zero) return workerw;
        }

        // 3) 兜底
        return progman;
    }

    /// <summary>把窗口嵌入桌面层（壁纸之上、图标之下）。返回是否成功。</summary>
    public static bool EmbedIntoDesktop(IntPtr hwnd, out uint originalStyle)
    {
        originalStyle = 0;
        if (hwnd == IntPtr.Zero) return false;

        IntPtr host = GetWorkerWBehindIcons();
        if (host == IntPtr.Zero) return false;

        RECT rect, hostRect;
        if (!GetWindowRect(hwnd, out rect)) return false;
        GetWindowRect(host, out hostRect);

        originalStyle = GetWindowLong(hwnd, (int)GWL_STYLE);
        uint style = (originalStyle & ~WS_POPUP) | WS_CHILD;
        SetWindowLong(hwnd, (int)GWL_STYLE, style);

        // 成为子窗口后，SetWindowPos 使用相对父窗口客户区的坐标
        SetParent(hwnd, host);
        SetWindowPos(hwnd, IntPtr.Zero,
            rect.Left - hostRect.Left, rect.Top - hostRect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        ShowWindow(hwnd, SW_SHOW);
        return GetParent(hwnd) == host;
    }

    /// <summary>把窗口从桌面层恢复为普通顶层窗口。</summary>
    public static void UnembedFromDesktop(IntPtr hwnd, uint originalStyle)
    {
        if (hwnd == IntPtr.Zero) return;

        RECT rect;
        GetWindowRect(hwnd, out rect);   // 子窗口也返回屏幕坐标

        SetParent(hwnd, IntPtr.Zero);
        uint style = GetWindowLong(hwnd, (int)GWL_STYLE);
        style = (style & ~WS_CHILD) | WS_POPUP;
        SetWindowLong(hwnd, (int)GWL_STYLE, style);
        if (originalStyle != 0) SetWindowLong(hwnd, (int)GWL_STYLE, originalStyle);

        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        ShowWindow(hwnd, SW_SHOW);
    }

    /// <summary>用屏幕坐标移动/缩放窗口（自动换算为父窗口客户区坐标，嵌入态同样有效）</summary>
    public static void MoveWindowTo(IntPtr hwnd, int screenX, int screenY, int cx, int cy)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr parent = GetParent(hwnd);
        if (parent != IntPtr.Zero)
        {
            RECT pr;
            if (GetWindowRect(parent, out pr))
            {
                screenX -= pr.Left;
                screenY -= pr.Top;
            }
        }
        SetWindowPos(hwnd, IntPtr.Zero, screenX, screenY, cx, cy, SWP_NOZORDER);
    }

    public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect) => GetWindowRect(hwnd, out rect);

    /// <summary>把窗口（及其顶层窗口）带到前台，确保点击后键盘输入落到便笺上</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (GetForegroundWindow() != root)
        {
            SetForegroundWindow(root);
        }
    }
}
