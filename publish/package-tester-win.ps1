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
#   - `dotnet test` exits 0 when ZERO test projects enrol (a misnamed project, or a lost
#     IsTestingPlatformApplication / TestingPlatformDotnetTestSupport property). The only signal is
#     the absence of an MTP "Passed!"/"Failed!" summary, so the test output is grepped for it.
#     Without that check a silent green ships as a validated release.
#   - $env:TZ does NOT reproduce CI's TZ=Europe/Berlin here. TZ is a Unix-only mechanism in .NET
#     (TimeZoneInfo.Unix.NonAndroid.cs reads it; TimeZoneInfo.Windows.cs resolves the local zone from
#     kernel32!GetDynamicTimeZoneInformation and reads no environment variable at all). The machine's
#     zone is asserted instead — see -AllowUtcTestTimeZone.

#Requires -Version 7.0
# PowerShell 7+, not Windows PowerShell 5.1: this script sets $ErrorActionPreference = "Stop" AND redirects
# native-command stderr (`gh ... 2>&1`) to inspect a 404. Under 5.1 that combination turns the redirected
# stderr into a terminating error, which would break the "release not found" path in Get-GitHubReleaseByTag.
# (The sibling publish/windows/uninstall-xe-local-ai-engine.ps1 requires only 5.1 — it does neither.)

[CmdletBinding()]
param(
    # Optional assertion, WITHOUT the leading 'v'. When supplied it must match
    # VersionPrefix-VersionSuffix from Directory.Build.props exactly.
    [string]$Version,
    [string]$GitHubAppClientId = $env:XE_TESTER_GITHUB_APP_CLIENT_ID,
    [string]$TesterRepo = "https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App",
    [switch]$SkipUpload,
    [switch]$PublishDraft,
    [string]$ExpectedPortableSha256,
    # Escape hatch for a genuinely UTC packaging machine: accepts the reduced time-zone coverage
    # instead of failing. Prefer changing the machine zone over passing this.
    [switch]$AllowUtcTestTimeZone
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

function Get-GitHubReleaseByTag {
    param(
        [Parameter(Mandatory)][string]$ReleaseTag,
        [Parameter(Mandatory)][string]$RepositorySlug
    )

    $releaseOutput = @(gh release view $ReleaseTag --repo $RepositorySlug --json isDraft,tagName,name,assets 2>&1)
    if ($LASTEXITCODE -eq 0) {
        return ($releaseOutput -join "`n") | ConvertFrom-Json
    }

    $errorText = $releaseOutput -join "`n"
    if ($errorText -match '(?i)(release not found|HTTP 404)') {
        return $null
    }

    throw "GitHub release lookup failed for '$ReleaseTag':`n$errorText"
}

# Resolve the tester release for a version REGARDLESS of how its tag was spelled. Historical tester releases
# carry a BARE tag ("0.1.0-rc.4.1") and only their release NAME has the 'v' — `gh release view v0.1.0-rc.4.1`
# answers "release not found" for the currently-live release. This script uploads with `--tag v<version>`, so
# newly created releases are v-prefixed while every pre-existing one is bare; both forms must be handled
# indefinitely. A lookup that probes only one form makes the already-published guard blind to the live
# release and lets `vpk upload --merge` push untested assets into a shipped update feed.
function Find-GitHubRelease {
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][string]$RepositorySlug
    )

    $candidateTags = @("v$ReleaseVersion", $ReleaseVersion)
    foreach ($candidateTag in $candidateTags) {
        $release = Get-GitHubReleaseByTag -ReleaseTag $candidateTag -RepositorySlug $RepositorySlug
        if ($null -ne $release) { return $release }
    }

    # Fallback: a release whose NAME identifies the version but whose tag is spelled some third way.
    $listOutput = @(gh release list --repo $RepositorySlug --limit 200 --json name,tagName 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release list failed for '$RepositorySlug':`n$($listOutput -join "`n")"
    }
    $matchingRelease = @(($listOutput -join "`n") | ConvertFrom-Json | Where-Object {
        $candidateTags -contains $_.name -or $candidateTags -contains $_.tagName
    }) | Select-Object -First 1
    if ($null -ne $matchingRelease) {
        # Re-fetch by the real tag: `gh release list --json` cannot return the assets array.
        return Get-GitHubReleaseByTag -ReleaseTag $matchingRelease.tagName -RepositorySlug $RepositorySlug
    }

    return $null
}

