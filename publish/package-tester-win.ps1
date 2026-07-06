# package-tester-win.ps1 — build + pack + upload a Windows tester release (Velopack).
#
# The versioned form of the manual tester-build flow: builds the SPA, publishes the
# single-file self-contained win-x64 host (tester update flavor), generates release
# notes with a pinned git-cliff, packs with vpk 1.2.0 and uploads to the tester repo.
#
# Prerequisites:
#   - $env:VPK_TOKEN  GitHub PAT with release access to the tester repo (never commit it)
#   - pnpm, dotnet SDK (per global.json), dnx, gh CLI (authenticated) on PATH
#
# Usage (from the repo root):
#   $env:VPK_TOKEN = "<token>"
#   .\publish\package-tester-win.ps1                     # version read from Directory.Build.props
#   .\publish\package-tester-win.ps1 -Version 0.1.0-rc.3.0   # explicit override
#   .\publish\package-tester-win.ps1 -SkipUpload         # pack only, no GitHub upload
#
# Gotchas encoded here (learned the hard way):
#   - .env is gitignored: a fresh checkout builds the SPA without VITE_ values. The env
#     schema check runs at APP STARTUP in the browser (not during `vite build`), so a
#     missing value used to surface only as a blank page. Seed .env from .env.template.
#   - vpk 1.2.0 `pack` has NO --pre flag; prerelease state rides the SemVer suffix.
#     --pre IS valid on `vpk upload github`.
#   - If HEAD is already tagged, git-cliff --unreleased is EMPTY (commits-after-latest-tag)
#     and you ship notes with only a date. Mirror scripts/generate-release-notes.sh:
#     --latest when HEAD is tagged, else --unreleased --tag.
#   - vpk sets the GH release body only when it CREATES the release; a re-upload to an
#     existing release leaves the body stale. `gh release edit` forces the notes.

[CmdletBinding()]
param(
    # Pack version WITHOUT the leading 'v' (e.g. 0.1.0-rc.3.0). Defaults to
    # VersionPrefix-VersionSuffix from Directory.Build.props so the package can
    # never disagree with the assembly/self-update version.
    [string]$Version,
    [string]$TesterRepo = "https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App",
    [switch]$SkipUpload
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not $SkipUpload -and -not $env:VPK_TOKEN) {
    throw "VPK_TOKEN is not set. `$env:VPK_TOKEN = '<github token>' (or pass -SkipUpload)."
}

# --- Version: single source of truth is Directory.Build.props -----------------------
if (-not $Version) {
    [xml]$props = Get-Content "Directory.Build.props"
    $prefix = ($props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }) | Select-Object -First 1
    $suffix = ($props.Project.PropertyGroup.VersionSuffix | Where-Object { $_ }) | Select-Object -First 1
    if (-not $prefix) { throw "Could not read VersionPrefix from Directory.Build.props." }
    $Version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
}
$tag = "v$Version"
Write-Host ">> Packing version $Version (tag $tag)"

# --- 1. SPA build --------------------------------------------------------------------
Push-Location XE-Local-AI-Engine.Client.React
try {
    if (-not (Test-Path .env)) {
        Copy-Item .env.template .env
        Write-Host ">> Seeded .env from .env.template"
    }
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
    pnpm build
    if ($LASTEXITCODE -ne 0) { throw "pnpm build failed" }
}
finally {
    Pop-Location
}

# --- 2. Publish (single-file self-contained win-x64, tester update flavor) ----------
dotnet publish XE-Local-AI-Engine.Client\XE-Local-AI-Engine.Client.csproj `
    --configuration Release `
    -p:PublishProfile=win-x64 `
    -p:UpdateChannel=tester
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$publishDir = "XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish"
foreach ($required in @("$publishDir\wwwroot\index.html", "$publishDir\wwwroot\assets")) {
    if (-not (Test-Path $required)) {
        throw "SPA missing from publish output: $required — packing would ship a blank page."
    }
}
Write-Host ">> Publish output verified (SPA present)."

# --- 3. Release notes (pinned git-cliff) ---------------------------------------------
$gcVersion = "2.13.1"
$gcSha = "3AE3A5549E85C7AD5B20192EBCFEE4371269DECA51255F6F2F2E051C6541F5CA"  # x86_64-pc-windows-msvc.zip
$gcZip = "git-cliff-$gcVersion-x86_64-pc-windows-msvc.zip"
if (-not (Test-Path "gc-tmp")) {
    Invoke-WebRequest "https://github.com/orhun/git-cliff/releases/download/v$gcVersion/$gcZip" -OutFile $gcZip
    if ((Get-FileHash $gcZip -Algorithm SHA256).Hash -ne $gcSha) { throw "git-cliff checksum mismatch" }
    Expand-Archive $gcZip -DestinationPath gc-tmp -Force
    Remove-Item $gcZip
}
$gcExe = Get-ChildItem -Recurse -Filter git-cliff.exe gc-tmp | Select-Object -First 1 -ExpandProperty FullName
if (-not $gcExe) { throw "git-cliff.exe not found under gc-tmp" }

git describe --exact-match --tags HEAD 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    & $gcExe --latest --strip header -o RELEASE_NOTES.md
} else {
    & $gcExe --unreleased --tag $tag --strip header -o RELEASE_NOTES.md
}
if (-not (Select-String -Path RELEASE_NOTES.md -Pattern '^### ' -Quiet)) {
    throw "RELEASE_NOTES.md has no commit sections — empty range. Check tags/branch."
}
Write-Host ">> Release notes:"
Get-Content RELEASE_NOTES.md

# --- 4. Pack -------------------------------------------------------------------------
dnx vpk@1.2.0 pack `
    --packId XE-Local-AI-Engine `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe XE-Local-AI-Engine.Client.exe `
    --channel win `
    --releaseNotes RELEASE_NOTES.md `
    --noInst
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

if ($SkipUpload) {
    Write-Host ">> -SkipUpload: package built, not uploaded. Done."
    exit 0
}

# --- 5. Upload + notes safety net ----------------------------------------------------
dnx vpk@1.2.0 upload github `
    --repoUrl $TesterRepo `
    --token $env:VPK_TOKEN --channel win --pre --publish
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }

$repoSlug = ($TesterRepo -replace '^https://github.com/', '')
gh release edit $Version --repo $repoSlug --notes-file RELEASE_NOTES.md
if ($LASTEXITCODE -ne 0) { throw "gh release edit failed (notes may be stale on the release)" }

Write-Host ">> Tester release $Version uploaded to $TesterRepo with release notes."
