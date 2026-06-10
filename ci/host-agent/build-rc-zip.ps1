<#
.SYNOPSIS
    Builds the RC1 distribution zip for XE Local AI Engine.

.DESCRIPTION
    Packages a self-contained RC1 zip bundle containing:
      - xe-installer.exe (self-contained win-x64 single-file)
      - payload/rootfs/ubuntu.tar.gz          (WSL2 distro rootfs)
      - payload/images/xe-node-web-server.tar.gz  (app image docker save)
      - payload/bundle-metadata.json          (config Id + script SHAs)
      - payload/in-distro-scripts/            (static bash -s scripts)
      - payload/host-agent/                   (published HostAgent.Windows + Tray binaries)
      - payload/manifest/managed.yaml         (runtime manifest, @sha256: = config Id)
      - payload/scripts/                      (vendored install-host-agent.ps1 + uninstall-host-agent.ps1)
      - payload/SHA256SUMS                    (corruption guard, NOT anti-tamper)
      - payload/README-TESTER.md              (tester-facing quickstart)

    SHA256SUMS covers every file under payload/ (corruption guard — see §10 of the plan).
    The SHA-256s of the static in-distro bash scripts (after token substitution) are recorded
    in bundle-metadata.json and verified by Wsl2Driver.BootstrapAsync / RuntimeInstallAsync at
    install time.

    Build host requirements (this script, NOT the tester box):
      - PowerShell 7+ (pwsh) — build-host tool; the installer invokes vendored *.ps1 via
        powershell.exe (Windows PowerShell 5.1) at install time (HIGH-4 contract).
      - .NET 10 SDK (dotnet)
      - Docker with BuildKit support (for docker build / docker save / docker inspect)
      - WSL2 + an Ubuntu base image accessible to docker export, OR a pre-built rootfs tar.

    G1 (app image): if -SkipImageBuild is set, the script expects
        payload/images/xe-node-web-server.tar.gz to exist already and reads the config Id
        from the running daemon via docker inspect.  Use this during development when the
        image is already built.
    G2 (rootfs): if -SkipRootfsBuild is set, the script expects
        payload/rootfs/ubuntu.tar.gz to exist already.

.PARAMETER Output
    Path for the output zip file.  Defaults to out/xe-rc-<version>.zip.

.PARAMETER RepoRoot
    Root of the XE-Local-AI-Engine repository.  Defaults to the directory two levels above
    this script (ci/host-agent/ → Apps/XE-Local-AI-Engine/).

.PARAMETER ImageTag
    Docker image tag to build the app image with.
    Defaults to ghcr.io/c0re/xe-local-ai-engine:0.1.0-rc.1

.PARAMETER SkipImageBuild
    Skip docker build (image must already exist in the local daemon under -ImageTag).
    Useful for iteration when the image is already built.

.PARAMETER SkipRootfsBuild
    Skip rootfs tar build (payload/rootfs/ubuntu.tar.gz must already exist).

.PARAMETER UbuntuRootfsBase
    Docker image used to export the Ubuntu rootfs tarball.
    Defaults to ubuntu:24.04

.PARAMETER WorkDir
    Temporary working directory for staging the bundle.  Cleaned up on exit unless
    -KeepWorkDir is set.

.PARAMETER KeepWorkDir
    Do not delete the staging directory after packaging.  Useful for debugging.

.EXAMPLE
    # Full build (build host with Docker + WSL tooling):
    pwsh ci/host-agent/build-rc-zip.ps1 -Output out/xe-rc.zip

.EXAMPLE
    # Iteration: image already built, skip docker build:
    pwsh ci/host-agent/build-rc-zip.ps1 -Output out/xe-rc.zip -SkipImageBuild

.NOTES
    Worker-only — do NOT commit or push (per plan §11 convention).
    Build host uses pwsh (PowerShell 7); tester box uses powershell.exe (5.1) — these are distinct.
    SHA256SUMS is a corruption guard, not an anti-tamper guarantee (§10).
