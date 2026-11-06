# Снимок окна для README.
#
#   powershell -File docs\screenshot.ps1 -Video demo.mp4 -Lang ru -Out docs\screenshot.png
#
# Ролик для снимка генерирует ffmpeg:
#   ffmpeg -f lavfi -i "gradients=s=1920x1080:r=30:n=4:c0=0x2536a8:c1=0xa33be0:c2=0xf2703a:c3=0x18b6c9:speed=0.012:type=spiral:seed=11"
#          -f lavfi -i "sine=frequency=330:sample_rate=48000" -t 60
#          -c:v libx264 -preset fast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 160k demo.mp4
#
# Окно запускается в песочнице через tools\probe.ps1: настройки во временной папке, окно
# скрыто и фокус не забирает, снимок рисует сам WPF. config.json пользователя не трогается.
# Системные кнопки заголовка рисует DWM, на снимке их нет.

param(
    [Parameter(Mandatory)] [string]$Video,
    [ValidateSet("ru", "en")] [string]$Lang = "ru",
    [Parameter(Mandatory)] [string]$Out,
    [string]$Exe = "",
    [string]$Start = "0:12",
    [string]$End = "0:31",
    [string]$Position = "0:20"
)

$ErrorActionPreference = "Stop"
$Video = (Resolve-Path $Video).Path
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

$box = Join-Path ([IO.Path]::GetTempPath()) ("videotrim-shot-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$probe = Join-Path $PSScriptRoot "..\tools\probe.ps1"
$extra = if ($Exe) { @{ Exe = (Resolve-Path $Exe).Path } } else { @{} }

try {
    $steps = "launch;open $Video;start $Start;end $End;seek $Position;snap $Out;close"
    $log = & $probe -Sandbox $box -Lang $Lang -Steps $steps @extra
    $log
    if (-not ($log -match "^ok snap") -or -not (Test-Path $Out)) { throw "снимок не получился" }
}
finally {
    Remove-Item -Recurse -Force $box -ErrorAction SilentlyContinue
}
