using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DesktopNotes.Services;

/// <summary>
/// 开机自启：写入当前用户的 Run 注册表项（无需管理员权限）。
/// 启动参数带 --autostart，应用启动后只恢复便笺、不弹主面板。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopNotes";

    /// <summary>登录时是否已登记自启</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>设置自启；返回是否成功。失败原因写入 error。</summary>
    public static bool SetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                error = "无法打开注册表 Run 项";
                return false;
            }

            if (enabled)
            {
                var command = BuildCommandLine();
                if (command == null)
                {
                    error = "无法确定程序路径";
                    return false;
                }
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>登录时执行的命令行：优先可执行文件本身，退化为 dotnet + dll</summary>
    private static string? BuildCommandLine()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) &&
            !exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{exe}\" --autostart";
        }

        var dll = typeof(AutoStart).Assembly.Location;
        if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) return null;

        var host = exe ?? "dotnet";
        return $"\"{host}\" \"{dll}\" --autostart";
    }
}