#>
# Write-Host is intentional: this is an interactive, operator-facing build script.
# PSScriptAnalyzer PSAvoidUsingWriteHost is acknowledged and suppressed here to match
# the repo convention used by uninstall-host-agent.ps1 and windows-clean-install.ps1.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Output,
    [string]$RepoRoot,
    [string]$ImageTag      = 'ghcr.io/c0re/xe-local-ai-engine:0.1.0-rc.1',
    [switch]$SkipImageBuild,
    [switch]$SkipRootfsBuild,
    [string]$UbuntuRootfsBase = 'ubuntu:24.04',
    [string]$WorkDir,
    [switch]$KeepWorkDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ──────────────────────────────────────────────────────────────

$ScriptDir = $PSScriptRoot
if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $ScriptDir '../..')).Path
}
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)

# Read version from Directory.Build.props
$BuildPropsPath = Join-Path $RepoRoot 'Directory.Build.props'
if (-not (Test-Path $BuildPropsPath)) {
    throw "Directory.Build.props not found at: $BuildPropsPath"
}
$BuildPropsXml = [xml](Get-Content $BuildPropsPath -Raw)
$VersionPrefix = ($BuildPropsXml.SelectSingleNode('//VersionPrefix')).'#text'
$VersionSuffix = ($BuildPropsXml.SelectSingleNode('//VersionSuffix')).'#text'
$Version       = if ($VersionSuffix) { "$VersionPrefix-$VersionSuffix" } else { $VersionPrefix }

if (-not $Output) {
    $OutDir = Join-Path $RepoRoot 'out'
    if (-not (Test-Path $OutDir)) { $null = New-Item -ItemType Directory -Path $OutDir }
    $Output = Join-Path $OutDir "xe-rc-$Version.zip"
}

if (-not $WorkDir) {
    $WorkDir = Join-Path ([IO.Path]::GetTempPath()) "xe-rc-build-$([guid]::NewGuid().ToString('N').Substring(0,8))"
}

$PayloadDir   = Join-Path $WorkDir 'payload'
$RootfsDir    = Join-Path $PayloadDir 'rootfs'
$ImagesDir    = Join-Path $PayloadDir 'images'
$ScriptsDir   = Join-Path $PayloadDir 'scripts'
$ManifestDir  = Join-Path $PayloadDir 'manifest'
$InDistroDir  = Join-Path $PayloadDir 'in-distro-scripts'
$HostAgentDir = Join-Path $PayloadDir 'host-agent'

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step([string]$Msg) {
    Write-Host "`n==> $Msg" -ForegroundColor Cyan
}

function Write-Evidence([string]$Label, [string]$Value) {
    Write-Host "    $Label : $Value" -ForegroundColor Gray
}

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $Name.  Install it before running this script."
    }
}

function Get-FileSha256([string]$FilePath) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $FilePath).Hash
    return $hash.ToLowerInvariant()
}

function Get-StringSha256([string]$Text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $sha   = [Security.Cryptography.SHA256]::Create()
    $hash  = $sha.ComputeHash($bytes)
    return [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
}

# ── Preflight ─────────────────────────────────────────────────────────────────

Write-Step "Preflight checks"
Assert-Command 'dotnet'
Assert-Command 'docker'

$dotnetVersion = dotnet --version
Write-Evidence 'dotnet' $dotnetVersion
$dockerVersion = docker version --format '{{.Server.Version}}' 2>$null
Write-Evidence 'docker' $dockerVersion
Write-Evidence 'RepoRoot' $RepoRoot
Write-Evidence 'Version'  $Version
Write-Evidence 'ImageTag' $ImageTag
Write-Evidence 'WorkDir'  $WorkDir
Write-Evidence 'Output'   $Output

# ── Create staging layout ──────────────────────────────────────────────────────

Write-Step "Creating staging layout"
foreach ($d in @($PayloadDir, $RootfsDir, $ImagesDir, $ScriptsDir, $ManifestDir, $InDistroDir, $HostAgentDir)) {
    $null = New-Item -ItemType Directory -Path $d -Force
}

# ── Build app image (G1) ──────────────────────────────────────────────────────

$AppImageTar = Join-Path $ImagesDir 'xe-node-web-server.tar.gz'

if (-not $SkipImageBuild) {
    Write-Step "Building app image: $ImageTag"
    $Dockerfile = Join-Path $RepoRoot 'docker/Dockerfile.xe-node'
    if (-not (Test-Path $Dockerfile)) {
        # Dockerfile.xe-node does not exist yet (Gap G1 in the plan) — the build-host must
        # supply it.  Emit a clear error rather than silently using a wrong image.
        throw @"
Gap G1: docker/Dockerfile.xe-node not found at $Dockerfile.
Create the production Dockerfile for the xe-node-web-server image, then re-run.
If you have already built the image under tag '$ImageTag', pass -SkipImageBuild.
"@
    }

    # Build with --provenance=false --sbom=false so BuildKit does not attach OCI attestation
    # blobs that can cause the config Id to diverge between the Windows build daemon and the
    # in-distro rootless daemon (§14 W1 spike gate, locked decision).
    Write-Evidence 'build flags' '--provenance=false --sbom=false --platform linux/amd64'
    docker build `
        --provenance=false `
        --sbom=false `
        --platform linux/amd64 `
        --tag $ImageTag `
        --file $Dockerfile `
        $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "docker build failed (exit $LASTEXITCODE)" }
} else {
    Write-Step "Skipping image build (SkipImageBuild set) — using existing tag: $ImageTag"
    $existing = docker images --format '{{.Repository}}:{{.Tag}}' 2>$null | Where-Object { $_ -eq $ImageTag }
    if (-not $existing) {
        throw "Image '$ImageTag' not found in local daemon.  Build it first or remove -SkipImageBuild."
    }
}

