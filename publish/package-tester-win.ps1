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
#   $env:XE_TESTER_GITHUB_APP_CLIENT_ID = "<GitHub App client ID>"
#   .\publish\package-tester-win.ps1                     # version read from Directory.Build.props
#   .\publish\package-tester-win.ps1 -SkipUpload         # pack only, no GitHub upload
#   .\publish\package-tester-win.ps1 -PublishDraft -ExpectedPortableSha256 <sha256>
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
    # Optional assertion, WITHOUT the leading 'v'. When supplied it must match
    # VersionPrefix-VersionSuffix from Directory.Build.props exactly.
    [string]$Version,
    [string]$GitHubAppClientId = $env:XE_TESTER_GITHUB_APP_CLIENT_ID,
    [string]$TesterRepo = "https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App",
    [switch]$SkipUpload,
    [switch]$PublishDraft,
    [string]$ExpectedPortableSha256
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed (exit code $LASTEXITCODE)."
    }
}

function Get-ProjectVersion {
    [xml]$props = Get-Content "Directory.Build.props"
    $prefix = ($props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }) | Select-Object -First 1
    $suffix = ($props.Project.PropertyGroup.VersionSuffix | Where-Object { $_ }) | Select-Object -First 1
    if (-not $prefix) {
        throw "Could not read VersionPrefix from Directory.Build.props."
    }

    if ($suffix) { return "$prefix-$suffix" }
    return [string]$prefix
}

function Get-GitHubRelease {
    param(
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][string]$RepositorySlug,
        [switch]$AllowMissing
    )

    $releaseOutput = @(gh release view $ReleaseTag --repo $RepositorySlug --json isDraft,tagName,assets 2>&1)
    if ($LASTEXITCODE -eq 0) {
        return ($releaseOutput -join "`n") | ConvertFrom-Json
    }

    $errorText = $releaseOutput -join "`n"
    if ($AllowMissing -and $errorText -match '(?i)(release not found|HTTP 404)') {
        return $null
    }

    throw "GitHub release lookup failed for '$ReleaseTag':`n$errorText"
}

git rev-parse --is-inside-work-tree 2>$null | Out-Null
Assert-LastExitCode "Git repository check"

$dirtyPaths = @(git status --porcelain=v1 --untracked-files=all)
Assert-LastExitCode "Git clean-tree check"
if ($dirtyPaths.Count -gt 0) {
    throw "Refusing to package a dirty working tree. Commit or remove all changes first.`n$($dirtyPaths -join "`n")"
}

$canonicalTesterRepo = "https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App"
if ($TesterRepo.TrimEnd('/') -ne $canonicalTesterRepo) {
    throw "TesterRepo must be the canonical tester repository: $canonicalTesterRepo"
}
$repoSlug = ($TesterRepo -replace '^https://github.com/', '')

# --- Version: single source of truth is Directory.Build.props -----------------------
$projectVersion = Get-ProjectVersion
if ($Version -and $Version -ne $projectVersion) {
    throw "Requested version '$Version' does not match Directory.Build.props version '$projectVersion'."
}
$Version = $projectVersion
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$') {
    throw "Project version '$Version' is not valid SemVer."
}

$tag = "v$Version"

$targetTagCommit = git rev-list -n 1 $tag 2>$null
if ($LASTEXITCODE -eq 0) {
    $headCommit = git rev-parse HEAD
    Assert-LastExitCode "HEAD resolution"
    if ($targetTagCommit -ne $headCommit) {
        throw "Tag '$tag' already exists at $targetTagCommit, not at HEAD $headCommit."
    }
}

$releaseTagsAtHead = @(git tag --points-at HEAD --list "v*")
Assert-LastExitCode "HEAD tag check"
if ($releaseTagsAtHead.Count -gt 0 -and $releaseTagsAtHead -notcontains $tag) {
    throw "HEAD has release tag(s) '$($releaseTagsAtHead -join "', '")', but the project version requires '$tag'."
}
if (-not $SkipUpload -and $releaseTagsAtHead -notcontains $tag) {
    throw "Uploading requires HEAD to carry the exact release tag '$tag'. Use -SkipUpload for a pre-tag package rehearsal."
}

if ($PublishDraft) {
    if ($SkipUpload) {
        throw "-PublishDraft and -SkipUpload cannot be combined."
    }
    if ($ExpectedPortableSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "-PublishDraft requires the SHA-256 recorded after smoke-testing the draft Portable.zip."
    }

    if (-not (Test-Path RELEASE_NOTES.md -PathType Leaf)) {
        throw "RELEASE_NOTES.md is required to publish the draft."
    }

    $draft = Get-GitHubRelease -ReleaseTag $tag -RepositorySlug $repoSlug
    if ($draft.tagName -ne $tag -or -not $draft.isDraft) {
        throw "Release '$tag' must exist as a draft before publication."
    }

    $remotePortableAssets = @($draft.assets | Where-Object { $_.name -like "*Portable*.zip" })
    if ($remotePortableAssets.Count -ne 1) {
        throw "Expected exactly one Portable.zip asset on draft '$tag'; found $($remotePortableAssets.Count)."
    }
    $remotePortableAssetName = $remotePortableAssets[0].name

    $remoteArtifactDir = Join-Path ([IO.Path]::GetTempPath()) "xe-local-ai-engine-remote-artifact-$([Guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Path $remoteArtifactDir | Out-Null
        gh release download $tag `
            --repo $repoSlug `
            --pattern $remotePortableAssetName `
            --dir $remoteArtifactDir
        Assert-LastExitCode "Remote Portable.zip download"

        $downloadedPortable = @(Get-ChildItem -Path $remoteArtifactDir -File)
        if ($downloadedPortable.Count -ne 1) {
            throw "Expected exactly one downloaded Portable.zip for '$tag'; found $($downloadedPortable.Count)."
        }
        $remotePortableHash = (Get-FileHash $downloadedPortable[0].FullName -Algorithm SHA256).Hash
        if ($remotePortableHash -ne $ExpectedPortableSha256) {
            throw "The Portable.zip attached to draft '$tag' does not match the smoke-tested SHA-256."
        }
    }
    finally {
        if (Test-Path $remoteArtifactDir) {
            Remove-Item -Path $remoteArtifactDir -Recurse -Force
        }
    }

    gh release edit $tag --repo $repoSlug --draft=false --notes-file RELEASE_NOTES.md
    Assert-LastExitCode "GitHub draft publication"
    Write-Host ">> Published smoke-tested draft $tag (remote Portable SHA-256 $remotePortableHash)."
    exit 0
}

