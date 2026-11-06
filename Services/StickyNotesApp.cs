using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DesktopNotes.Services;

/// <summary>
/// Windows 自带便笺（Microsoft Sticky Notes）程序控制。
///
/// 便笺把数据存在本地 plum.sqlite，再由它自己的同步引擎上传到微软账号。
/// 我们直接写数据库时，运行中的便笺会用内存缓存覆盖掉写入，而且不会感知到新便笺；
/// 因此导出流程是：关闭运行中的便笺 → 写库 → 重新打开便笺（它读到新数据后自行完成云同步）。
/// </summary>
public static class StickyNotesApp
{
    public const string PackageFamilyName = "Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe";
    public const string AppId = "App";

    /// <summary>包内可执行文件是 Microsoft.Notes.exe</summary>
    private const string ProcessName = "Microsoft.Notes";

    /// <summary>是否允许自动关闭/重新打开便笺（测试可用 DESKTOPNOTES_NO_SYNC=1 关闭）</summary>
    public static bool ControlEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable("DESKTOPNOTES_NO_SYNC"), "1", StringComparison.Ordinal);

    public static bool IsRunning => Process.GetProcessesByName(ProcessName).Length > 0;

    /// <summary>关闭便笺程序（WM_CLOSE → taskkill 优雅结束 → 强杀兜底），返回是否已退出</summary>
    public static bool Close(TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(8);
        var procs = Process.GetProcessesByName(ProcessName);
        if (procs.Length == 0) return true;

        foreach (var proc in procs)
        {
            using (proc)
            {
                Win32.CloseWindowsOf(proc.Id);
            }
        }

        if (WaitExit((int)(wait.TotalMilliseconds / 2))) return true;

        // UWP 应用的窗口由 ApplicationFrameHost 承载，WM_CLOSE 不一定生效，用 taskkill 再关一次
        RunTaskKill("/IM " + ProcessName + ".exe");
        if (WaitExit((int)(wait.TotalMilliseconds / 2))) return true;

        // 最后强杀兜底：便笺每次修改都会落盘，强杀不会丢内容
        RunTaskKill("/F /IM " + ProcessName + ".exe");

        foreach (var proc in Process.GetProcessesByName(ProcessName))
        {
            try { proc.Kill(); } catch { /* 忽略 */ }
            finally { proc.Dispose(); }
        }
        return WaitExit(3000);
    }

    private static void RunTaskKill(string arguments)
    {
        try
        {
            using var tk = Process.Start(new ProcessStartInfo("taskkill", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            tk?.WaitForExit(3000);
        }
        catch { /* 交给下一步兜底 */ }
    }

    /// <summary>
    /// 启动便笺程序。它会加载 plum.sqlite，把新增/修改过的本地便笺交给同步引擎上传到云端。
    /// </summary>
    public static bool Launch()
    {
        try
        {
            var psi = new ProcessStartInfo(
                "explorer.exe",
                $@"shell:AppsFolder\{PackageFamilyName}!{AppId}")
            {
                UseShellExecute = true,
            };
            using var p = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动便笺程序失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>等待便笺进程全部退出</summary>
    private static bool WaitExit(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            if (!IsRunning) return true;
            Thread.Sleep(150);
        }
        return !IsRunning;
    }
}
