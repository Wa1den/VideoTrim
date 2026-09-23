# Снимок окна для README.
#
#   powershell -File docs\screenshot.ps1 -Exe dist\VideoTrim.exe -Video demo.mp4 -Lang ru -Out docs\screenshot.png
#
# Ролик для снимка генерирует ffmpeg:
#   ffmpeg -f lavfi -i "gradients=s=1920x1080:r=30:n=4:c0=0x2536a8:c1=0xa33be0:c2=0xf2703a:c3=0x18b6c9:speed=0.012:type=spiral:seed=11"
#          -f lavfi -i "sine=frequency=330:sample_rate=48000" -t 60
#          -c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 160k demo.mp4
#
# config.json пользователя подменяется на время съёмки (язык, размер окна) и возвращается
# на место: программа пишет настройки при выходе.

param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Video,
    [ValidateSet("ru", "en")] [string]$Lang = "ru",
    [Parameter(Mandatory)] [string]$Out,
    [string]$Start = "0:12",
    [string]$End = "0:31",
    [int]$Position = 20
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Shot {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
"@
[Shot]::SetProcessDPIAware() | Out-Null

$Exe = (Resolve-Path $Exe).Path
$Video = (Resolve-Path $Video).Path
$Out = [IO.Path]::GetFullPath($Out)

$cfgPath = Join-Path $env:APPDATA "VideoTrim\config.json"
$backup = "$cfgPath.screenshot-backup"
$hadConfig = Test-Path $cfgPath
if ($hadConfig) { Copy-Item $cfgPath $backup -Force }

try {
    $cfg = if ($hadConfig) { Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    foreach ($p in @{ Language = $Lang; Left = 100; Top = 100; Width = 1040; Height = 800; Maximized = $false; CopyStreams = $false; ByQuality = $false }.GetEnumerator()) {
        $cfg | Add-Member -NotePropertyName $p.Key -NotePropertyValue $p.Value -Force
    }
    New-Item -ItemType Directory -Force (Split-Path $cfgPath) | Out-Null
    [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

    $proc = Start-Process $Exe -ArgumentList "`"$Video`"" -PassThru
    Start-Sleep 7
    $proc.Refresh()
    $A = [System.Windows.Automation.AutomationElement]
    $w = $A::FromHandle($proc.MainWindowHandle)

    # фрагмент: поля начала и конца — первые два поля ввода окна
    $edits = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    foreach ($pair in @(@(0, $Start), @(1, $End))) {
        $e = $edits[$pair[0]]
        $e.SetFocus()
        $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($pair[1])
    }
    $edits[0].SetFocus()
    Start-Sleep -Milliseconds 300

    # отметка воспроизведения внутрь фрагмента: кнопка «+1 с» нужное число раз
    $buttons = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $forward = $buttons | Where-Object { $_.Current.Name -like "+1*" } | Select-Object -First 1
    for ($i = 0; $i -lt $Position; $i++) {
        $forward.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 60
    }

    # фокус уходит с полей, чтобы на снимке не было рамки ввода
    $forward.SetFocus()
    Start-Sleep 2

    $r = New-Object Shot+RECT
    [Shot]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [Shot]::PrintWindow($proc.MainWindowHandle, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc)
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

    $proc.CloseMainWindow() | Out-Null
    $proc.WaitForExit(5000) | Out-Null
}
finally {
    if ($hadConfig) { Move-Item $backup $cfgPath -Force } else { Remove-Item $cfgPath -ErrorAction SilentlyContinue }
}
