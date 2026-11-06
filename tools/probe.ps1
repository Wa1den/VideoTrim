# Проба VideoTrim в песочнице без захвата фокуса.
#
# Программа запускается с --sandbox <папка>: config.json и папка lang берутся из неё, окно
# скрыто через DWM, не активируется и не показывается на панели задач. Настоящий
# %AppData%\VideoTrim не трогается.
#
# К окну снаружи никто не обращается: ни SetForegroundWindow, ни SendKeys, ни UI Automation.
# Каждое из них выводит окно вперёд и уводит клавиатуру из того, чем занят пользователь.
# Команды дописываются в <папка>\probe.in, окно выполняет их само и отвечает в <папка>\probe.out,
# снимки рисует сам WPF. Список команд — в MainWindow.Sandbox.cs.
#
# Шаги через точку с запятой: launch, wait:<секунды> и любая команда окна:
#   open <файл>, start 0:12, end 0:31, seek 0:20, step 3, tab 1, combo format=GIF,
#   check copy=on, bitrate 8000, export <файл>, wait, state, snap <файл.png>, close.
# После каждого шага проверяется активное окно; если его заняло окно песочницы, проба
# останавливается.
#
# Пример:
#   .\probe.ps1 -Sandbox $box -Lang ru -Steps "launch;open D:\demo.mp4;seek 0:20;snap $box\1.png;close"
#
# С -Lang файл config.json песочницы перед launch пишется заново: язык и размер окна,
# остальное по умолчанию. Без -Lang берётся то, что осталось от прошлой пробы.
param(
    [Parameter(Mandatory)] [string]$Sandbox,
    [Parameter(Mandatory)] [string]$Steps,
    [ValidateSet("", "ru", "en")] [string]$Lang = "",
    [int]$Width = 1040,
    [int]$Height = 800,
    [string]$Exe = ""
)

# $PSScriptRoot в значении параметра по умолчанию пуст у powershell -File
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot "..\bin\Release\net9.0-windows\VideoTrim.exe" }
if (-not (Test-Path $Exe)) { "нет $Exe, сначала dotnet build -c Release"; exit 1 }
$Exe = (Resolve-Path $Exe).Path

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Fg {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@

function Owner([IntPtr]$h) {
    $id = 0; [Fg]::GetWindowThreadProcessId($h, [ref]$id) | Out-Null
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p) { $p.ProcessName } else { "?" }
}

$Sandbox = [IO.Path]::GetFullPath($Sandbox)
New-Item -ItemType Directory -Force $Sandbox | Out-Null
$in = Join-Path $Sandbox "probe.in"
$out = Join-Path $Sandbox "probe.out"
function Answers { if (Test-Path $out) { @(Get-Content $out -Encoding UTF8) } else { @() } }

# без launch продолжается проба, начатая раньше: её ответы уже в файле
$answered = @(Answers).Count
$firstFg = [Fg]::GetForegroundWindow()
$proc = $null
$stopped = $false

foreach ($step in $Steps.Split(';')) {
    $step = $step.Trim()
    if ($step.Length -eq 0) { continue }
    $fg = [Fg]::GetForegroundWindow()

    if ($step -eq 'launch') {
        if ($Lang) {
            $cfg = [ordered]@{ Language = $Lang; Width = $Width; Height = $Height; Left = 100; Top = 100;
                               Maximized = $false; CopyStreams = $false; ByQuality = $false; CheckUpdates = $false }
            [IO.File]::WriteAllText((Join-Path $Sandbox "config.json"), ($cfg | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        }
        $started = Get-Date
        $proc = Start-Process $Exe -ArgumentList "--sandbox", "`"$Sandbox`"" -PassThru
        # окно очищает probe.in и probe.out, когда готово принимать команды
        $deadline = $started.AddSeconds(20)
        while ((Get-Date) -lt $deadline -and -not ((Test-Path $out) -and (Get-Item $out).LastWriteTime -ge $started)) {
            Start-Sleep -Milliseconds 200
        }
        $answered = 0
        if ((Get-Date) -lt $deadline) { "запущено" } else { "окно не ответило за 20 с"; $stopped = $true }
    }
    elseif ($step -like 'wait:*') {
        Start-Sleep -Seconds ([int]$step.Substring(5))
    }
    else {
        [IO.File]::AppendAllText($in, "$step`n", [Text.UTF8Encoding]::new($false))
        # открытие, смена языка и ожидание экспорта ждут ffmpeg
        $limit = if ($step -eq 'wait') { 900 } elseif ($step -match '^(open|combo language)') { 60 } else { 20 }
        $deadline = (Get-Date).AddSeconds($limit)
        while (@(Answers).Count -le $answered -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
        $all = @(Answers)
        if ($all.Count -gt $answered) { $all[$answered]; $answered++ } else { "нет ответа на «$step»"; $stopped = $true }
    }

    $now = [Fg]::GetForegroundWindow()
    if ($now -ne $fg) {
        $name = Owner $now
        "  активное окно сменилось на шаге «$step»: $name"
        if ($name -eq 'VideoTrim') { "  ОКНО ПЕСОЧНИЦЫ ЗАБРАЛО ФОКУС, проба остановлена"; $stopped = $true }
    }
    if ($stopped) { break }
}

# окно, оставшееся после остановленной пробы, закрывается: оно невидимо и без команды close продолжило бы работать
if ($stopped -and $proc -and -not $proc.HasExited) { $proc.Kill() }

$lastFg = [Fg]::GetForegroundWindow()
if ($lastFg -eq $firstFg) { "активное окно за пробу не менялось: $(Owner $lastFg)" }
else { "активное окно до пробы: $(Owner $firstFg), после: $(Owner $lastFg)" }