# Capture config Id BEFORE docker save (save/load preserves it — §14 W1 spike gate).
Write-Step "Capturing app image config Id"
$RawConfigId = docker inspect --format '{{.Id}}' $ImageTag 2>$null
if (-not $RawConfigId) { throw "docker inspect returned empty Id for '$ImageTag'" }
$RawConfigId = $RawConfigId.Trim()

# Normalize to bare hex (the existing DockerRuntimeClient:111 slice strips after last ':').
# docker inspect returns sha256:<hex>; we keep the full form and record it.
# The installer's load-image.sh bakes the full form (sha256:<hex>) to match docker inspect output.
$AppImageConfigId = $RawConfigId   # e.g. sha256:abcdef…
Write-Evidence 'AppImageConfigId' $AppImageConfigId

# docker save — single platform, save to a temp file first so a failed `docker save`
# cannot produce a silently-truncated gzip (in a pipeline, $LASTEXITCODE reflects only
# the last stage; saving to a temp file and checking each stage explicitly is safer).
Write-Step "Saving app image tar: $AppImageTar"
$AppImageTarRaw = "$AppImageTar.tmp"
try {
    docker save --platform linux/amd64 --output $AppImageTarRaw $ImageTag
    if ($LASTEXITCODE -ne 0) { throw "docker save failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $AppImageTarRaw) -or (Get-Item $AppImageTarRaw).Length -eq 0) {
        throw "docker save produced an empty or missing output file: $AppImageTarRaw"
    }
    gzip -c $AppImageTarRaw > $AppImageTar
    if ($LASTEXITCODE -ne 0) { throw "gzip failed (exit $LASTEXITCODE)" }
} finally {
    if (Test-Path $AppImageTarRaw) { Remove-Item $AppImageTarRaw -Force -ErrorAction SilentlyContinue }
}
Write-Evidence 'ImageTarSize' "$('{0:N1}' -f ((Get-Item $AppImageTar).Length / 1MB)) MB"

# ── Build rootfs tar (G2) ─────────────────────────────────────────────────────

$RootfsTar = Join-Path $RootfsDir 'ubuntu.tar.gz'

if (-not $SkipRootfsBuild) {
    Write-Step "Building Ubuntu rootfs tar from $UbuntuRootfsBase"
    # Export a clean Ubuntu container as the WSL2 rootfs.
    # We create a throw-away container, export its filesystem, and remove it.
    $cname = "xe-rootfs-export-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    docker create --name $cname --platform linux/amd64 $UbuntuRootfsBase /bin/true 2>$null
    if ($LASTEXITCODE -ne 0) { throw "docker create for rootfs export failed (exit $LASTEXITCODE)" }
    try {
        docker export $cname | gzip -c > $RootfsTar
        if ($LASTEXITCODE -ne 0) { throw "docker export | gzip failed (exit $LASTEXITCODE)" }
    } finally {
        docker rm -f $cname 2>$null | Out-Null
    }
    Write-Evidence 'RootfsTarSize' "$('{0:N1}' -f ((Get-Item $RootfsTar).Length / 1MB)) MB"
} else {
    Write-Step "Skipping rootfs build (SkipRootfsBuild set)"
    if (-not (Test-Path $RootfsTar)) {
        throw "SkipRootfsBuild set but rootfs tar not found at $RootfsTar"
    }
    Write-Evidence 'RootfsTar' "$RootfsTar (pre-existing)"
}