if (-not $SkipUpload -and -not $env:VPK_TOKEN) {
    throw "VPK_TOKEN is not set. `$env:VPK_TOKEN = '<github token>' (or pass -SkipUpload)."
}

if ([string]::IsNullOrWhiteSpace($GitHubAppClientId) -or
    $GitHubAppClientId -match '^(REPLACE_|CHANGE_ME|TODO)' -or
    $GitHubAppClientId -notmatch '^Iv[0-9A-Za-z.]{14,}$') {
    throw "A real GitHub App client ID is required. Pass -GitHubAppClientId or set XE_TESTER_GITHUB_APP_CLIENT_ID (expected an Iv... client ID; never an App ID or placeholder)."
}

Write-Host ">> Packing version $Version (tag $tag)"

# --- 1. Release validation + SPA build -----------------------------------------------
Push-Location XE-Local-AI-Engine.Client.React
try {
    if (-not (Test-Path .env)) {
        Copy-Item .env.template .env
        Write-Host ">> Seeded .env from .env.template"
    }
    pnpm install --frozen-lockfile
    Assert-LastExitCode "pnpm install"
    pnpm run lint
    Assert-LastExitCode "Frontend lint"
    pnpm run openapi:check
    Assert-LastExitCode "Frontend OpenAPI drift check"
    pnpm run licenses:check
    Assert-LastExitCode "Frontend third-party license check"
    pnpm run test:coverage:check
    Assert-LastExitCode "Frontend tests and coverage gate"
    pnpm audit --prod --audit-level=high
    Assert-LastExitCode "Frontend production dependency audit"
    pnpm run build
    Assert-LastExitCode "Frontend build"
}
finally {
    Pop-Location
}

dotnet restore XE-Local-AI-Engine.slnx
Assert-LastExitCode "Backend restore"
$nugetAuditOutput = @(dotnet package list `
    --project XE-Local-AI-Engine.slnx `
    --vulnerable `
    --include-transitive `
    --format json `
    --no-restore)
Assert-LastExitCode "Backend NuGet vulnerability audit"
$nugetAudit = ($nugetAuditOutput -join "`n") | ConvertFrom-Json
$vulnerablePackages = @(
    foreach ($project in @($nugetAudit.projects)) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if (@($package.vulnerabilities).Count -gt 0) {
                    [pscustomobject]@{
                        Project = $project.path
                        Package = $package.id
                        Version = $package.resolvedVersion
                        Vulnerabilities = @($package.vulnerabilities)
                    }
                }
            }
        }
    }
)
if ($vulnerablePackages.Count -gt 0) {
    $summary = $vulnerablePackages | ConvertTo-Json -Depth 6
    throw "Backend NuGet vulnerability audit found vulnerable packages:`n$summary"
}
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
Assert-LastExitCode "Backend Release build"
dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
Assert-LastExitCode "Backend tests"

