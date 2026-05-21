param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDirectory,

    [string] $InstallDirectory = "$env:ProgramFiles\XE-Local-AI-Engine",

    [string] $ShortcutDirectory = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\XE-Local-AI-Engine",

    [string] $DesktopDirectory = ([Environment]::GetFolderPath('CommonDesktopDirectory'))
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $ShortcutDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $DesktopDirectory | Out-Null

$hostAgentSource = Join-Path $SourceDirectory 'XE-Local-AI-Engine.HostAgent.Windows.exe'
$hostAgentTarget = Join-Path $InstallDirectory 'XE-Local-AI-Engine.HostAgent.Windows.exe'
Copy-Item -Path $hostAgentSource -Destination $hostAgentTarget -Force

$traySource = Join-Path $SourceDirectory 'XE-Local-AI-Engine.Tray.exe'
$trayTarget = Join-Path $InstallDirectory 'XE-Local-AI-Engine.Tray.exe'
Copy-Item -Path $traySource -Destination $trayTarget -Force

$shell = New-Object -ComObject WScript.Shell

function New-TrayShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [string] $Arguments = ''
    )

    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $trayTarget
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.IconLocation = "$trayTarget,0"
    $shortcut.Save()
}

New-TrayShortcut -Path (Join-Path $ShortcutDirectory 'XE-Local-AI-Engine.lnk')
New-TrayShortcut -Path (Join-Path $ShortcutDirectory 'XE-Local-AI-Engine — Log Mode.lnk') -Arguments '--log'
New-TrayShortcut -Path (Join-Path $DesktopDirectory 'XE-Local-AI-Engine.lnk')
New-TrayShortcut -Path (Join-Path $DesktopDirectory 'XE-Local-AI-Engine — Log Mode.lnk') -Arguments '--log'