function Get-RemoteTagCommit {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$RepositoryUrl
    )

    $tagRefs = @(git ls-remote --exit-code --tags $RepositoryUrl "refs/tags/$Tag" "refs/tags/$Tag^{}" 2>&1)
    if ($LASTEXITCODE -eq 2) {
        return $null
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve tag '$Tag' in the canonical source repository '$RepositoryUrl':`n$($tagRefs -join "`n")"
    }

    $parsedRefs = @(
        foreach ($line in $tagRefs) {
            if ($line -match '^([0-9a-fA-F]{40,64})\s+(.+)$') {
                [pscustomobject]@{ Commit = $Matches[1]; Ref = $Matches[2] }
            }
        }
    )
    $peeledTag = $parsedRefs | Where-Object Ref -EQ "refs/tags/$Tag^{}" | Select-Object -First 1
    $directTag = $parsedRefs | Where-Object Ref -EQ "refs/tags/$Tag" | Select-Object -First 1
    $resolvedTag = if ($null -ne $peeledTag) { $peeledTag } else { $directTag }
    if ($null -eq $resolvedTag) {
        throw "Canonical source tag lookup for '$Tag' returned no parseable tag ref."
    }

    return $resolvedTag.Commit
}

function Assert-RemoteSourceTagAtHead {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$HeadCommit,
        [Parameter(Mandatory)][string]$RepositoryUrl
    )

    $remoteTagCommit = Get-RemoteTagCommit -Tag $Tag -RepositoryUrl $RepositoryUrl
    if ([string]::IsNullOrWhiteSpace($remoteTagCommit)) {
        throw "Tag '$Tag' does not exist in the canonical source repository '$RepositoryUrl'. Push the source tag before publishing the tester draft."
    }
    if ($remoteTagCommit -ne $HeadCommit) {
        throw "Canonical source tag '$Tag' resolves to $remoteTagCommit, not HEAD $HeadCommit. Refusing to publish a draft whose source tag does not identify this commit."
    }
}

function Get-ViteReleaseEnvironmentConflict {
    param([Parameter(Mandatory)][string]$FrontendDirectory)

    $conflicts = [System.Collections.Generic.List[string]]::new()
    foreach ($fileName in @('.env', '.env.local', '.env.production', '.env.production.local')) {
        if (Test-Path (Join-Path $FrontendDirectory $fileName) -PathType Leaf) {
            $conflicts.Add($fileName)
        }
    }
    foreach ($variable in @(Get-ChildItem Env: | Where-Object Name -Like 'VITE_*')) {
        $conflicts.Add("environment variable $($variable.Name)")
    }

    return @($conflicts)
}

function Get-ExpectedVelopackAsset {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Assets)

    # vpk 1.2.0 writes a build-local upload manifest into --outputDir that it never uploads. Verified on
    # Windows 11 2026-08-02: the rc.4.1 pack output still carries Releases\assets.win.json alongside its five
    # artifacts (same mtime, same vpk pin — 1.2.0 has been pinned since 5da4b16a, 2026-07-06), while the
    # published 0.1.0-rc.4.1 release carries exactly those five and no assets.win.json. Its contents are a
    # vpk-internal index of what to upload ([{RelativeFileName,Type}] for Delta/Full/Portable), not a channel
    # asset. So it must be filtered from the INVENTORY, not promoted to a sixth definition: the other three
    # call sites (the SHA-256 manifest, the remote draft's assets, the downloaded assets) legitimately hold
    # five, and requiring a sixth would fail every one of them in the opposite direction. Anything else
    # unrecognised still throws below.
    $localOnlyVpkArtifacts = @('assets.win.json')
    $assetList = @(
        $Assets |
            Where-Object { $_ } |
            Where-Object { $localOnlyVpkArtifacts -cnotcontains ([string]$_.Name) }
    )
    $definitions = @(
        [pscustomobject]@{ Label = 'Portable.zip'; Matches = { param($name) $name -like '*Portable*.zip' } },
        [pscustomobject]@{ Label = 'full.nupkg'; Matches = { param($name) $name -like '*-full.nupkg' } },
        [pscustomobject]@{ Label = 'delta.nupkg'; Matches = { param($name) $name -like '*-delta.nupkg' } },
        [pscustomobject]@{ Label = 'releases.win.json'; Matches = { param($name) $name -ceq 'releases.win.json' } },
        [pscustomobject]@{ Label = 'RELEASES'; Matches = { param($name) $name -ceq 'RELEASES' } }
    )

    $resolved = [System.Collections.Generic.List[object]]::new()
    foreach ($definition in $definitions) {
        $assetMatches = @($assetList | Where-Object { & $definition.Matches ([string]$_.Name) })
        if ($assetMatches.Count -ne 1) {
            throw "Expected exactly one Velopack $($definition.Label) asset; found $($assetMatches.Count)."
        }
        $resolved.Add($assetMatches[0])
    }

    $resolvedNames = @($resolved | ForEach-Object { [string]$_.Name })
    $unexpected = @($assetList | Where-Object { $resolvedNames -cnotcontains ([string]$_.Name) })
    if ($unexpected.Count -gt 0 -or $assetList.Count -ne $definitions.Count) {
        $unexpectedNames = @($unexpected | ForEach-Object { [string]$_.Name })
        throw "Velopack release contains unexpected or duplicate assets: $($unexpectedNames -join ', '). Expected only the five channel assets."
    }

    return @($resolved)
}