# --- 2. Publish (single-file self-contained win-x64, tester update flavor) ----------
dotnet publish XE-Local-AI-Engine.Client\XE-Local-AI-Engine.Client.csproj `
    --configuration Release `
    -p:PublishProfile=win-x64 `
    -p:UpdateChannel=tester
Assert-LastExitCode "dotnet publish"

$publishDir = "XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish"
foreach ($required in @("$publishDir\wwwroot\index.html", "$publishDir\wwwroot\assets")) {
    if (-not (Test-Path $required)) {
        throw "SPA missing from publish output: $required — packing would ship a blank page."
    }
}
Write-Host ">> Publish output verified (SPA present)."

$publishedUpdateConfigPath = Join-Path $publishDir "appsettings.AppUpdate.json"
if (-not (Test-Path $publishedUpdateConfigPath -PathType Leaf)) {
    throw "Published app-update config is missing: $publishedUpdateConfigPath"
}
$publishedUpdateConfig = Get-Content $publishedUpdateConfigPath -Raw | ConvertFrom-Json
if ($publishedUpdateConfig.AppUpdate.Channel -ne "tester" -or
    $publishedUpdateConfig.AppUpdate.GitHubRepositoryUrl.TrimEnd('/') -ne $canonicalTesterRepo) {
    throw "Published app-update config does not target the canonical tester channel/repository."
}
$publishedUpdateConfig.AppUpdate.GitHubAppClientId = $GitHubAppClientId
$publishedUpdateConfig | ConvertTo-Json -Depth 10 | Set-Content $publishedUpdateConfigPath -Encoding utf8
$publishedUpdateConfigText = Get-Content $publishedUpdateConfigPath -Raw
if ($publishedUpdateConfigText -match 'REPLACE_' -or
    [string]::IsNullOrWhiteSpace((ConvertFrom-Json $publishedUpdateConfigText).AppUpdate.GitHubAppClientId)) {
    throw "Published app-update config still contains a placeholder or empty GitHub App client ID."
}
Write-Host ">> Published tester update config verified (canonical repo, supplied client ID, no placeholders)."

# --- 3. Release notes (pinned git-cliff) ---------------------------------------------
$gcVersion = "2.13.1"
$gcSha = "3AE3A5549E85C7AD5B20192EBCFEE4371269DECA51255F6F2F2E051C6541F5CA"  # x86_64-pc-windows-msvc.zip
$gcZip = "git-cliff-$gcVersion-x86_64-pc-windows-msvc.zip"
$gcCacheDir = Join-Path ([IO.Path]::GetTempPath()) "xe-local-ai-engine-git-cliff-$gcVersion"
$gcZipPath = Join-Path $gcCacheDir $gcZip
New-Item -ItemType Directory -Path $gcCacheDir -Force | Out-Null
if (-not (Test-Path $gcZipPath -PathType Leaf)) {
    Invoke-WebRequest "https://github.com/orhun/git-cliff/releases/download/v$gcVersion/$gcZip" -OutFile $gcZipPath
}
$actualGcSha = (Get-FileHash $gcZipPath -Algorithm SHA256).Hash
if ($actualGcSha -ne $gcSha) {
    throw "git-cliff checksum mismatch for cached archive '$gcZipPath' (expected $gcSha, got $actualGcSha)."
}
$gcExtractDir = Join-Path $gcCacheDir "git-cliff-$gcVersion"
Expand-Archive $gcZipPath -DestinationPath $gcExtractDir -Force
$gcExe = Get-ChildItem -Path $gcExtractDir -Recurse -Filter git-cliff.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $gcExe) { throw "git-cliff.exe not found under gc-tmp" }

if ($releaseTagsAtHead -contains $tag) {
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
Assert-LastExitCode "vpk pack"

$portableCandidates = @(Get-ChildItem -Path Releases -Recurse -File -Filter "*Portable*.zip")
if ($portableCandidates.Count -ne 1) {
    throw "Expected exactly one Velopack Portable.zip under Releases; found $($portableCandidates.Count)."
}
$portableHash = (Get-FileHash $portableCandidates[0].FullName -Algorithm SHA256).Hash
Write-Host ">> Portable artifact: $($portableCandidates[0].FullName)"
Write-Host ">> Portable SHA-256: $portableHash"

if ($SkipUpload) {
    Write-Host ">> -SkipUpload: package built, not uploaded. Done."
    exit 0
}

# --- 5. Upload as draft + notes safety net -------------------------------------------
$existingRelease = Get-GitHubRelease -ReleaseTag $tag -RepositorySlug $repoSlug -AllowMissing
if ($null -ne $existingRelease -and -not $existingRelease.isDraft) {
    throw "Release '$tag' is already published. Refusing to merge untested assets into the live update feed."
}

dnx vpk@1.2.0 upload github `
    --repoUrl $TesterRepo `
    --token $env:VPK_TOKEN `
    --channel win `
    --pre `
    --merge `
    --tag $tag
Assert-LastExitCode "vpk upload"

$uploadedRelease = Get-GitHubRelease -ReleaseTag $tag -RepositorySlug $repoSlug
if ($uploadedRelease.tagName -ne $tag -or -not $uploadedRelease.isDraft) {
    throw "Velopack upload did not leave release '$tag' in draft state. Refusing to continue."
}

gh release edit $tag --repo $repoSlug --notes-file RELEASE_NOTES.md
Assert-LastExitCode "gh release edit (notes may be stale on the release)"

Write-Host ">> Tester release $tag uploaded as a DRAFT to $TesterRepo with release notes."
Write-Host ">> Smoke-test the exact Portable.zip above, then publish without rebuilding:"
Write-Host "   .\publish\package-tester-win.ps1 -PublishDraft -ExpectedPortableSha256 $portableHash"