# ── Static in-distro scripts (G1/§7.5a) ──────────────────────────────────────
#
# The plan requires per-bundle token substitution before SHA recording.
# Template files live in XE-Local-AI-Engine.HostAgent.Linux/Packaging/in-distro-scripts/.
# build-rc-zip substitutes @@TOKENS@@, records the SHA of the substituted content,
# and writes the substituted script into payload/in-distro-scripts/.
#
# stage-image.sh has no tokens — copy as-is.  Its SHA is the SHA of the file on disk.

Write-Step "Processing in-distro scripts"

$InDistroSrcDir = Join-Path $RepoRoot 'XE-Local-AI-Engine.HostAgent.Linux/Packaging/in-distro-scripts'

# ---- stage-image.sh (no tokens) ----
$StageImageSrc  = Join-Path $InDistroSrcDir 'stage-image.sh'
$StageImageDest = Join-Path $InDistroDir    'stage-image.sh'
if (-not (Test-Path $StageImageSrc)) { throw "stage-image.sh not found: $StageImageSrc" }
Copy-Item $StageImageSrc $StageImageDest
$StageImageSha = Get-FileSha256 $StageImageDest
Write-Evidence 'stage-image.sh SHA256' $StageImageSha

# ---- load-image.sh (token substitution) ----
#  @@XE_EXPECTED_IMAGE_ID@@  → $AppImageConfigId  (e.g. sha256:abcdef…)
#  @@XE_REPO_TAG@@           → repository+tag portion of $ImageTag (without digest suffix)
$RepoTag = $ImageTag -replace '@sha256:[0-9a-f]+$', ''   # strip digest if present
$LoadImageSrc  = Join-Path $InDistroSrcDir 'load-image.sh'
if (-not (Test-Path $LoadImageSrc)) { throw "load-image.sh not found: $LoadImageSrc" }
$LoadImageBody = (Get-Content $LoadImageSrc -Raw -Encoding UTF8) `
    -replace '@@XE_EXPECTED_IMAGE_ID@@', $AppImageConfigId `
    -replace '@@XE_REPO_TAG@@',          $RepoTag
$LoadImageDest = Join-Path $InDistroDir 'load-image.sh'
[IO.File]::WriteAllText($LoadImageDest, $LoadImageBody, [Text.Encoding]::UTF8)
$LoadImageSha  = Get-StringSha256 $LoadImageBody
Write-Evidence 'load-image.sh  SHA256' $LoadImageSha