function Assert-VelopackAssetHash {
    param(
        [Parameter(Mandatory)][object[]]$Files,
        [Parameter(Mandatory)][object[]]$ExpectedAssets
    )

    $actualFiles = @(Get-ExpectedVelopackAsset -Assets $Files)
    $expected = @(Get-ExpectedVelopackAsset -Assets $ExpectedAssets)
    foreach ($file in $actualFiles) {
        $expectedAsset = $expected | Where-Object Name -CEQ $file.Name | Select-Object -First 1
        $expectedHash = [string]$expectedAsset.Sha256
        if ($expectedHash -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "Expected SHA-256 for Velopack asset '$($file.Name)' is missing or malformed."
        }
        $actualHash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Velopack asset '$($file.Name)' SHA-256 mismatch (expected $expectedHash, got $actualHash)."
        }
    }
}

function Assert-VelopackManifestSource {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$HeadCommit,
        [Parameter(Mandatory)][string]$Tag
    )

    if ([string]$Manifest.SourceCommit -cne $HeadCommit) {
        throw "Velopack manifest source commit '$($Manifest.SourceCommit)' does not match HEAD $HeadCommit. Refusing a stale same-version manifest."
    }
    if ([string]$Manifest.SourceTag -cne $Tag) {
        throw "Velopack manifest source tag '$($Manifest.SourceTag)' does not match required tag '$Tag'."
    }
}

function Invoke-VelopackPreviousReleaseDownload {
    param(
        [Parameter(Mandatory)][string]$RepositoryUrl,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$Token
    )

    dnx vpk@1.2.0 download github `
        --repoUrl $RepositoryUrl `
        --outputDir $OutputDirectory `
        --channel win `
        --pre `
        --token $Token
    if ($LASTEXITCODE -ne 0) {
        throw "Previous tester release download failed (exit code $LASTEXITCODE). This repository already has published tester releases, so packing without a delta base is not allowed."
    }
}

function Assert-VelopackPreviousReleaseSeed {
    param([Parameter(Mandatory)][string]$OutputDirectory)

    $previousFullPackages = @(Get-ChildItem -Path $OutputDirectory -Recurse -File -Filter '*-full.nupkg')
    if ($previousFullPackages.Count -eq 0) {
        throw "Velopack download produced no previous full.nupkg. This repository already has published tester releases, so this is not a first-release pack and must have a delta base."
    }
    if ($previousFullPackages.Count -ne 1) {
        throw "Expected exactly one previous full.nupkg in the clean Velopack output directory; found $($previousFullPackages.Count)."
    }

    Write-Host ">> Previous Velopack release seeded for delta generation: $($previousFullPackages[0].Name)"
}

git rev-parse --is-inside-work-tree 2>$null | Out-Null
Assert-LastExitCode "Git repository check"

$dirtyPaths = @(git status --porcelain=v1 --untracked-files=all)
Assert-LastExitCode "Git clean-tree check"
if ($dirtyPaths.Count -gt 0) {
    throw "Refusing to package a dirty working tree. Commit or remove all changes first.`n$($dirtyPaths -join "`n")"
}

$canonicalTesterRepo = "https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App"
$canonicalSourceRepo = "https://github.com/w0rldx/XE-Local-AI-Engine.git"
if ($TesterRepo.TrimEnd('/') -ne $canonicalTesterRepo) {
    throw "TesterRepo must be the canonical tester repository: $canonicalTesterRepo"
}
# Normalise ONCE and use the normalised value everywhere below. The gate above compares a .TrimEnd('/')ed
# value, so "…/Tester-App/" passes it — deriving the slug from the raw parameter then yielded
# "w0rldx/XE-Local-AI-Engine.Tester-App/", which gh rejects only AFTER the full build/test/publish/pack.
$TesterRepo = $TesterRepo.TrimEnd('/')
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
$headCommit = git rev-parse HEAD
Assert-LastExitCode "HEAD resolution"

