# 桌面便笺 - 自动化功能测试（UI Automation）
# 用法:
#   powershell -ExecutionPolicy Bypass -File tools\test-app.ps1
# 说明:
#   - 会备份 %AppData%\DesktopNotes\notes.json，测试结束自动还原
#   - 截图与结果写入 .sbx\shots\
param(
    [string]$ProjectRoot = 'E:\DeepSeek Harness\DesktopNote',
    [string]$Exe,
    [string]$ShotDir
)

$ErrorActionPreference = 'Stop'
if ($PSScriptRoot) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
if (-not $Exe) { $Exe = Join-Path $ProjectRoot 'bin\Debug\net8.0-windows\DesktopNotes.exe' }
if (-not $ShotDir) { $ShotDir = Join-Path $ProjectRoot '.sbx\shots' }
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

$logFile = Join-Path $ShotDir 'test-log.txt'
if (Test-Path $logFile) { Remove-Item $logFile -Force }
function Log([string]$m) {
    $line = '{0:HH:mm:ss.fff} {1}' -f (Get-Date), $m
    Add-Content -Path $logFile -Value $line -Encoding UTF8
    Write-Host $line
}

if (-not (Test-Path $Exe)) { throw "找不到可执行文件: $Exe（请先 dotnet build）" }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# 清理上次中断遗留的实例（只结束本测试用的那个可执行文件，避免多个实例抢同一份 notes.json）
Get-Process DesktopNotes -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $Exe } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue

Log "test start exe=$Exe"

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class Desk
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public class W
    {
        public IntPtr H;
        public string Title = "";
        public string Cls = "";
        public RECT R;
        public uint Style;
        public IntPtr Parent;
        public string ParentCls = "";
        public bool Visible;
        public int Pid;
    }

    public delegate bool EP(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EP cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EP cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);

    public const uint MOUSE_DOWN = 0x0002;
    public const uint MOUSE_UP = 0x0004;

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(MOUSE_DOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSE_UP, 0, 0, 0, UIntPtr.Zero);
    }

    public const uint WS_CHILD = 0x40000000;
    public const uint WS_EX_TOPMOST = 0x00000008;

    static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static W Describe(IntPtr h)
    {
        var w = new W();
        w.H = h;
        w.Title = TitleOf(h);
        w.Cls = ClassOf(h);
        RECT r; GetWindowRect(h, out r); w.R = r;
        w.Style = GetWindowLong(h, -16);
        w.Parent = GetParent(h);
        w.ParentCls = w.Parent == IntPtr.Zero ? "" : ClassOf(w.Parent);
        w.Visible = IsWindowVisible(h);
        uint p; GetWindowThreadProcessId(h, out p); w.Pid = (int)p;
        return w;
    }

    public static IntPtr Progman()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (ClassOf(h) == "Progman") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>桌面壁纸层窗口：新版 Windows 是 Progman 的 WorkerW 子窗口，经典布局是顶层 WorkerW</summary>
    public static IntPtr DesktopHost()
    {
        IntPtr prog = Progman();
        if (prog == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ww = FindWindowEx(prog, IntPtr.Zero, "WorkerW", null);
        if (ww != IntPtr.Zero) return ww;
        IntPtr defViewOwner = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { defViewOwner = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (defViewOwner != IntPtr.Zero)
        {
            ww = FindWindowEx(IntPtr.Zero, defViewOwner, "WorkerW", null);
            if (ww != IntPtr.Zero) return ww;
        }
        return prog;
    }

    public static List<W> TopWindowsOf(int pid)
    {
        var list = new List<W>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid) list.Add(Describe(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<W> ChildWindowsOf(IntPtr parent, int pid)
    {
        var list = new List<W>();
        if (parent == IntPtr.Zero) return list;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if ((int)p == pid) list.Add(Describe(h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static W One(IntPtr h) { return Describe(h); }
}
'@
Log 'win helper ready'

# ---------------- 通用工具 ----------------
function Save-Shot([string]$path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Log "screenshot -> $path"
    return $path
}

$checks = New-Object System.Collections.ArrayList
function Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$checks.Add([ordered]@{ name = $name; ok = $ok; detail = $detail })
    if ($ok) { Log "PASS $name ($detail)" } else { Log "FAIL $name ($detail)" }
}

# 不用 Start-Process：它走 ShellExecute，新编译的 exe 首次启动可能被 shell/杀软拖住
function Start-App([string]$exePath, [string]$arguments = '') {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path -Parent $exePath
    if ($arguments) { $psi.Arguments = $arguments }
    return [System.Diagnostics.Process]::Start($psi)
}
function Get-Notes([int]$processId) {
    $top = @([Desk]::TopWindowsOf($processId) | Where-Object { $_.Title -eq '便笺' -and $_.Visible })
    $host_ = [Desk]::DesktopHost()
    $kids = @([Desk]::ChildWindowsOf($host_, $processId) | Where-Object { $_.Title -eq '便笺' -and $_.Visible })
    return @($top + $kids)
}

function Get-MainWindow([int]$processId) {
    return [Desk]::TopWindowsOf($processId) | Where-Object { $_.Title -eq '桌面便笺' } | Select-Object -First 1
}

function Get-MainRoot([int]$processId) {
    $mw = Get-MainWindow $processId
    if ($null -eq $mw) { Log 'Get-MainRoot: 未找到主面板窗口'; return $null }
    $h = $mw.H
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    if ($null -eq $el) { Log 'Get-MainRoot: FromHandle 返回空' }
    return $el
}
function Uia-Root([IntPtr]$hwnd) {
    if ($hwnd -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
}

function Uia-Descendants($root) {
    if ($null -eq $root) { return @() }
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))
}

function Uia-FindByType($root, [string]$controlType) {
    return @(Uia-Descendants $root | Where-Object { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$controlType" })
}

function Uia-InvokeElement($el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
}

function Uia-ClickButton($root, [string]$name, [switch]$Exact) {
    $buttons = Uia-FindByType $root 'Button'
    $target = $null
    if ($Exact) { $target = $buttons | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1 }
    else { $target = $buttons | Where-Object { $_.Current.Name -like "*$name*" } | Select-Object -First 1 }
    if ($null -eq $target) { throw "找不到按钮: $name" }
    Uia-InvokeElement $target
    return $true
}

function Invoke-DragTest([IntPtr]$hwnd, [int]$offsetX, [int]$offsetY, [int]$dx, [int]$dy, [int]$steps = 20) {
    # 先激活目标窗口，否则第一下点击可能只用于激活，导致拖动起点丢失
    [Desk]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 350
    $r = [Desk]::One($hwnd).R
    $sx = $r.L + $offsetX; $sy = $r.T + $offsetY
    [Desk]::SetCursorPos($sx, $sy) | Out-Null
    Start-Sleep -Milliseconds 200
    [Desk]::mouse_event([Desk]::MOUSE_DOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    $samples = @()
    for ($i = 1; $i -le $steps; $i++) {
        [Desk]::SetCursorPos(($sx + [int]($dx * $i / $steps)), ($sy + [int]($dy * $i / $steps))) | Out-Null
        Start-Sleep -Milliseconds 35
        $cur = [Desk]::One($hwnd)
        $samples += [pscustomobject]@{ exp = [int]($dx * $i / $steps); act = $cur.R.L - $r.L }
    }
    [Desk]::mouse_event([Desk]::MOUSE_UP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
    $final = [Desk]::One($hwnd)
    $maxLag = ($samples | ForEach-Object { [math]::Abs($_.act - $_.exp) } | Measure-Object -Maximum).Maximum
    $back = 0
    for ($i = 1; $i -lt $samples.Count; $i++) { if ($samples[$i].act -lt $samples[$i - 1].act) { $back++ } }
    return [pscustomobject]@{
        DeltaX = $final.R.L - $r.L
        DeltaY = $final.R.T - $r.T
        MaxLag = [int]$maxLag
        Backtracks = $back
    }
}
function Get-StatusText($root) {
    $texts = Uia-FindByType $root 'Text' | ForEach-Object { $_.Current.Name }
    return ($texts | Where-Object { $_ -like '共 * 张便笺*' } | Select-Object -First 1)
}

function Get-NoteTitleValues([IntPtr]$hwnd) {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $vals = @()
    foreach ($e in (Uia-FindByType $el 'Edit')) {
        try { $vals += ($e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { }
    }
    return $vals
}

function Find-NoteByTitle([int]$processId, [string]$title) {
    foreach ($n in (Get-Notes $processId)) {
        try {
            if ((Get-NoteTitleValues $n.H) -contains $title) { return $n }
        } catch { }
    }
    return $null
}

function Read-Json([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Wait-Until([scriptblock]$cond, [int]$timeoutMs = 8000, [int]$intervalMs = 250) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (& $cond) { return $true }
        Start-Sleep -Milliseconds $intervalMs
    }
    return $false
}

# ---------------- 数据准备 ----------------
# 便笺数据库使用副本测试，避免改动真实的 Windows 便笺数据
$stickySrcDb = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState\plum.sqlite'
$stickyTestDir = Join-Path $ShotDir 'sticky-db'
Remove-Item $stickyTestDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stickyTestDir | Out-Null
$stickyTestDb = Join-Path $stickyTestDir 'plum.sqlite'
if (Test-Path $stickySrcDb) {
    Copy-Item $stickySrcDb $stickyTestDb -Force
    foreach ($suffix in @('-wal','-shm')) {
        if (Test-Path ($stickySrcDb + $suffix)) { Copy-Item ($stickySrcDb + $suffix) ($stickyTestDb + $suffix) -Force }
    }
    $env:DESKTOPNOTES_STICKY_DB = $stickyTestDb
    $env:DESKTOPNOTES_BACKUP_DIR = Join-Path $ShotDir 'backups'
    $env:DESKTOPNOTES_NO_SYNC = '1'   # 测试不关闭/重开真实的便笺程序
    Log "便笺数据库副本: $stickyTestDb"
} else {
    Log '本机没有 Windows 便笺数据库，导入/导出测试将覆盖“未找到”分支'
}

# 应用数据目录也指向测试目录：不碰用户真实数据，且在受限环境里也能写
$appDataDir = Join-Path $ShotDir 'data'
Remove-Item $appDataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $appDataDir | Out-Null
$env:DESKTOPNOTES_DATA_DIR = $appDataDir
$notesPath = Join-Path $appDataDir 'notes.json'
$backupPath = Join-Path $appDataDir 'notes.user-backup.json'
New-Item -ItemType Directory -Force -Path $appDataDir | Out-Null
if (Test-Path $notesPath) { Copy-Item $notesPath $backupPath -Force; Log "备份用户便笺到 $backupPath" }

$now = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
$past = (Get-Date).AddMinutes(-2).ToString('yyyy-MM-ddTHH:mm:ss')
$seed = @(
    [ordered]@{
        Id = [guid]::NewGuid().ToString(); Type = 0; Title = '便签测试'
        Content = "这是一张普通便签。`n支持多行文本输入。"
        TodoItems = @(); Entries = @()
        X = 300; Y = 120; Width = 300; Height = 240; Color = '#FFF6B3'
        AlwaysOnTop = $false; Embedded = $false; Collapsed = $false
        Reminder = $null; ReminderFired = $false
        CreatedAt = $now; UpdatedAt = $now
    },
    [ordered]@{
        Id = [guid]::NewGuid().ToString(); Type = 1; Title = '待办测试'; Content = ''
        TodoItems = @(
            [ordered]@{ Text = '已完成的项'; Done = $true },
            [ordered]@{ Text = '未完成的项'; Done = $false }
        )
        Entries = @()
        X = 300; Y = 420; Width = 300; Height = 260; Color = '#C8F7C5'
        AlwaysOnTop = $false; Embedded = $false; Collapsed = $false
        Reminder = $null; ReminderFired = $false
        CreatedAt = $now; UpdatedAt = $now
    },
    [ordered]@{
        Id = [guid]::NewGuid().ToString(); Type = 2; Title = '时间轴测试'; Content = ''
        TodoItems = @()
        Entries = @(
            [ordered]@{ Time = $now; Text = '第一条时间轴记录' },
            [ordered]@{ Time = $now; Text = '第二条时间轴记录' }
        )
        X = 300; Y = 720; Width = 300; Height = 220; Color = '#BDE3FF'
        AlwaysOnTop = $false; Embedded = $true; Collapsed = $false
        Reminder = $null; ReminderFired = $false
        CreatedAt = $now; UpdatedAt = $now
    },
    [ordered]@{
        Id = [guid]::NewGuid().ToString(); Type = 0; Title = '提醒测试'; Content = '提醒应在启动后 15 秒内触发并置顶。'
        TodoItems = @(); Entries = @()
        X = 640; Y = 120; Width = 280; Height = 200; Color = '#FFD1DC'
        AlwaysOnTop = $false; Embedded = $false; Collapsed = $false
        Reminder = $past; ReminderFired = $false
        CreatedAt = $now; UpdatedAt = $now
    }
)
$seed | ConvertTo-Json -Depth 6 | Set-Content -Path $notesPath -Encoding UTF8
Log '种子数据已写入（4 张便笺，其中 1 张嵌入式、1 张到期提醒）'

# ---------------- 阶段 1：启动 ----------------
$proc = Start-App $Exe
Log "应用启动 pid=$($proc.Id)"

$ok = Wait-Until { (Get-Notes $proc.Id).Count -ge 4 } 20000
Start-Sleep -Milliseconds 1200

Check '应用进程存活' (-not $proc.HasExited) "pid=$($proc.Id)"
$main = Get-MainWindow $proc.Id
Check '主面板窗口出现' ($null -ne $main) $(if ($main) { "hwnd=$($main.H.ToInt64())" } else { '未找到' })

$notes = Get-Notes $proc.Id
Check '启动恢复 4 张便笺窗口' ($notes.Count -eq 4) "实际 $($notes.Count) 张"

# 几何还原（探测进程非 DPI 感知，按 96/DPI 折算）
$geomOk = 0
foreach ($s in $seed) {
    $hit = $notes | Where-Object {
        $dpi = [Desk]::GetDpiForWindow($_.H)
        $k = 96.0 / $dpi
        ([math]::Abs($_.R.L - ($s.X * $k)) -le 12) -and
        ([math]::Abs($_.R.T - ($s.Y * $k)) -le 12) -and
        ([math]::Abs(($_.R.R - $_.R.L) - $s.Width) -le 8) -and
        ([math]::Abs(($_.R.B - $_.R.T) - $s.Height) -le 8)
    }
    if ($hit) { $geomOk++ } else { Log "  未匹配: $($s.Title) 期望 $($s.X),$($s.Y) $($s.Width)x$($s.Height)" }
}
Check '位置/尺寸按存档还原' ($geomOk -eq 4) "$geomOk/4 张匹配"

# 嵌入
$hostW = [Desk]::DesktopHost()
$hostCls = [Desk]::One($hostW).Cls
$embedded = @($notes | Where-Object { $_.ParentCls -eq 'WorkerW' -or $_.Parent -eq $hostW })
Check 'WorkerW 嵌入生效' ($embedded.Count -eq 1) $(if ($embedded.Count) { "父窗口类=$($embedded[0].ParentCls)" } else { "没有嵌入窗口（桌面层类=$hostCls）" })
if ($embedded.Count -ge 1) {
    Check '嵌入窗口为子窗口 (WS_CHILD)' (($embedded[0].Style -band [Desk]::WS_CHILD) -ne 0) ("style=0x{0:X8}" -f $embedded[0].Style)
}

# 状态栏
$mainRoot = Uia-Root $main.H
$status = Get-StatusText $mainRoot
Check '主面板状态栏统计' ($status -like '共 4 张便笺*') "status=[$status]"

# 提醒
$reminder = $notes | Where-Object {
    $dpi = [Desk]::GetDpiForWindow($_.H); $k = 96.0 / $dpi
    ([math]::Abs($_.R.L - (640 * $k)) -le 12) -and ([math]::Abs($_.R.T - (120 * $k)) -le 12)
} | Select-Object -First 1

# 用扩展样式判断置顶（GetWindowLong -20）
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class ExStyle {
  [DllImport("user32.dll")] public static extern uint GetWindowLong(IntPtr h, int i);
  public static bool Topmost(IntPtr h) { return (GetWindowLong(h, -20) & 0x8) != 0; }
}
'@
if ($reminder) {
    $topmost = Wait-Until { [ExStyle]::Topmost($reminder.H) } 20000
    Check '到期提醒触发并置顶' $topmost "exstyle.topmost=$topmost"
} else {
    Check '到期提醒触发并置顶' $false '未找到提醒便笺窗口'
}

$shot1 = Save-Shot (Join-Path $ShotDir '01-startup.png')
# ---------------- 阶段 1.5：拖动与聚焦 ----------------
$dragNote = Find-NoteByTitle $proc.Id '便签测试'
if ($dragNote) {
    $drag = Invoke-DragTest $dragNote.H 120 15 120 80 20
    $okDelta = ([math]::Abs($drag.DeltaX - 120) -le 4) -and ([math]::Abs($drag.DeltaY - 80) -le 4)
    Check '拖动窗口 1:1 跟随光标' $okDelta "位移=($($drag.DeltaX),$($drag.DeltaY)) 期望=(120,80)"
    Check '拖动过程无抖动/回弹' (($drag.MaxLag -le 10) -and ($drag.Backtracks -eq 0)) "最大偏差=$($drag.MaxLag)px 回退=$($drag.Backtracks)"

    $pos = [Desk]::One($dragNote.H).R
    [Desk]::Click(($pos.L + 150), ($pos.T + 130))
    Start-Sleep -Milliseconds 700
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $focusOk = ($null -ne $focused) -and
               ($focused.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit') -and
               ($focused.Current.ProcessId -eq $proc.Id)
    Check '点击正文即可聚焦输入框' $focusOk $(if ($focused) { "$($focused.Current.ControlType.ProgrammaticName) pid=$($focused.Current.ProcessId)" } else { '无焦点元素' })
} else {
    Check '拖动窗口 1:1 跟随光标' $false '未找到便签窗口'
    Check '拖动过程无抖动/回弹' $false '未找到便签窗口'
    Check '点击正文即可聚焦输入框' $false '未找到便签窗口'
}

# ---------------- 阶段 2：主面板交互 ----------------
$uiRoot = Get-MainRoot $proc.Id
try {
    Uia-ClickButton $uiRoot '新建待办'
    $okNew = Wait-Until { (Get-Notes $proc.Id).Count -eq 5 } 6000
    $jsonCount = (Read-Json $notesPath).Count
    Check '主面板“新建待办”创建新便笺' ($okNew -and $jsonCount -eq 5) "窗口=$(($(Get-Notes $proc.Id)).Count) json=$jsonCount"
} catch { Check '主面板“新建待办”创建新便笺' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

$uiRoot = Get-MainRoot $proc.Id
try {
    Uia-ClickButton $uiRoot '新建时间轴'
    $okNew2 = Wait-Until { (Get-Notes $proc.Id).Count -eq 6 } 6000
    Check '主面板“新建时间轴”创建新便笺' $okNew2 "窗口=$(($(Get-Notes $proc.Id)).Count)"
} catch { Check '主面板“新建时间轴”创建新便笺' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

$status = Get-StatusText (Get-MainRoot $proc.Id)
Check '状态栏随新建更新' ($status -like '共 6 张便笺*') "status=[$status]"
$shot2 = Save-Shot (Join-Path $ShotDir '02-after-create.png')

# 全部嵌入 / 全部还原
$uiRoot = Get-MainRoot $proc.Id
try {
    Uia-ClickButton $uiRoot '全部嵌入'
    $allEmb = Wait-Until {
        $n = Get-Notes $proc.Id
        (@($n | Where-Object { ($_.Style -band [Desk]::WS_CHILD) -ne 0 }).Count -eq 6)
    } 8000
    Check '“全部嵌入”把所有便笺嵌入桌面' $allEmb "已嵌入=$(@(Get-Notes $proc.Id | Where-Object { ($_.Style -band [Desk]::WS_CHILD) -ne 0 }).Count)/6"
    $shot3 = Save-Shot (Join-Path $ShotDir '03-embed-all.png')
} catch { Check '“全部嵌入”把所有便笺嵌入桌面' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

# 嵌入便笺位于图标层之下收不到点击：从主面板“打开”时应临时浮出，失焦后自动回桌面层
try {
    $uiRoot = Get-MainRoot $proc.Id
    Uia-ClickButton $uiRoot '打开' -Exact
    $promotedOk = Wait-Until {
        @(Get-Notes $proc.Id | Where-Object { $_.Parent -eq [IntPtr]::Zero }).Count -ge 1
    } 8000
    $promoted = @(Get-Notes $proc.Id | Where-Object { $_.Parent -eq [IntPtr]::Zero }) | Select-Object -First 1
    Check '打开嵌入便笺时临时浮出' ($promotedOk -and $null -ne $promoted) $(if ($promoted) { "hwnd=$($promoted.H.ToInt64()) 父窗口=桌面" } else { '没有窗口浮出' })

    if ($promoted) {
        Start-Sleep -Milliseconds 500
        $fg = [Desk]::GetForegroundWindow()
        Check '浮出后便笺获得焦点' ($fg -eq $promoted.H) "前台=$($fg.ToInt64()) 便笺=$($promoted.H.ToInt64())"

        $mainNow = Get-MainWindow $proc.Id
        [Desk]::SetForegroundWindow($mainNow.H) | Out-Null
        $reembedOk = Wait-Until { ([Desk]::One($promoted.H)).Parent -ne [IntPtr]::Zero } 6000
        Check '失焦后自动回到桌面层' $reembedOk "父窗口=$(([Desk]::One($promoted.H)).ParentCls)"
    }
} catch { Check '嵌入便笺浮出编辑' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

$uiRoot = Get-MainRoot $proc.Id
try {
    Uia-ClickButton $uiRoot '全部还原'
    $allBack = Wait-Until {
        $n = Get-Notes $proc.Id
        @($n | Where-Object { ($_.Style -band [Desk]::WS_CHILD) -eq 0 }).Count -eq 6
    } 8000
    Check '“全部还原”恢复为普通窗口' $allBack "已还原=$(@(Get-Notes $proc.Id | Where-Object { ($_.Style -band [Desk]::WS_CHILD) -eq 0 }).Count)/6"
} catch { Check '“全部还原”恢复为普通窗口' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

# 单张窗口：嵌入 → 还原
$target = Find-NoteByTitle $proc.Id '便签测试'
if (-not $target) { $target = (Get-Notes $proc.Id) | Where-Object { ($_.Style -band [Desk]::WS_CHILD) -eq 0 } | Select-Object -First 1 }
if ($target) {
    try {
        # 先确保处于未嵌入状态（按钮显示“还原”时先点还原）
        $nr = Uia-Root $target.H
        $backBtn = Uia-FindByType $nr 'Button' | Where-Object { $_.Current.Name -eq '还原' } | Select-Object -First 1
        if ($backBtn) { Uia-InvokeElement $backBtn; Start-Sleep -Milliseconds 800 }

        $nr = Uia-Root $target.H
        Uia-ClickButton $nr '嵌入' -Exact
        $embOne = Wait-Until { (([Desk]::One($target.H)).Style -band [Desk]::WS_CHILD) -ne 0 } 6000
        Check '单张便笺“嵌入”按钮' $embOne "style=0x$('{0:X8}' -f ([Desk]::One($target.H)).Style)"

        $nr = Uia-Root $target.H
        Uia-ClickButton $nr '还原' -Exact
        $backOne = Wait-Until { (([Desk]::One($target.H)).Style -band [Desk]::WS_CHILD) -eq 0 } 6000
        Check '单张便笺“还原”按钮' $backOne "style=0x$('{0:X8}' -f ([Desk]::One($target.H)).Style)"
    } catch { Check '单张便笺“嵌入/还原”按钮' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }
} else {
    Check '单张便笺“嵌入/还原”按钮' $false '没有可用的顶层便笺窗口'
}

# 待办：删除条目
$current = Get-Notes $proc.Id
Log ("便笺列表: " + (($current | ForEach-Object {
    "({0},{1}) {2}x{3} child={4}" -f $_.R.L, $_.R.T, ($_.R.R - $_.R.L), ($_.R.B - $_.R.T), (($_.Style -band [Desk]::WS_CHILD) -ne 0)
}) -join ' | '))
$todoWin = Find-NoteByTitle $proc.Id '待办测试'
if ($todoWin) {
    try {
        $before = ((Read-Json $notesPath) | Where-Object { $_.Title -eq '待办测试' }).TodoItems.Count
        $tr = Uia-Root $todoWin.H
        Uia-ClickButton $tr '✕' -Exact
        $deleted = Wait-Until {
            $after = ((Read-Json $notesPath) | Where-Object { $_.Title -eq '待办测试' }).TodoItems.Count
            $after -eq ($before - 1)
        } 5000
        $after = ((Read-Json $notesPath) | Where-Object { $_.Title -eq '待办测试' }).TodoItems.Count
        Check '待办条目可删除并即时保存' $deleted "条目 $before -> $after"
    } catch { Check '待办条目可删除并即时保存' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }
} else {
    Check '待办条目可删除并即时保存' $false '未找到待办便笺窗口'
}

# 换色
if ($target) {
    try {
        $all = Read-Json $notesPath
        $beforeColors = ($all | ForEach-Object { $_.Color }) -join ','
        $nr = Uia-Root $target.H
        Uia-ClickButton $nr '🎨' -Exact
        $changed = Wait-Until {
            $now_ = (Read-Json $notesPath | ForEach-Object { $_.Color }) -join ','
            $now_ -ne $beforeColors
        } 5000
        Check '换色按钮修改颜色并保存' $changed "颜色: $beforeColors"
    } catch { Check '换色按钮修改颜色并保存' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }
}

# 隐藏 → 主面板重新打开
if ($target) {
    try {
        $nr = Uia-Root $target.H
        Uia-ClickButton $nr '—' -Exact
        $hidden = Wait-Until { -not ([Desk]::One($target.H)).Visible } 5000
        Check '便笺“隐藏”按钮生效' $hidden "visible=$(([Desk]::One($target.H)).Visible)"

        $uiRoot = Get-MainRoot $proc.Id
        $openButtons = Uia-FindByType $uiRoot 'Button' | Where-Object { $_.Current.Name -eq '打开' }
        foreach ($b in $openButtons) { try { Uia-InvokeElement $b } catch { } }
        $reopened = Wait-Until { ([Desk]::One($target.H)).Visible } 6000
        Check '主面板“打开”重新显示便笺' $reopened "visible=$(([Desk]::One($target.H)).Visible)"
    } catch { Check '便笺隐藏/重新打开' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }
}

# 删除便笺（主面板垃圾桶）
try {
    $beforeDel = (Read-Json $notesPath).Count
    $uiRoot = Get-MainRoot $proc.Id
    $trash = Uia-FindByType $uiRoot 'Button' | Where-Object { $_.Current.Name -eq '🗑' } | Select-Object -First 1
    if ($trash) { Uia-InvokeElement $trash }
    $deletedNote = Wait-Until { (Read-Json $notesPath).Count -eq ($beforeDel - 1) } 5000
    Check '主面板删除便笺' $deletedNote "json $beforeDel -> $((Read-Json $notesPath).Count)"
} catch { Check '主面板删除便笺' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

$shot4 = Save-Shot (Join-Path $ShotDir '04-after-interactions.png')

# ---------------- 阶段 3：重启持久化 ----------------
$jsonBeforeRestart = Read-Json $notesPath
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Wait-Until { $proc.HasExited } 5000 | Out-Null
Check '应用可正常结束' $proc.HasExited "exited=$($proc.HasExited)"

$proc2 = Start-App $Exe
Log "重启应用 pid=$($proc2.Id)"
$visibleExpected = @($jsonBeforeRestart | Where-Object { -not $_.Collapsed }).Count
$restored = Wait-Until { (Get-Notes $proc2.Id).Count -eq $visibleExpected } 20000
Start-Sleep -Milliseconds 800
$notes2 = Get-Notes $proc2.Id
Check '重启后便笺数量与存档一致' ($notes2.Count -eq $visibleExpected) "窗口=$($notes2.Count) 期望=$visibleExpected（另有隐藏 $($jsonBeforeRestart.Count - $visibleExpected) 张）"
$shot5 = Save-Shot (Join-Path $ShotDir '05-restart.png')

# ---------------- 阶段 4：Windows 便笺导入 ----------------
$uiRoot = Get-MainRoot $proc2.Id
$beforeImport = (Read-Json $notesPath).Count
$stickyDb = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe\LocalState\plum.sqlite'
$hasStickyData = Test-Path $stickyDb

try {
    Uia-ClickButton $uiRoot '导入便笺'
    Start-Sleep -Milliseconds 1500
    $uiRoot = Get-MainRoot $proc2.Id
    $importStatus = (Uia-FindByType $uiRoot 'Text' | ForEach-Object { $_.Current.Name } |
        Where-Object { $_ -like '已导入*' -or $_ -like 'Windows 便笺*' -or $_ -like '未找到*' } |
        Select-Object -First 1)
    $afterImport = (Read-Json $notesPath).Count
    if ($hasStickyData) {
        Check '导入 Windows 便笺' ($afterImport -gt $beforeImport -and $importStatus -like '已导入*') "便笺 $beforeImport -> $afterImport，提示=[$importStatus]"
    } else {
        Check '导入 Windows 便笺' ($importStatus -like '未找到*') "本机无便笺数据，提示=[$importStatus]"
    }
} catch { Check '导入 Windows 便笺' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

# 再点一次：SourceId 去重，数量不再增长
try {
    $uiRoot = Get-MainRoot $proc2.Id
    $countBeforeSecond = (Read-Json $notesPath).Count
    Uia-ClickButton $uiRoot '导入便笺'
    Start-Sleep -Milliseconds 1500
    $countAfterSecond = (Read-Json $notesPath).Count
    $uiRoot = Get-MainRoot $proc2.Id
    $secondStatus = (Uia-FindByType $uiRoot 'Text' | ForEach-Object { $_.Current.Name } |
        Where-Object { $_ -like '已导入*' -or $_ -like 'Windows 便笺*' -or $_ -like '未找到*' } |
        Select-Object -First 1)
    Check '重复导入不重复创建便笺' ($countAfterSecond -eq $countBeforeSecond) "数量仍为 $countAfterSecond，提示=[$secondStatus]"
} catch { Check '重复导入不重复创建便笺' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

# ---------------- 阶段 4.5：导出到 Windows 便笺 ----------------
if (Test-Path $stickyTestDb) {
    try {
        $backupDir = Join-Path $ShotDir 'backups'
        $backupsBefore = @(Get-ChildItem $backupDir -Filter 'plum-*.sqlite' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })

        $uiRoot = Get-MainRoot $proc2.Id
        Uia-ClickButton $uiRoot '导出便笺'
        Start-Sleep -Milliseconds 1500
        $uiRoot = Get-MainRoot $proc2.Id
        $exportStatus = (Uia-FindByType $uiRoot 'Text' | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ -like '已导出*' -or $_ -like '*失败*' } | Select-Object -First 1)
        $jsonAfterExport = Read-Json $notesPath
        $withSource = @($jsonAfterExport | Where-Object { $_.SourceId -like 'stickynotes:*' }).Count
        Check '导出便笺到 Windows 便笺' ($exportStatus -like '已导出*') "提示=[$exportStatus] 带来源标识=$withSource/$($jsonAfterExport.Count)"

        $newBackups = @(Get-ChildItem $backupDir -Filter 'plum-*.sqlite' -ErrorAction SilentlyContinue |
            Where-Object { $backupsBefore -notcontains $_.FullName })
        Check '导出前自动备份系统便笺数据库' ($newBackups.Count -ge 1) "新增备份=$($newBackups.Count)"

        $uiRoot = Get-MainRoot $proc2.Id
        Uia-ClickButton $uiRoot '导出便笺'
        Start-Sleep -Milliseconds 1500
        $uiRoot = Get-MainRoot $proc2.Id
        $secondExport = (Uia-FindByType $uiRoot 'Text' | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ -like '已导出*' } | Select-Object -First 1)
        Check '重复导出只更新不重复创建' ($secondExport -like '已导出 0 张*') "提示=[$secondExport]"
    } catch { Check '导出到 Windows 便笺' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }
} else {
    Check '导出到 Windows 便笺' $false '没有可用的便笺数据库副本'
}

# ---------------- 阶段 5：开机自启 ----------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Get-AutoStartValue {
    return (Get-ItemProperty $runKey -Name DesktopNotes -ErrorAction SilentlyContinue).DesktopNotes
}

Remove-ItemProperty $runKey -Name DesktopNotes -ErrorAction SilentlyContinue
try {
    $uiRoot = Get-MainRoot $proc2.Id
    $checkBox = Uia-FindByType $uiRoot 'CheckBox' | Where-Object { $_.Current.Name -eq '开机自启' } | Select-Object -First 1
    if ($null -eq $checkBox) { throw '未找到“开机自启”复选框' }
    $toggle = $checkBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)

    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    $onOk = Wait-Until { $v = Get-AutoStartValue; return ($v -ne $null -and $v -like '*--autostart*') } 5000
    Check '开启开机自启写入注册表' $onOk "值=$(Get-AutoStartValue)"

    $uiRoot = Get-MainRoot $proc2.Id
    $checkBox = Uia-FindByType $uiRoot 'CheckBox' | Where-Object { $_.Current.Name -eq '开机自启' } | Select-Object -First 1
    $toggle = $checkBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { $toggle.Toggle() }
    $offOk = Wait-Until { (Get-AutoStartValue) -eq $null } 5000
    Check '关闭开机自启移除注册表项' $offOk "值=[$(Get-AutoStartValue)]"
} catch { Check '开机自启开关' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }

# 以 --autostart 启动：只恢复便笺，不显示主面板
$jsonNow = Read-Json $notesPath
$expectedVisible = @($jsonNow | Where-Object { -not $_.Collapsed }).Count
Stop-Process -Id $proc2.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
try {
    $proc3 = Start-App $Exe '--autostart'
    Log "以 --autostart 启动 pid=$($proc3.Id)"
    $ok3 = Wait-Until { (Get-Notes $proc3.Id).Count -eq $expectedVisible } 20000
    Start-Sleep -Milliseconds 800
    $mainWin = Get-MainWindow $proc3.Id
    $mainVisible = if ($mainWin) { $mainWin.Visible } else { $false }
    Check '开机自启启动时不弹主面板' (-not $mainVisible) "主面板存在=$($null -ne $mainWin)，可见=$mainVisible"
    Check '开机自启启动时恢复桌面便笺' $ok3 "便笺窗口=$((Get-Notes $proc3.Id).Count) 期望=$expectedVisible"
    $shot6 = Save-Shot (Join-Path $ShotDir '06-autostart.png')
} catch { Check '开机自启启动行为' $false ("{0} @行{1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber) }
finally {
    if ($proc3) { Stop-Process -Id $proc3.Id -Force -ErrorAction SilentlyContinue }
    Remove-ItemProperty $runKey -Name DesktopNotes -ErrorAction SilentlyContinue
}

# ---------------- 还原用户数据 ----------------
# 保证不把“开机自启”留在测试机上
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name DesktopNotes -ErrorAction SilentlyContinue
if (Test-Path $backupPath) {
    Copy-Item $backupPath $notesPath -Force
    Remove-Item $backupPath -Force
    Log '已还原用户便笺数据'
}

$failedChecks = @($checks | Where-Object { -not $_.ok })
$result = [ordered]@{
    exe = $Exe
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    total = $checks.Count
    passed = $checks.Count - $failedChecks.Count
    failed = $failedChecks.Count
    checks = $checks
    shots = @($shot1, $shot2, $shot3, $shot4, $shot5, $shot6) | Where-Object { $_ }
}
$resultPath = Join-Path $ShotDir 'test-result.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -Path $resultPath -Encoding UTF8
Log "RESULT $($result.passed)/$($result.total) passed"
Log 'done'


















