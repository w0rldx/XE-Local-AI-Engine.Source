param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
$launcher = Join-Path $publishPath 'XE-Local-AI-Engine.WindowsLauncher.exe'
$launcherRuntimeConfig = Join-Path $publishPath 'XE-Local-AI-Engine.WindowsLauncher.runtimeconfig.json'
$managedEntryPoint = Join-Path $publishPath 'XE-Local-AI-Engine.Client.dll'
foreach ($required in @($launcher, $launcherRuntimeConfig, $managedEntryPoint)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Windows framework-dependent payload is missing: $required"
    }
}

$process = Start-Process -FilePath $launcher `
    -ArgumentList @('--veloapp-obsolete', '0.0.0') `
    -WorkingDirectory $publishPath `
    -PassThru `
    -NoNewWindow
try {
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        throw 'Windows framework launcher did not forward the Velopack hook and exit within 30 seconds.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Windows framework launcher Velopack hook smoke returned $($process.ExitCode)."
    }
}
finally {
    $process.Dispose()
}

Write-Host 'windows-framework-launcher-smoke.ps1: PASS'