$targetTagCommit = git rev-list -n 1 $tag 2>$null
if ($LASTEXITCODE -eq 0) {
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

    Assert-RemoteSourceTagAtHead -Tag $tag -HeadCommit $headCommit -RepositoryUrl $canonicalSourceRepo

    $assetManifestPath = Join-Path "publish\dist" "XE-Local-AI-Engine-$Version-win.sha256.json"
    if (-not (Test-Path $assetManifestPath -PathType Leaf)) {
        throw "Velopack SHA-256 manifest is missing: $assetManifestPath. Publish the draft from the same verified pack output that created it."
    }
    $assetManifest = Get-Content $assetManifestPath -Raw | ConvertFrom-Json
    if ($assetManifest.Version -ne $Version) {
        throw "Velopack SHA-256 manifest version '$($assetManifest.Version)' does not match '$Version'."
    }
    Assert-VelopackManifestSource -Manifest $assetManifest -HeadCommit $headCommit -Tag $tag
    $expectedVelopackAssets = @(Get-ExpectedVelopackAsset -Assets @($assetManifest.Assets))
    $expectedPortableAsset = $expectedVelopackAssets | Where-Object Name -Like '*Portable*.zip' | Select-Object -First 1
    if ($expectedPortableAsset.Sha256 -ne $ExpectedPortableSha256) {
        throw "The smoke-tested Portable.zip SHA-256 does not match the verified pack manifest for version '$Version'."
    }

    $draft = Find-GitHubRelease -ReleaseVersion $Version -RepositorySlug $repoSlug
    if ($null -eq $draft -or -not $draft.isDraft) {
        throw "Release for version '$Version' must exist as a draft before publication (looked for tags 'v$Version' and '$Version')."
    }
    # The remote tag may be bare or v-prefixed; every gh call below must use the tag that actually exists.
    $remoteTag = $draft.tagName

    $null = Get-ExpectedVelopackAsset -Assets @($draft.assets)

    $remoteArtifactDir = Join-Path ([IO.Path]::GetTempPath()) "xe-local-ai-engine-remote-artifact-$([Guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Path $remoteArtifactDir | Out-Null
        gh release download $remoteTag `
            --repo $repoSlug `
            --dir $remoteArtifactDir
        Assert-LastExitCode "Remote Velopack asset download"

        $downloadedVelopackAssets = @(Get-ExpectedVelopackAsset -Assets @(Get-ChildItem -Path $remoteArtifactDir -File))
        Assert-VelopackAssetHash -Files $downloadedVelopackAssets -ExpectedAssets $expectedVelopackAssets
        $downloadedPortable = $downloadedVelopackAssets | Where-Object Name -Like '*Portable*.zip' | Select-Object -First 1
        $remotePortableHash = (Get-FileHash $downloadedPortable.FullName -Algorithm SHA256).Hash
        if ($remotePortableHash -ne $ExpectedPortableSha256) {
            throw "The Portable.zip attached to draft '$remoteTag' does not match the smoke-tested SHA-256."
        }
    }
    finally {
        if (Test-Path $remoteArtifactDir) {
            Remove-Item -Path $remoteArtifactDir -Recurse -Force
        }
    }

    gh release edit $remoteTag --repo $repoSlug --draft=false --notes-file RELEASE_NOTES.md
    Assert-LastExitCode "GitHub draft publication"
    Write-Host ">> Published smoke-tested draft $remoteTag after verifying all five Velopack asset SHA-256 digests (Portable $remotePortableHash)."
    exit 0
}

if (-not $env:VPK_TOKEN) {
    throw "VPK_TOKEN is not set. `$env:VPK_TOKEN = '<github token>'. It is required to download the previous release from the private tester repository, including for -SkipUpload packs."
}

# Client-ID policy remains the sole -SkipUpload credential relaxation. A SUPPLIED id is always validated
# (a placeholder is an error, not an absence); an ABSENT id is tolerated only for a rehearsal, which then
# bakes no client ID at all — AppUpdateChannelOptions.IsConfigured stays false, the updater ships inert
# rather than placeholder-configured, and the artifact is stamped non-shippable below.
$hasGitHubAppClientId = -not [string]::IsNullOrWhiteSpace($GitHubAppClientId)
if ($hasGitHubAppClientId) {
    if ($GitHubAppClientId -match '^(REPLACE_|CHANGE_ME|TODO)' -or
        $GitHubAppClientId -notmatch '^Iv[0-9A-Za-z.]{14,}$') {
        throw "GitHubAppClientId '$GitHubAppClientId' is not a real GitHub App client ID (expected an Iv... client ID; never an App ID or placeholder)."
    }
}
elseif (-not $SkipUpload) {
    throw "A real GitHub App client ID is required to upload. Pass -GitHubAppClientId or set XE_TESTER_GITHUB_APP_CLIENT_ID (expected an Iv... client ID; never an App ID or placeholder)."
}

$isRehearsalPackage = $SkipUpload -and -not $hasGitHubAppClientId
$rehearsalMarkerName = "REHEARSAL-DO-NOT-SHIP.txt"