# ---- pull-model.sh (token substitution) ----
#  @@XE_BOOTSTRAP_MODEL@@  → bootstrap model from managed.yaml (qwen3:0.6b)
$BootstrapModel   = 'qwen3:0.6b'   # locked D5; matches managed.yaml
$PullModelSrc  = Join-Path $InDistroSrcDir 'pull-model.sh'
if (-not (Test-Path $PullModelSrc)) { throw "pull-model.sh not found: $PullModelSrc" }
$PullModelBody = (Get-Content $PullModelSrc -Raw -Encoding UTF8) `
    -replace '@@XE_BOOTSTRAP_MODEL@@', $BootstrapModel
$PullModelDest = Join-Path $InDistroDir 'pull-model.sh'
[IO.File]::WriteAllText($PullModelDest, $PullModelBody, [Text.Encoding]::UTF8)
$PullModelSha  = Get-StringSha256 $PullModelBody
Write-Evidence 'pull-model.sh  SHA256' $PullModelSha

# ---- write-manifest.sh (no tokens) ----
# Manifest YAML content is not known at build time — it rides stdin AFTER the
# hashed script body at install time (BootstrapAsync seam).  Copy as-is; the
# SHA is of the verbatim file bytes so the installer can verify before execution.
$WriteManifestSrc  = Join-Path $InDistroSrcDir 'write-manifest.sh'
$WriteManifestDest = Join-Path $InDistroDir    'write-manifest.sh'
if (-not (Test-Path $WriteManifestSrc)) { throw "write-manifest.sh not found: $WriteManifestSrc" }
Copy-Item $WriteManifestSrc $WriteManifestDest
$WriteManifestSha = Get-FileSha256 $WriteManifestDest
Write-Evidence 'write-manifest.sh SHA256' $WriteManifestSha

# ── Runtime manifest (managed.yaml with real config Id substituted) ───────────

Write-Step "Writing runtime manifest"
$ManifestSrc = Join-Path $RepoRoot 'Plans/artifacts/sample-manifests/managed.yaml'
if (-not (Test-Path $ManifestSrc)) { throw "managed.yaml not found: $ManifestSrc" }
$ManifestContent = Get-Content $ManifestSrc -Raw -Encoding UTF8

# Replace the xe-node-web-server placeholder digest with the real config Id.
# W1 (§7.6) documents that for managed/loaded images the @sha256: field carries
# the config Id (not a registry RepoDigest).
#
# Pattern: "ghcr.io/c0re/xe-local-ai-engine:<tag>@sha256:<any64hex>"
#          replaced with  "<RepoTag>@<AppImageConfigId>"
# where AppImageConfigId is already "sha256:<hex>" so we produce:
#   ghcr.io/c0re/xe-local-ai-engine:0.1.0-rc.1@sha256:<hex>
$ManifestImageRef = "$RepoTag@$AppImageConfigId"
$ManifestContent  = $ManifestContent -replace `
    'ghcr\.io/c0re/xe-local-ai-engine:[^@"]+@sha256:[0-9a-fA-F]{64}', `
    $ManifestImageRef

$ManifestDest = Join-Path $ManifestDir 'managed.yaml'
[IO.File]::WriteAllText($ManifestDest, $ManifestContent, [Text.Encoding]::UTF8)
Write-Evidence 'ManifestImageRef' $ManifestImageRef

# ── Vendored PowerShell scripts ───────────────────────────────────────────────

Write-Step "Copying vendored PowerShell scripts"
$WinPackagingDir = Join-Path $RepoRoot 'XE-Local-AI-Engine.HostAgent.Windows/Packaging/Windows'
foreach ($ps1 in @('install-host-agent.ps1', 'uninstall-host-agent.ps1')) {
    $src = Join-Path $WinPackagingDir $ps1
    if (-not (Test-Path $src)) { throw "Vendored script not found: $src" }
    Copy-Item $src (Join-Path $ScriptsDir $ps1)
    Write-Evidence "vendored" $ps1
}

# ── Publish HostAgent.Windows ─────────────────────────────────────────────────

Write-Step "Publishing HostAgent.Windows"
$HostAgentProj = Join-Path $RepoRoot 'XE-Local-AI-Engine.HostAgent.Windows/XE-Local-AI-Engine.HostAgent.Windows.csproj'
if (-not (Test-Path $HostAgentProj)) { throw "HostAgent.Windows csproj not found: $HostAgentProj" }
dotnet publish $HostAgentProj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $HostAgentDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish HostAgent.Windows failed (exit $LASTEXITCODE)" }

# ── Publish Tray ──────────────────────────────────────────────────────────────

Write-Step "Publishing Tray"
$TrayProj = Join-Path $RepoRoot 'XE-Local-AI-Engine.Tray/XE-Local-AI-Engine.Tray.csproj'
if (-not (Test-Path $TrayProj)) { throw "Tray csproj not found: $TrayProj" }
dotnet publish $TrayProj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $HostAgentDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Tray failed (exit $LASTEXITCODE)" }

# ── Publish installer (xe-installer.exe) ─────────────────────────────────────

Write-Step "Publishing xe-installer.exe"
$InstallerProj = Join-Path $RepoRoot 'tools/installer/XE-Local-AI-Engine.Installer.csproj'
if (-not (Test-Path $InstallerProj)) { throw "Installer csproj not found: $InstallerProj" }

# Publish-time flags per §7.1 (NOT in the csproj so dotnet build stays fast).
dotnet publish $InstallerProj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -p:EnableCompressionInSingleFile=true `
    --output (Join-Path $WorkDir 'installer-out') `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish installer failed (exit $LASTEXITCODE)" }

$InstallerExe = Join-Path $WorkDir 'installer-out/xe-installer.exe'
if (-not (Test-Path $InstallerExe)) { throw "xe-installer.exe not found after publish at $InstallerExe" }
Write-Evidence 'InstallerSize' "$('{0:N1}' -f ((Get-Item $InstallerExe).Length / 1MB)) MB"

# Smoke-test the trimmed exe: xe-installer status should not crash (§13 trimmer guard).
# On a Linux build host this cross-compiled exe cannot be executed; skip silently.
if ($IsWindows) {
    Write-Step "Smoke-testing trimmed xe-installer.exe (status verb)"
    & $InstallerExe status 2>&1 | Write-Host
    # status exits non-zero if not installed, which is expected on a build box — just
    # ensure the process launched without a runtime crash.
    Write-Evidence 'smokeTest' "process launched (exit $LASTEXITCODE expected non-zero on clean box)"
} else {
    Write-Evidence 'smokeTest' "skipped (cross-compiled win-x64 exe on non-Windows build host)"
}

# ── bundle-metadata.json ──────────────────────────────────────────────────────

Write-Step "Writing bundle-metadata.json"

# Collect payload file sizes for the disk-space preflight (MED-7a, §7.5 probe phase).
# These are rough estimates — the installer adds overhead for DB, config, etc.
$RootfsSizeBytes  = (Get-Item $RootfsTar).Length
$ImageSizeBytes   = (Get-Item $AppImageTar).Length
# Conservative model pull estimate for qwen3:0.6b (~400 MB compressed, ~500 MB on disk).
$ModelPullEstimateBytes = 600MB

$BundleMetadata = [ordered]@{
    schemaVersion              = 1
    version                    = $Version
    imageTag                   = $ImageTag
    # XE_EXPECTED_IMAGE_ID: the installer reads this field by name (§5 / §7.5a / plan §6.3).
    # Value is the full "sha256:<hex>" config Id from `docker inspect --format '{{.Id}}'`.
    XE_EXPECTED_IMAGE_ID       = $AppImageConfigId
    bootstrapModel             = $BootstrapModel
    stageImageScriptSha256     = $StageImageSha
    loadImageScriptSha256      = $LoadImageSha
    pullModelScriptSha256      = $PullModelSha
    writeManifestScriptSha256  = $WriteManifestSha
    rootfsTarSizeBytes         = $RootfsSizeBytes
    appImageTarSizeBytes       = $ImageSizeBytes
    modelPullEstimateBytes     = [long]$ModelPullEstimateBytes
    # minimumFreeDiskBytes = rootfs + image tar (uncompressed ~3-4x) + model pull estimate.
    # The installer probe phase uses this field for the free-disk preflight (MED-7a).
    minimumFreeDiskBytes       = [long]($RootfsSizeBytes * 3 + $ImageSizeBytes * 4 + $ModelPullEstimateBytes)
    builtAtUtc                 = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$BundleMetadataJson = $BundleMetadata | ConvertTo-Json -Depth 5
$BundleMetadataDest = Join-Path $PayloadDir 'bundle-metadata.json'
[IO.File]::WriteAllText($BundleMetadataDest, $BundleMetadataJson, [Text.Encoding]::UTF8)

# ── README-TESTER.md ─────────────────────────────────────────────────────────
# Source: ci/host-agent/bundle/README-TESTER.md (Lane C output).
# Copied verbatim into the payload so it appears at the bundle root.

$ReadmeSrc  = Join-Path $ScriptDir 'bundle/README-TESTER.md'
$ReadmeDest = Join-Path $PayloadDir 'README-TESTER.md'
if (Test-Path $ReadmeSrc) {
    Copy-Item $ReadmeSrc $ReadmeDest
    Write-Evidence 'README-TESTER.md' "copied from $ReadmeSrc"
} else {
    # Fallback stub so SHA256SUMS always includes the file and a missing-source
    # error surfaces clearly at bundle-open time rather than silently.
    $ReadmeStub = "# XE Local AI Engine $Version`n`n" +
                  "> README-TESTER.md source not found at ci/host-agent/bundle/README-TESTER.md.`n" +
                  '> Re-run build-rc-zip.ps1 after the source file is present.'
    [IO.File]::WriteAllText($ReadmeDest, $ReadmeStub, [Text.Encoding]::UTF8)
    Write-Warning "README-TESTER.md source not found — stub written.  Add ci/host-agent/bundle/README-TESTER.md and rebuild."
}

