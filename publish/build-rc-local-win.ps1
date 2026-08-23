# build-rc-local-win.ps1 — build the Windows Velopack Portable.zip LOCALLY, no publish, no token.
#
# Mirrors the win-x64 build-pack job in .github/workflows/release.yml (the source of truth), minus the
# compliance evidence (SBOM/license corpus), the delta-base download, and the upload. Produces the same
# Portable.zip a real release would, so you can smoke-test Windows before cutting the tag.
#
# Run from anywhere inside the repo in PowerShell 7:   pwsh .\publish\build-rc-local-win.ps1
#
# Prerequisites on this machine:
#   - .NET 10 SDK (matching global.json), pnpm, Node 22
#   - To actually RUN the packaged app: ASP.NET Core Runtime 10.0.11+ (x64). The build is
#     framework-dependent and does NOT bundle the runtime — that is by design.
#   - vpk (Velopack CLI) 1.2.0 — this script installs it if missing.

#Requires -Version 7.0
$ErrorActionPreference = "Stop"
Set-Location (& git rev-parse --show-toplevel)    # run from anywhere inside the repo; resolves to the root

function Assert-Ok {
    param([string]$What)
    if ($LASTEXITCODE -ne 0) {
        throw ("{0} failed (exit {1})." -f $What, $LASTEXITCODE)
    }
}

$version    = (& python scripts/read-release-version.py).Trim(); Assert-Ok "read version"
$publishDir = "XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish"
$outDir     = "Releases-win-x64-local"
Write-Host ">> Building XE-Local-AI-Engine $version (win-x64, local, no publish)" -ForegroundColor Cyan

# --- 1. Build the SPA (.env is gitignored; seed it from the committed template) ---
Push-Location XE-Local-AI-Engine.Client.React
try {
    Copy-Item .env.template .env -Force
    pnpm install --frozen-lockfile; Assert-Ok "pnpm install"
    pnpm run build;                 Assert-Ok "SPA build"
}
finally {
    Remove-Item .env -Force -ErrorAction SilentlyContinue
    Pop-Location
}

# --- 2. Wipe the publish leaf so no stale runtime state ships ---
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# --- 3. Publish the framework-dependent Client (win-x64) ---
dotnet publish XE-Local-AI-Engine.Client\XE-Local-AI-Engine.Client.csproj `
    --configuration Release -p:PublishProfile=win-x64 -p:UpdateChannel=tester
Assert-Ok "dotnet publish (Client)"

# --- 4. Publish the C# prerequisite launcher into the SAME dir, then smoke it ---
dotnet publish XE-Local-AI-Engine.WindowsLauncher\XE-Local-AI-Engine.WindowsLauncher.csproj `
    --configuration Release -p:PublishProfile=win-x64 --output $publishDir
Assert-Ok "dotnet publish (WindowsLauncher)"
& scripts\tests\windows-framework-launcher-smoke.ps1 -PublishDirectory $publishDir
Assert-Ok "launcher smoke"

# --- 5. Ensure vpk 1.2.0, then pack the portable artifact ---
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    dotnet tool install -g vpk --version 1.2.0; Assert-Ok "install vpk"
    $env:PATH = $env:PATH + [IO.Path]::PathSeparator + (Join-Path $HOME ".dotnet\tools")
}
"Local rehearsal build of $version." | Set-Content RELEASE_NOTES.md -Encoding utf8
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

vpk pack `
    --outputDir $outDir `
    --packId XE-Local-AI-Engine `
    --packVersion $version `
    --packDir $publishDir `
    --mainExe XE-Local-AI-Engine.WindowsLauncher.exe `
    --runtime win-x64 `
    --channel win `
    --releaseNotes RELEASE_NOTES.md `
    --noInst
Assert-Ok "vpk pack"

$portable = Get-ChildItem -Path $outDir -Recurse -Filter "*Portable*.zip" | Select-Object -First 1
if (-not $portable) { throw "No Portable.zip produced under $outDir." }
$sha = (Get-FileHash $portable.FullName -Algorithm SHA256).Hash
Write-Host ""
Write-Host ">> DONE. Portable package (not published):" -ForegroundColor Green
Write-Host "   $($portable.FullName)"
Write-Host "   SHA-256: $sha"
Write-Host ">> Extract it to a writable folder and run XE-Local-AI-Engine.WindowsLauncher.exe to smoke-test."