Write-Host ">> Packing version $Version (tag $tag)"
if ($isRehearsalPackage) {
    Write-Warning "No GitHub App client ID supplied: building a REHEARSAL package. Its in-app updater is inert and it carries a $rehearsalMarkerName marker. Do not distribute it."
}

# --- 1. Release validation + SPA build -----------------------------------------------
Push-Location XE-Local-AI-Engine.Client.React
$createdReleaseEnv = $false
try {
    $viteEnvironmentConflicts = @(Get-ViteReleaseEnvironmentConflict -FrontendDirectory (Get-Location).Path)
    if ($viteEnvironmentConflicts.Count -gt 0) {
        throw "Refusing to build with local Vite environment overrides that can influence production output:`n$($viteEnvironmentConflicts -join "`n")"
    }

    git ls-files --error-unmatch -- .env.template | Out-Null
    Assert-LastExitCode "Committed Vite release environment contract check"
    Copy-Item .env.template .env
    $createdReleaseEnv = $true
    Write-Host ">> Materialized the committed .env.template as the isolated production Vite environment."

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
    if ($createdReleaseEnv -and (Test-Path .env -PathType Leaf)) {
        Remove-Item .env -Force
    }
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
# Every level filters out $null before counting. `dotnet package list --vulnerable` omits the `frameworks`
# key entirely from a project with no vulnerabilities, and @($null).Count is 1 in PowerShell — NOT 0. Without
# the Where-Object guards each clean project yields two phantom entries (one per missing package collection)
# whose @($package.vulnerabilities).Count is @($null).Count = 1, so a perfectly clean solution "fails" the
# audit with one null-filled row per project and the release never reaches the SPA build.
$vulnerablePackages = @(
    foreach ($project in @($nugetAudit.projects | Where-Object { $_ })) {
        foreach ($framework in @($project.frameworks | Where-Object { $_ })) {
            $frameworkPackages = @($framework.topLevelPackages) + @($framework.transitivePackages)
            foreach ($package in @($frameworkPackages | Where-Object { $_ })) {
                $packageVulnerabilities = @($package.vulnerabilities | Where-Object { $_ })
                if ($packageVulnerabilities.Count -gt 0) {
                    [pscustomobject]@{
                        Project = $project.path
                        Package = $package.id
                        Version = $package.resolvedVersion
                        Vulnerabilities = $packageVulnerabilities
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

# Non-UTC time-zone exposure — the local equivalent of TZ=Europe/Berlin in
# .github/workflows/build-and-test.yml. Setting $env:TZ here would do nothing (.NET only honours TZ on
# Unix), so the machine's zone is asserted instead: on a UTC box the off-by-offset class of bug the CI
# variable exists to catch (CapabilityReporterTests / RunningModelSnapshotMapper) re-hides silently.
$testTimeZone = [System.TimeZoneInfo]::Local
$testUtcOffset = $testTimeZone.GetUtcOffset([DateTimeOffset]::Now)
if ($testUtcOffset -eq [TimeSpan]::Zero -and -not $AllowUtcTestTimeZone) {
    throw "Backend tests must run in a non-UTC time zone to expose off-by-offset bugs, but the local zone '$($testTimeZone.Id)' is currently at UTC+00:00. Set the machine zone (e.g. tzutil /s ""W. Europe Standard Time"") or pass -AllowUtcTestTimeZone to accept the reduced coverage."
}
Write-Host ">> Backend tests run in time zone '$($testTimeZone.Id)' (current UTC offset $testUtcOffset)."

$testOutputPath = Join-Path ([IO.Path]::GetTempPath()) "xe-local-ai-engine-backend-tests-$([Guid]::NewGuid().ToString('N')).log"
try {
    # Tee-Object keeps the run streaming to the console (a human watches this) while capturing it for the
    # hollow-gate grep below.
    dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1 |
        Tee-Object -FilePath $testOutputPath
    Assert-LastExitCode "Backend tests"

    # Hollow-gate guard, ported from .github/workflows/build-and-test.yml (which is disabled, making this
    # script the only gate). MTP always emits a "Passed!" or "Failed!" summary per suite that ran. If zero
    # suites enrol, the output carries no summary line and `dotnet test` still exits 0 — this catches that
    # silent green before it becomes a released package.
    if (-not (Test-Path $testOutputPath -PathType Leaf) -or
        -not (Select-String -Path $testOutputPath -Pattern 'Passed!|Failed!' -Quiet)) {
        throw "Backend tests produced no test-suite summary — zero test projects enrolled. Check project names or the TestingPlatformDotnetTestSupport property in Directory.Build.props."
    }
    Write-Host ">> Backend test summary markers found (test projects really enrolled)."
}
finally {
    Remove-Item $testOutputPath -Force -ErrorAction SilentlyContinue
}

# --- 2. Publish (single-file self-contained win-x64, tester update flavor) ----------
# The publish directory is wiped FIRST because `dotnet publish` never removes stale files from it, and the app
# writes runtime state next to its own executable. Running the published exe straight out of this directory —
# a manual smoke test — leaves artifacts behind that the next pack silently ships:
#   - logs\        ResolveLogFileDirectory (LoggerExtensions.cs) uses NodeData:Directory, which ONLY desktop mode
#                  layers in; a non-desktop run falls back to ContentRootPath = the exe's own folder. That is how
#                  0.1.0-rc.5.0 shipped a maintainer log containing G:\Repos\... source paths and a stack trace.
#   - dead-letter-queue\  FileDeadLetterStore roots on AppContext.BaseDirectory UNCONDITIONALLY and calls
#                  Directory.CreateDirectory in its constructor, so every launch recreates it regardless of mode.
# Both are pure write-sinks that recreate themselves on demand, so deleting them cannot break a tester install.
# The same ContentRootPath fallback also governs dp-keys\ (ConfigureServices.cs) and, behind the desktop flag,
# node.sqlite / node.key — so the next stray run could leak real secrets rather than just paths. Wiping the
# directory removes the accumulation mechanism itself instead of blacklisting today's known offenders.
#
# Only the publish leaf is cleared: incremental state lives in obj\ and bin\<config>\<tfm>\<rid>\, so this forces
# a fresh output copy, not a full rebuild. Nothing carries content across runs — every consumer below (the SPA
# check, the update-config rewrite, the rehearsal marker, and `vpk pack --packDir`) reads only what this publish
# produces.
$publishDir = "XE-Local-AI-Engine.Client\bin\Release\net10.0\win-x64\publish"
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
    Write-Host ">> Cleared the previous publish output so no stale runtime state can ride into the package."
}

dotnet publish XE-Local-AI-Engine.Client\XE-Local-AI-Engine.Client.csproj `
    --configuration Release `
    -p:PublishProfile=win-x64 `
    -p:UpdateChannel=tester
Assert-LastExitCode "dotnet publish"

foreach ($required in @("$publishDir\wwwroot\index.html", "$publishDir\wwwroot\assets")) {
    if (-not (Test-Path $required)) {
        throw "SPA missing from publish output: $required — packing would ship a blank page."
    }
}
Write-Host ">> Publish output verified (SPA present)."

# Tripwire for runtime state that must never ship. The wipe above removes the accumulation mechanism; this catches
# anything created DURING this run (a smoke test added between publish and pack, or a new code path that writes
# beside the executable). It fails the build rather than deleting quietly: a silent scrub is how the original leak
# stayed invisible, and a maintainer who sees this throw learns the publish directory was executed in.
#
# These patterns are the state/secret classes specifically — logs and the dead-letter queue leak host paths, while
# dp-keys / node.sqlite / node.key / *.enc would leak real secrets. A strict allow-list of expected filenames would
# be stronger still, but authoring one requires a captured inventory of a real win-x64 publish; add it here once
# that inventory exists rather than guessing at it and failing a release on a legitimate file.
$forbiddenPublishArtifacts = @(
    @{ Pattern = "logs";             Reason = "log output from running the app in the publish directory (leaks host paths)" },
    @{ Pattern = "dead-letter-queue"; Reason = "dead-letter queue created beside the executable on every launch" },
    @{ Pattern = "dp-keys";          Reason = "Data Protection key ring" },
    @{ Pattern = "*.sqlite";         Reason = "node database" },
    @{ Pattern = "*.sqlite-wal";     Reason = "node database write-ahead log" },
    @{ Pattern = "*.sqlite-shm";     Reason = "node database shared memory" },
    @{ Pattern = "node.key";         Reason = "node encryption key" },
    @{ Pattern = "*.enc";            Reason = "encrypted secret store (HF token, GitHub token, provider credentials)" },
    @{ Pattern = "desktop-port.txt"; Reason = "persisted desktop loopback port" },
    @{ Pattern = "*.log";            Reason = "log file" }
)
$leakedArtifacts = [System.Collections.Generic.List[string]]::new()
foreach ($forbidden in $forbiddenPublishArtifacts) {
    foreach ($match in @(Get-ChildItem -Path $publishDir -Recurse -Force -Filter $forbidden.Pattern -ErrorAction SilentlyContinue)) {
        $relativePath = $match.FullName.Substring((Resolve-Path $publishDir).Path.Length).TrimStart('\')
        $leakedArtifacts.Add("$relativePath — $($forbidden.Reason)")
    }
}
if ($leakedArtifacts.Count -gt 0) {
    throw @"
Runtime state found in the publish output. Packing would ship it to testers:

$($leakedArtifacts -join "`n")

This means the application was executed from '$publishDir'. Never run the published executable in place —
it writes logs, queues and (in desktop mode) its database and keys next to itself. Delete that directory and
re-run this script. Smoke-test the packed Portable.zip instead, which is what testers actually receive.
"@
}
Write-Host ">> Publish output carries no runtime state (no logs, queues, keys or databases)."

$publishedUpdateConfigPath = Join-Path $publishDir "appsettings.AppUpdate.json"
if (-not (Test-Path $publishedUpdateConfigPath -PathType Leaf)) {
    throw "Published app-update config is missing: $publishedUpdateConfigPath"
}
$publishedUpdateConfig = Get-Content $publishedUpdateConfigPath -Raw | ConvertFrom-Json
# [string] coercion first: a missing GitHubRepositoryUrl key makes the property $null, and calling
# .TrimEnd() on it throws "cannot call a method on a null-valued expression" instead of the real message.
$publishedRepositoryUrl = ([string]$publishedUpdateConfig.AppUpdate.GitHubRepositoryUrl).TrimEnd('/')
if ($publishedUpdateConfig.AppUpdate.Channel -ne "tester" -or
    $publishedRepositoryUrl -ne $canonicalTesterRepo) {
    throw "Published app-update config does not target the canonical tester channel/repository (channel '$($publishedUpdateConfig.AppUpdate.Channel)', repo '$publishedRepositoryUrl')."
}
if ($hasGitHubAppClientId) {
    $publishedUpdateConfig.AppUpdate.GitHubAppClientId = $GitHubAppClientId
    $publishedUpdateConfig | ConvertTo-Json -Depth 10 | Set-Content $publishedUpdateConfigPath -Encoding utf8
    $publishedUpdateConfigText = Get-Content $publishedUpdateConfigPath -Raw
    if ($publishedUpdateConfigText -match '(?i)(REPLACE_|CHANGE_ME|TODO)' -or
        [string]::IsNullOrWhiteSpace((ConvertFrom-Json $publishedUpdateConfigText).AppUpdate.GitHubAppClientId)) {
        throw "Published app-update config still contains a placeholder or empty GitHub App client ID."
    }
    Write-Host ">> Published tester update config verified (canonical repo, supplied client ID, no placeholders)."
}
else {
    # Rehearsal: bake NO client ID. The committed tester config already ships an empty one, so the only thing
    # to prove is that nothing placeholder-shaped survives — an empty id leaves IsConfigured false (honestly
    # disabled), whereas a REPLACE_ value would be config-shaped garbage.
    $publishedUpdateConfigText = Get-Content $publishedUpdateConfigPath -Raw
    if ($publishedUpdateConfigText -match '(?i)(REPLACE_|CHANGE_ME|TODO)') {
        throw "Published app-update config contains placeholder text: $publishedUpdateConfigPath"
    }
    if (-not [string]::IsNullOrWhiteSpace((ConvertFrom-Json $publishedUpdateConfigText).AppUpdate.GitHubAppClientId)) {
        throw "Rehearsal package must bake no GitHub App client ID, but the published config already carries one."
    }
    Write-Host ">> Rehearsal update config verified (canonical repo, no client ID baked, no placeholders) — updater is inert."
}

# Stamp/clear the non-shippable marker. `dotnet publish` does not clean stale files out of the publish
# directory, so a marker left by an earlier rehearsal must be removed on a real run or it would ride along
# into a shipped Portable.zip.
$rehearsalMarkerPath = Join-Path $publishDir $rehearsalMarkerName
if ($isRehearsalPackage) {
    Set-Content -Path $rehearsalMarkerPath -Encoding utf8 -Value @(
        "REHEARSAL BUILD — DO NOT DISTRIBUTE",
        "",
        "Version:  $Version",
        "Built:    $([DateTimeOffset]::Now.ToString('u'))",
        "",
        "This package was produced by publish/package-tester-win.ps1 -SkipUpload with no GitHub App",
        "client ID, so no update credential is baked in and the in-app updater is permanently inert.",
        "It exists to rehearse the packaging steps only. Rebuild with -GitHubAppClientId to ship."
    )
    Write-Host ">> Stamped $rehearsalMarkerName into the publish output."
}
elseif (Test-Path $rehearsalMarkerPath -PathType Leaf) {
    Remove-Item $rehearsalMarkerPath -Force
    Write-Host ">> Removed a stale $rehearsalMarkerName left by an earlier rehearsal."
}

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
if (-not $gcExe) { throw "git-cliff.exe not found under $gcExtractDir" }

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
# Velopack's documented delta flow is download -> pack -> upload. Download into an isolated seed directory,
# validate exactly one prior full package, then copy only that delta base into a clean pack directory. This
# keeps downloaded feed metadata or stale local files from being mistaken for assets produced by this run.
# -SkipUpload still performs the authenticated private-repository download and builds the complete five-asset
# set; it may omit only the client ID and skips only the final upload.
$velopackSeedDir = Join-Path "Releases" "package-tester-seed-$Version"
$velopackOutputDir = Join-Path "Releases" "package-tester-win-$Version"
foreach ($directory in @($velopackSeedDir, $velopackOutputDir)) {
    if (Test-Path $directory) {
        Remove-Item -Path $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
Invoke-VelopackPreviousReleaseDownload `
    -RepositoryUrl $TesterRepo `
    -OutputDirectory $velopackSeedDir `
    -Token $env:VPK_TOKEN
Assert-VelopackPreviousReleaseSeed -OutputDirectory $velopackSeedDir
$previousFullPackage = Get-ChildItem -Path $velopackSeedDir -Recurse -File -Filter '*-full.nupkg' | Select-Object -First 1
Copy-Item -Path $previousFullPackage.FullName -Destination $velopackOutputDir
$previousFullPackageName = $previousFullPackage.Name

dnx vpk@1.2.0 pack `
    --outputDir $velopackOutputDir `
    --packId XE-Local-AI-Engine `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe XE-Local-AI-Engine.Client.exe `
    --channel win `
    --releaseNotes RELEASE_NOTES.md `
    --noInst
Assert-LastExitCode "vpk pack"

$currentPackFiles = @(
    Get-ChildItem -Path $velopackOutputDir -Recurse -File |
        Where-Object Name -CNE $previousFullPackageName
)
$packedVelopackAssets = @(Get-ExpectedVelopackAsset -Assets $currentPackFiles)
$portableArtifact = $packedVelopackAssets | Where-Object Name -Like '*Portable*.zip' | Select-Object -First 1
$portableHash = (Get-FileHash $portableArtifact.FullName -Algorithm SHA256).Hash
$assetManifestPath = Join-Path "publish\dist" "XE-Local-AI-Engine-$Version-win.sha256.json"
$assetManifestDirectory = Split-Path -Parent $assetManifestPath
New-Item -ItemType Directory -Path $assetManifestDirectory -Force | Out-Null
$assetManifest = [ordered]@{
    Version = $Version
    SourceCommit = $headCommit
    SourceTag = $tag
    Assets = @(
        $packedVelopackAssets | ForEach-Object {
            [ordered]@{
                Name = $_.Name
                Sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            }
        }
    )
}
$assetManifest | ConvertTo-Json -Depth 4 | Set-Content $assetManifestPath -Encoding utf8
Write-Host ">> Portable artifact: $($portableArtifact.FullName)"
Write-Host ">> Portable SHA-256: $portableHash"
Write-Host ">> Velopack SHA-256 manifest: $assetManifestPath"

if ($SkipUpload) {
    if ($isRehearsalPackage) {
        Write-Warning "Rehearsal package built (updater inert, $rehearsalMarkerName inside). NOT shippable — rebuild with -GitHubAppClientId for a real tester release."
    }
    Write-Host ">> -SkipUpload: package built, not uploaded. Done."
    exit 0
}

# --- 5. Upload as draft + notes safety net -------------------------------------------
$existingRelease = Find-GitHubRelease -ReleaseVersion $Version -RepositorySlug $repoSlug
if ($null -ne $existingRelease -and -not $existingRelease.isDraft) {
    throw "Release '$($existingRelease.tagName)' for version '$Version' is already published. Refusing to merge untested assets into the live update feed."
}
# Merge into the existing draft's own tag when there is one; otherwise create the v-prefixed tag.
$uploadTag = if ($null -ne $existingRelease) { $existingRelease.tagName } else { $tag }

dnx vpk@1.2.0 upload github `
    --outputDir $velopackOutputDir `
    --repoUrl $TesterRepo `
    --token $env:VPK_TOKEN `
    --channel win `
    --pre `
    --merge `
    --tag $uploadTag
Assert-LastExitCode "vpk upload"

$uploadedRelease = Find-GitHubRelease -ReleaseVersion $Version -RepositorySlug $repoSlug
if ($null -eq $uploadedRelease -or -not $uploadedRelease.isDraft) {
    throw "Velopack upload did not leave a draft release for version '$Version'. Refusing to continue."
}

gh release edit $uploadedRelease.tagName --repo $repoSlug --notes-file RELEASE_NOTES.md
Assert-LastExitCode "gh release edit (notes may be stale on the release)"

Write-Host ">> Tester release $($uploadedRelease.tagName) uploaded as a DRAFT to $TesterRepo with release notes."
Write-Host ">> Smoke-test the exact Portable.zip above, then publish without rebuilding:"
Write-Host "   .\publish\package-tester-win.ps1 -PublishDraft -ExpectedPortableSha256 $portableHash"