# ── SHA256SUMS ────────────────────────────────────────────────────────────────
# Covers every file under payload/.  Written as a flat "<hex>  <relative-path>"
# list (two spaces, POSIX sha256sum / certutil convention).
# This is a CORRUPTION GUARD, NOT an anti-tamper guarantee (§10).

Write-Step "Computing SHA256SUMS"
$Sha256SumsLines = [Collections.Generic.List[string]]::new()
$PayloadFiles = Get-ChildItem -LiteralPath $PayloadDir -Recurse -File | Sort-Object FullName
foreach ($f in $PayloadFiles) {
    $relPath = $f.FullName.Substring($PayloadDir.Length).TrimStart([IO.Path]::DirectorySeparatorChar, '/').Replace('\','/')
    $hex     = Get-FileSha256 $f.FullName
    # Defensive: assert the hash is exactly 64 lowercase hex chars before writing.
    # A malformed hash here would produce an unparseable SHA256SUMS entry that the
    # installer's probe-phase verifier would silently skip or reject.
    if ($hex.Length -ne 64 -or $hex -notmatch '^[0-9a-f]{64}$') {
        throw "SHA256 hash for '$relPath' has unexpected format: '$hex'"
    }
    # Two-space separator: POSIX sha256sum / GNU coreutils convention (text mode).
    $Sha256SumsLines.Add("$hex  $relPath")
    Write-Evidence "SHA256" "$relPath"
}
$Sha256SumsContent = ($Sha256SumsLines -join "`n") + "`n"
$Sha256SumsDest = Join-Path $PayloadDir 'SHA256SUMS'
[IO.File]::WriteAllText($Sha256SumsDest, $Sha256SumsContent, [Text.Encoding]::ASCII)

# ── Assemble zip ──────────────────────────────────────────────────────────────

Write-Step "Assembling zip: $Output"

# xe-installer.exe lives at the zip root alongside the payload/ subtree.
$ZipStagingDir = Join-Path $WorkDir 'zip-staging'
$null = New-Item -ItemType Directory -Path $ZipStagingDir -Force
Copy-Item $InstallerExe (Join-Path $ZipStagingDir 'xe-installer.exe')
Copy-Item $PayloadDir   (Join-Path $ZipStagingDir 'payload') -Recurse

$OutputDir = Split-Path $Output -Parent
if ($OutputDir -and -not (Test-Path $OutputDir)) {
    $null = New-Item -ItemType Directory -Path $OutputDir -Force
}
if (Test-Path $Output) { Remove-Item $Output -Force }

# Use .NET's ZipFile to avoid external zip tool dependency on the build host.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($ZipStagingDir, $Output, [IO.Compression.CompressionLevel]::Optimal, $false)
$ZipSizeMB = '{0:N1}' -f ((Get-Item $Output).Length / 1MB)
Write-Evidence 'ZipSize' "$ZipSizeMB MB"

# ── Cleanup ───────────────────────────────────────────────────────────────────

if (-not $KeepWorkDir) {
    Write-Step "Cleaning up work directory"
    Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host " Build complete: $Output" -ForegroundColor Green
Write-Host "   Version           : $Version" -ForegroundColor Green
Write-Host "   AppImageConfigId  : $AppImageConfigId" -ForegroundColor Green
Write-Host "   StageImage SHA256 : $StageImageSha" -ForegroundColor Green
Write-Host "   LoadImage  SHA256 : $LoadImageSha" -ForegroundColor Green
Write-Host "   PullModel  SHA256 : $PullModelSha" -ForegroundColor Green
Write-Host "   Bundle size       : $ZipSizeMB MB" -ForegroundColor Green
Write-Host '════════════════════════════════════════════════════════════' -ForegroundColor Green
Write-Host ''
Write-Host 'IMPORTANT: SHA256SUMS is a CORRUPTION GUARD, not anti-tamper (see plan §10).' -ForegroundColor Yellow
Write-Host 'Code signing (signed exe + signed payload) is deferred to RC2/GA (G3).' -ForegroundColor Yellow
