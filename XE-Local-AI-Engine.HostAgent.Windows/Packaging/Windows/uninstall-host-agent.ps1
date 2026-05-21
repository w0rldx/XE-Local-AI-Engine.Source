param(
    [string] $InstallDirectory = "$env:ProgramFiles\XE-Local-AI-Engine",

    [string] $ShortcutDirectory = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\XE-Local-AI-Engine",

    [string] $DesktopDirectory = ([Environment]::GetFolderPath('CommonDesktopDirectory'))
)

$ErrorActionPreference = 'Stop'

$hostAgentTarget = Join-Path $InstallDirectory 'XE-Local-AI-Engine.HostAgent.Windows.exe'
$trayTarget = Join-Path $InstallDirectory 'XE-Local-AI-Engine.Tray.exe'
$shortcutPaths = @(
    (Join-Path $ShortcutDirectory 'XE-Local-AI-Engine.lnk'),
    (Join-Path $ShortcutDirectory 'XE-Local-AI-Engine — Log Mode.lnk'),
    (Join-Path $DesktopDirectory 'XE-Local-AI-Engine.lnk'),
    (Join-Path $DesktopDirectory 'XE-Local-AI-Engine — Log Mode.lnk')
)

Remove-Item -Path $hostAgentTarget -Force -ErrorAction SilentlyContinue
Remove-Item -Path $trayTarget -Force -ErrorAction SilentlyContinue
foreach ($shortcutPath in $shortcutPaths) {
    Remove-Item -Path $shortcutPath -Force -ErrorAction SilentlyContinue
}
