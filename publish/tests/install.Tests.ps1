#Requires -Modules Pester

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:InstallerPath = Join-Path $script:RepoRoot 'install.ps1'
    $parseErrors = $null
    $script:InstallerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:InstallerPath, [ref]$null, [ref]$parseErrors)
    if (@($parseErrors | Where-Object { $_ }).Count -gt 0) { throw "install.ps1 does not parse:`n$($parseErrors -join "`n")" }

    function Get-InstallerFunctionText {
        param([Parameter(Mandatory)][string]$Name)
        $functionMatches = @($script:InstallerAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name
                }, $true))
        if ($functionMatches.Count -ne 1) { throw "Expected one function '$Name', found $($functionMatches.Count)." }
        return $functionMatches[0].Extent.Text
    }

    $script:Repository = 'w0rldx/XE-Local-AI-Engine.Source'
    $script:DefaultDownloadBase = 'https://github.com'
    $script:OwnershipMarker = '.xe-local-ai-engine-install'
    $script:OwnershipValue = 'XE_LOCAL_AI_ENGINE_INSTALL=1'
    $script:AutostartOwnershipMarker = '.xe-local-ai-engine-autostart'
    $script:AutostartOwnershipValue = 'XE_LOCAL_AI_ENGINE_AUTOSTART=1'
    @(
        'ConvertTo-XETag', 'Assert-XENetworkBase', 'Get-XERequestHeader', 'Invoke-XEWebRequest', 'Get-XENextLink',
        'Resolve-XERelease', 'Get-XEReleaseAsset', 'Get-XEDownloadUrl', 'Get-XEExpectedChecksum',
        'Test-XEReleaseManifest', 'Resolve-XEInstallPath', 'Assert-XESafeInstallPath',
        'Test-XEInstallOwned', 'Test-XEInstallComplete', 'Test-XEAspNetCoreRuntimeCompatible',
        'Get-XERequiredAspNetCoreRuntime', 'Assert-XEAspNetCoreRuntime', 'Test-XEInstallReusable',
        'Install-XEArchive', 'Get-XEDataDirectory', 'Assert-XEAutostartPath', 'Resolve-XESetupCredential',
        'Get-XESanitizedDiagnostic', 'Invoke-XESetup', 'Test-XEReadyEvidence', 'Start-XEEngine',
        'Assert-XEAutostartOwnership', 'Write-XEAutostartLauncher', 'Register-XEAutostart', 'Expand-XESkillArchive',
        'Install-XESkill', 'Invoke-XEPostInstallAction'
    ) | ForEach-Object { . ([scriptblock]::Create((Get-InstallerFunctionText -Name $_))) }

    function New-CompletePayload {
        param([Parameter(Mandatory)][string]$Root, [string]$RequiredVersion = '10.0.3')
        New-Item -ItemType Directory -Path (Join-Path $Root 'current') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $Root 'XE-Local-AI-Engine.exe') -Value 'stub'
        Set-Content -LiteralPath (Join-Path $Root 'current/XE-Local-AI-Engine.Client.dll') -Value 'stub'
        @{ runtimeOptions = @{ frameworks = @(@{ name = 'Microsoft.AspNetCore.App'; version = $RequiredVersion }) } } |
            ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $Root 'current/XE-Local-AI-Engine.Client.runtimeconfig.json')
    }

    function New-OwnedInstall {
        param([Parameter(Mandatory)][string]$Root, [string]$Version = 'v1.0.0')
        New-CompletePayload -Root $Root
        Set-Content -LiteralPath (Join-Path $Root $script:OwnershipMarker) -Value $script:OwnershipValue
        Set-Content -LiteralPath (Join-Path $Root 'xe-install-version.txt') -Value $Version
    }

    # Linux test hosts do not ship the Windows ScheduledTasks module. These declarations provide
    # command metadata for Pester's mocks; the production function is still extracted from install.ps1.
    function New-ScheduledTaskAction { param($Execute, $Argument) }
    function New-ScheduledTaskTrigger { param([switch]$AtLogOn, $User) }
    function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) }
    function Get-ScheduledTask { param($TaskName, $ErrorAction) }
    function Export-ScheduledTask { param($TaskName) }
    function Register-ScheduledTask { param($TaskName, $Action, $Trigger, $Principal, $Xml, [switch]$Force) }
    function Unregister-ScheduledTask { param($TaskName, [switch]$Confirm) }
}

Describe 'install.ps1 release and trust contracts' {
    It 'accepts release tags with or without the v prefix' {
        ConvertTo-XETag -Value '1.2.3' | Should -BeExactly 'v1.2.3'
        ConvertTo-XETag -Value 'v1.2.3-rc.1' | Should -BeExactly 'v1.2.3-rc.1'
    }

    It 'parses authority and rejects userinfo or deceptive loopback hosts' {
        { Assert-XENetworkBase -Name api -Url 'https://api.github.com' } | Should -Not -Throw
        { Assert-XENetworkBase -Name api -Url 'http://127.0.0.1:8123' } | Should -Not -Throw
        { Assert-XENetworkBase -Name api -Url 'http://localhost:8123' } | Should -Not -Throw
        { Assert-XENetworkBase -Name api -Url 'http://localhost.evil' } | Should -Throw '*must use HTTPS*'
        { Assert-XENetworkBase -Name api -Url 'http://localhost@evil.test' } | Should -Throw '*user information*'
        { Assert-XENetworkBase -Name api -Url 'https://user@example.com' } | Should -Throw '*user information*'
    }

    It 'sends a token only to the HTTPS GitHub API host' {
        (Get-XERequestHeader -Token secret -Uri 'https://api.github.com/repos/x').Authorization | Should -BeExactly 'Bearer secret'
        (Get-XERequestHeader -Token secret -Uri 'https://example.com/repos/x').ContainsKey('Authorization') | Should -BeFalse
        (Get-XERequestHeader -Token secret -Uri 'http://127.0.0.1:8123/repos/x').ContainsKey('Authorization') | Should -BeFalse
    }

    It 'selects stable, prerelease-inclusive, and pinned releases' {
        $script:ReleaseListJson = @(
            @{ tag_name = 'v1.0.0'; draft = $false; prerelease = $false; published_at = '2026-01-01'; assets = @() },
            @{ tag_name = 'v1.1.0-rc.1'; draft = $false; prerelease = $true; published_at = '2026-02-01'; assets = @() }
        ) | ConvertTo-Json -Depth 5
        Mock Invoke-XEWebRequest { [pscustomobject]@{ Content = $script:ReleaseListJson; Headers = @{} } }
        (Resolve-XERelease -IncludePrerelease $false -ApiBase 'https://api.github.com').tag_name | Should -BeExactly 'v1.0.0'
        (Resolve-XERelease -IncludePrerelease $true -ApiBase 'https://api.github.com').tag_name | Should -BeExactly 'v1.1.0-rc.1'
        Mock Invoke-XEWebRequest { [pscustomobject]@{ Content = (@{ tag_name = 'v0.9.0'; assets = @() } | ConvertTo-Json); Headers = @{} } }
        (Resolve-XERelease -RequestedVersion '0.9.0' -IncludePrerelease $false -ApiBase 'https://api.github.com').tag_name |
            Should -BeExactly 'v0.9.0'
    }

    It 'rejects an external cleartext pagination Link before issuing the next request' {
        $script:FirstPage = $true
        Mock Invoke-WebRequest {
            if (-not $script:FirstPage) { throw 'A second network request must not be attempted.' }
            $script:FirstPage = $false
            [pscustomobject]@{
                Content = '[]'
                Headers = @{ Link = '<http://example.com/page/2>; rel="next"' }
            }
        }
        { Resolve-XERelease -IncludePrerelease $false -ApiBase 'https://api.github.com' } |
            Should -Throw '*request URL must use HTTPS*'
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
    }

    It 'extracts exact checksums and verifies manifest binding' {
        $hash = 'a' * 64
        $text = "$hash  ./XE-win-Portable.zip`n$('b' * 64)  ./other.zip"
        Get-XEExpectedChecksum -ChecksumText $text -AssetName 'XE-win-Portable.zip' | Should -BeExactly $hash
        $manifest = [pscustomobject]@{ tag = 'v1.0.0'; assets = @([pscustomobject]@{ name = 'XE-win-Portable.zip'; sha256 = $hash }) }
        Test-XEReleaseManifest -Manifest $manifest -Tag 'v1.0.0' -AssetName 'XE-win-Portable.zip' -AssetHash $hash |
            Should -BeTrue
        Test-XEReleaseManifest -Manifest $manifest -Tag 'v1.0.1' -AssetName 'XE-win-Portable.zip' -AssetHash $hash |
            Should -BeFalse
    }
}

Describe 'install.ps1 safe installation flow' {
    BeforeEach {
        $script:TempRoot = Join-Path ([IO.Path]::GetTempPath()) "xe-install-pester-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:TempRoot | Out-Null
    }
    AfterEach {
        if (Test-Path -LiteralPath $script:TempRoot) { Remove-Item -LiteralPath $script:TempRoot -Recurse -Force }
    }

    It 'rejects protected and unowned non-empty directories' {
        $profile = Join-Path $script:TempRoot 'profile'
        $appData = Join-Path $script:TempRoot 'local/XE-Local-AI-Engine'
        $unowned = Join-Path $script:TempRoot 'unowned'
        New-Item -ItemType Directory -Path $profile, $appData, $unowned -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $unowned 'important.txt') -Value preserve
        { Assert-XESafeInstallPath -Path ([IO.Path]::GetPathRoot($script:TempRoot)) -UserProfile $profile -AppDataPath $appData } |
            Should -Throw '*filesystem root*'
        { Assert-XESafeInstallPath -Path $profile -UserProfile $profile -AppDataPath $appData } | Should -Throw '*profile*'
        { Assert-XESafeInstallPath -Path (Join-Path $appData 'nested') -UserProfile $profile -AppDataPath $appData } |
            Should -Throw '*app-owned data*'
        $nonexistentParent = Join-Path $script:TempRoot 'nonexistent-local-app-data'
        $nonexistentData = Join-Path $nonexistentParent 'XE-Local-AI-Engine'
        { Assert-XESafeInstallPath -Path $nonexistentParent -UserProfile $profile -AppDataPath $nonexistentData } |
            Should -Throw '*app-owned data*'
        Test-Path -LiteralPath $nonexistentParent | Should -BeFalse
        { Assert-XESafeInstallPath -Path $unowned -UserProfile $profile -AppDataPath $appData } |
            Should -Throw '*valid .xe-local-ai-engine-install marker*'
        (Get-Content -LiteralPath (Join-Path $unowned 'important.txt') -Raw).Trim() | Should -BeExactly preserve
    }

    It 'requires launcher, client assembly, and runtime config' {
        $payload = Join-Path $script:TempRoot 'payload'
        New-Item -ItemType Directory -Path $payload | Out-Null
        New-CompletePayload -Root $payload
        Test-XEInstallComplete -Path $payload | Should -BeTrue
        Remove-Item -LiteralPath (Join-Path $payload 'current/XE-Local-AI-Engine.Client.dll')
        Test-XEInstallComplete -Path $payload | Should -BeFalse
    }

    It 'rechecks runtime compatibility for idempotent installs' {
        $install = Join-Path $script:TempRoot 'install'
        New-OwnedInstall -Root $install
        Test-XEInstallReusable -Path $install -Version 'v1.0.0' `
            -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' | Should -BeTrue
        { Test-XEInstallReusable -Path $install -Version 'v1.0.0' `
                -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.2 [C:\dotnet]' } | Should -Throw '*required*'
        Remove-Item -LiteralPath (Join-Path $install 'current/XE-Local-AI-Engine.Client.dll')
        Test-XEInstallReusable -Path $install -Version 'v1.0.0' `
            -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' | Should -BeFalse
    }

    It 'preserves the old install on runtime failure and succeeds on retry' {
        $payload = Join-Path $script:TempRoot 'payload'
        New-CompletePayload -Root $payload
        $archive = Join-Path $script:TempRoot 'payload.zip'
        Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
        $target = Join-Path $script:TempRoot 'target'
        New-OwnedInstall -Root $target
        Set-Content -LiteralPath (Join-Path $target 'sentinel.txt') -Value preserve
        Mock Unblock-File {}

        { Install-XEArchive -Archive $archive -TargetPath $target -Version 'v2.0.0' `
                -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' -TestFailAfterBackup } |
            Should -Throw '*Injected extraction failure*'
        (Get-Content -LiteralPath (Join-Path $target 'sentinel.txt') -Raw).Trim() | Should -BeExactly preserve
        (Get-Content -LiteralPath (Join-Path $target 'xe-install-version.txt') -Raw).Trim() | Should -BeExactly 'v1.0.0'
        (Get-Content -LiteralPath (Join-Path $target $script:OwnershipMarker) -Raw).Trim() |
            Should -BeExactly $script:OwnershipValue
        @(Get-ChildItem -LiteralPath $script:TempRoot -Force | Where-Object Name -Like '.xe-install-backup-*').Count |
            Should -Be 0

        { Install-XEArchive -Archive $archive -TargetPath $target -Version 'v1.0.0' `
                -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.2 [C:\dotnet]' } | Should -Throw '*required*'
        (Get-Content -LiteralPath (Join-Path $target 'sentinel.txt') -Raw).Trim() | Should -BeExactly preserve
        (Get-Content -LiteralPath (Join-Path $target 'xe-install-version.txt') -Raw).Trim() | Should -BeExactly 'v1.0.0'

        Install-XEArchive -Archive $archive -TargetPath $target -Version 'v1.0.0' `
            -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]'
        Test-XEInstallReusable -Path $target -Version 'v1.0.0' `
            -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $target 'sentinel.txt') | Should -BeFalse
        Should -Invoke Unblock-File -Times 3 -Exactly
    }

    It 'preserves the committed new target when backup cleanup partially fails' {
        $payload = Join-Path $script:TempRoot 'payload-cleanup'
        New-CompletePayload -Root $payload
        $archive = Join-Path $script:TempRoot 'payload-cleanup.zip'
        Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
        $target = Join-Path $script:TempRoot 'target-cleanup'
        New-OwnedInstall -Root $target -Version 'v1.0.0'
        Set-Content -LiteralPath (Join-Path $target 'old-only.txt') -Value old
        Mock Unblock-File {}

        $cleanupWarnings = @()
        Install-XEArchive -Archive $archive -TargetPath $target -Version 'v2.0.0' `
            -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' `
            -TestFailBackupCleanup -WarningVariable cleanupWarnings

        Test-XEInstallReusable -Path $target -Version 'v2.0.0' `
            -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $target 'old-only.txt') | Should -BeFalse
        $cleanupWarnings -join "`n" | Should -Match 'new installation is committed'
        $backups = @(Get-ChildItem -LiteralPath $script:TempRoot -Force | Where-Object Name -Like '.xe-install-backup-*')
        $backups.Count | Should -Be 1
        Test-Path -LiteralPath (Join-Path $backups[0].FullName $script:OwnershipMarker) | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $target $script:OwnershipMarker) | Should -BeTrue
    }

    It 'pins Windows X64 and unblocks the archive' {
        $source = Get-Content -LiteralPath $script:InstallerPath -Raw
        $source | Should -Match 'RuntimeInformation\]::OSArchitecture'
        $source | Should -Match 'Architecture\]::X64'
        $source | Should -Match 'Unblock-File -LiteralPath \$Archive'
        $source | Should -Match 'Expand-Archive -LiteralPath \$Archive -DestinationPath \$TargetPath'
        $source | Should -Not -Match 'Expand-Archive[^\r\n]+DestinationPath \$stage'
    }
}

Describe 'ASP.NET Core runtime boundary' {
    It 'accepts exact or higher builds only within the required major and minor' {
        Test-XEAspNetCoreRuntimeCompatible -RequiredVersion '10.0.3' -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.3 [C:\dotnet]' | Should -BeTrue
        Test-XEAspNetCoreRuntimeCompatible -RequiredVersion '10.0.3' -RuntimeInventory 'Microsoft.AspNetCore.App 10.0.9 [C:\dotnet]' | Should -BeTrue
        Test-XEAspNetCoreRuntimeCompatible -RequiredVersion '10.0.3' -RuntimeInventory 'Microsoft.AspNetCore.App 10.1.9 [C:\dotnet]' | Should -BeFalse
        Test-XEAspNetCoreRuntimeCompatible -RequiredVersion '10.0.3' -RuntimeInventory 'Microsoft.AspNetCore.App 11.0.9 [C:\dotnet]' | Should -BeFalse
    }
}

Describe 'agentic post-install contracts' {
    BeforeEach {
        $script:TempRoot = Join-Path ([IO.Path]::GetTempPath()) "xe-post-install-pester-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:TempRoot | Out-Null
        $script:OldAdminEmail = $env:XE_ADMIN_EMAIL
        $script:OldAdminPassword = $env:XE_ADMIN_PASSWORD
        $script:OldUserProfile = $env:USERPROFILE
        $script:OldLocalAppData = $env:LOCALAPPDATA
        $script:OldDataDirectory = $env:XE_DATA_DIR
        $script:OldSystemRoot = $env:SystemRoot
        $script:OldUsername = $env:USERNAME
        $script:OldFailAutostartRegister = $env:XE_TEST_FAIL_AUTOSTART_REGISTER
        $script:OldUnicodeLaunchLog = $env:XE_UNICODE_LAUNCH_LOG
        $script:OldFailSkillSecondSwap = $env:XE_TEST_FAIL_SKILL_SECOND_SWAP
        $script:OldFailSkillRestore = $env:XE_TEST_FAIL_SKILL_RESTORE
        $script:OldDiagnosticMode = $env:XE_DIAGNOSTIC_MODE
        $env:USERPROFILE = Join-Path $script:TempRoot 'profile'
        $env:LOCALAPPDATA = Join-Path $script:TempRoot 'local'
        New-Item -ItemType Directory -Path $env:USERPROFILE, $env:LOCALAPPDATA -Force | Out-Null
    }
    AfterEach {
        $env:XE_ADMIN_EMAIL = $script:OldAdminEmail
        $env:XE_ADMIN_PASSWORD = $script:OldAdminPassword
        $env:USERPROFILE = $script:OldUserProfile
        $env:LOCALAPPDATA = $script:OldLocalAppData
        $env:XE_DATA_DIR = $script:OldDataDirectory
        $env:SystemRoot = $script:OldSystemRoot
        $env:USERNAME = $script:OldUsername
        $env:XE_TEST_FAIL_AUTOSTART_REGISTER = $script:OldFailAutostartRegister
        $env:XE_UNICODE_LAUNCH_LOG = $script:OldUnicodeLaunchLog
        $env:XE_TEST_FAIL_SKILL_SECOND_SWAP = $script:OldFailSkillSecondSwap
        $env:XE_TEST_FAIL_SKILL_RESTORE = $script:OldFailSkillRestore
        $env:XE_DIAGNOSTIC_MODE = $script:OldDiagnosticMode
        if (Test-Path -LiteralPath $script:TempRoot) { Remove-Item -LiteralPath $script:TempRoot -Recurse -Force }
    }

    It 'passes the setup password only through the environment and relays the machine contract' -Skip:(-not $IsLinux) {
        $engine = Join-Path $script:TempRoot 'engine.exe'
        $argumentLog = Join-Path $script:TempRoot 'arguments.log'
        @'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"$XE_TEST_ARGUMENT_LOG"
case "$1" in
  --setup) [[ "$XE_ADMIN_PASSWORD" == 'never-print-this' ]] || exit 3; echo XE_SETUP=created; echo "XE_ADMIN_EMAIL=$XE_ADMIN_EMAIL" ;;
  --mcp-key) echo 'warning: rotation' >&2; echo XE_MCP_KEY=xemcp_fixture ;;
esac
'@ | Set-Content -LiteralPath $engine -NoNewline
        & chmod +x $engine
        $env:XE_TEST_ARGUMENT_LOG = $argumentLog
        $env:XE_ADMIN_EMAIL = 'admin@localhost.test'
        $env:XE_ADMIN_PASSWORD = 'never-print-this'

        $output = @(Invoke-XESetup -Exe $engine -NonInteractive $true)

        $output | Should -Be @('XE_SETUP=created', 'XE_ADMIN_EMAIL=admin@localhost.test', 'XE_MCP_KEY=xemcp_fixture')
        (Get-Content -LiteralPath $argumentLog -Raw) | Should -Not -Match 'never-print-this'
        $output -join "`n" | Should -Not -Match 'never-print-this'
        $env:XE_ADMIN_PASSWORD | Should -BeExactly 'never-print-this'
    }

    It 'accepts already-configured setup without fabricating an email' -Skip:(-not $IsLinux) {
        $engine = Join-Path $script:TempRoot 'engine.exe'
        @'
#!/usr/bin/env bash
[[ "$1" == --setup ]] && { echo XE_SETUP=already-configured; exit 0; }
echo XE_MCP_KEY=xemcp_fixture
'@ | Set-Content -LiteralPath $engine -NoNewline
        & chmod +x $engine
        $env:XE_ADMIN_EMAIL = 'admin@localhost.test'
        $env:XE_ADMIN_PASSWORD = 'secret'
        @(Invoke-XESetup -Exe $engine -NonInteractive $true) |
            Should -Be @('XE_SETUP=already-configured', 'XE_MCP_KEY=xemcp_fixture')
    }

    It 'rejects missing non-interactive credentials and wrong or multiple MCP key lines' -Skip:(-not $IsLinux) {
        $env:XE_ADMIN_EMAIL = $null
        $env:XE_ADMIN_PASSWORD = $null
        { Resolve-XESetupCredential -NonInteractive $true } | Should -Throw '*requires XE_ADMIN_EMAIL*'

        $engine = Join-Path $script:TempRoot 'engine.exe'
        @'
#!/usr/bin/env bash
[[ "$1" == --setup ]] && { echo XE_SETUP=already-configured; exit 0; }
echo XE_MCP_KEY=xemcp_one
echo XE_MCP_KEY=xemcp_two
'@ | Set-Content -LiteralPath $engine -NoNewline
        & chmod +x $engine
        $env:XE_ADMIN_EMAIL = 'admin@localhost.test'
        $env:XE_ADMIN_PASSWORD = 'secret'
        { Invoke-XESetup -Exe $engine -NonInteractive $true } | Should -Throw '*exactly one XE_MCP_KEY*'
    }

    It 'preserves sanitized setup and key diagnostics without leaking secrets' -Skip:(-not $IsLinux) {
        $engine = Join-Path $script:TempRoot 'engine.exe'
        @'
#!/usr/bin/env bash
if [[ "$1" == --setup ]]; then
  if [[ "$XE_DIAGNOSTIC_MODE" == setup ]]; then
    echo "setup validation failed for password $XE_ADMIN_PASSWORD" >&2
    exit 3
  fi
  echo XE_SETUP=already-configured
  exit 0
fi
echo 'key backend rejected xemcp_hidden' >&2
exit 4
'@ | Set-Content -LiteralPath $engine -NoNewline
        & chmod +x $engine
        $env:XE_ADMIN_EMAIL = 'admin@localhost.test'
        $env:XE_ADMIN_PASSWORD = 'diagnostic-secret'
        $env:XE_DIAGNOSTIC_MODE = 'setup'
        $message = $null
        try { Invoke-XESetup -Exe $engine -NonInteractive $true } catch { $message = $_.Exception.Message }
        $message | Should -Match 'engine code 3: setup validation failed'
        $message | Should -Match '\[REDACTED\]'
        $message | Should -Not -Match 'diagnostic-secret'

        $env:XE_DIAGNOSTIC_MODE = 'key'
        try { Invoke-XESetup -Exe $engine -NonInteractive $true } catch { $message = $_.Exception.Message }
        $message | Should -Match 'engine code 4: key backend rejected \[REDACTED\]'
        $message | Should -Not -Match 'xemcp_hidden'
    }

    It 'validates canonical loopback ready evidence and rejects stale or remote evidence' {
        $data = Join-Path $script:TempRoot 'data'
        $valid = [pscustomobject]@{
            pid = 123; version = 'v1'; url = 'http://127.0.0.1:5199'
            mcpUrl = 'http://127.0.0.1:5199/api/local/v1/mcp/server'; dataDir = $data; startedAtUtc = '2026-08-22T00:00:00Z'
        }
        Test-XEReadyEvidence -Ready $valid -ProcessId 123 -DataDirectory $data | Should -BeTrue
        Test-XEReadyEvidence -Ready $valid -ProcessId 124 -DataDirectory $data | Should -BeFalse
        $valid.url = 'http://example.com:5199'
        Test-XEReadyEvidence -Ready $valid -ProcessId 123 -DataDirectory $data | Should -BeFalse
    }

    It 'prints exact readiness and PID only after the health probe succeeds' {
        $data = Join-Path $script:TempRoot 'data'
        New-Item -ItemType Directory -Path $data | Out-Null
        @{
            pid = 4321; version = 'v1.0.0'; url = 'http://127.0.0.1:5199'
            mcpUrl = 'http://127.0.0.1:5199/api/local/v1/mcp/server'; dataDir = $data; startedAtUtc = '2026-08-22T00:00:00Z'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'ready.json')
        Mock Start-Process { [pscustomobject]@{ Id = 4321; HasExited = $false } }
        Mock Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
        $fakeExe = Join-Path $script:TempRoot 'XE-Local-AI-Engine.exe'
        $output = @(Start-XEEngine -Exe $fakeExe -DataDirectory $data -TimeoutSeconds 1)
        $output[0] | Should -BeExactly "XE_READY=1 XE_VERSION=v1.0.0 XE_URL=http://127.0.0.1:5199 XE_MCP_URL=http://127.0.0.1:5199/api/local/v1/mcp/server XE_DATA_DIR=$data"
        $output[1] | Should -BeExactly 'XE_PID=4321'
        Should -Invoke Start-Process -ParameterFilter { $ArgumentList -eq '--mcp-only' -and $PassThru } -Times 1 -Exactly
        Should -Invoke Invoke-WebRequest -ParameterFilter { $Uri -eq 'http://127.0.0.1:5199/health/ready' } -Times 1 -Exactly
    }

    It 'times out without accepting absent readiness evidence and stops the child' {
        $data = Join-Path $script:TempRoot 'timeout-data'
        New-Item -ItemType Directory -Path $data | Out-Null
        Mock Start-Process { [pscustomobject]@{ Id = 9876; HasExited = $false } }
        Mock Start-Sleep { }
        Mock Stop-Process { }
        Mock Invoke-WebRequest { throw 'health must not be probed without ready evidence' }
        $fakeExe = Join-Path $script:TempRoot 'XE-Local-AI-Engine.exe'
        { Start-XEEngine -Exe $fakeExe -DataDirectory $data -TimeoutSeconds 0 } |
            Should -Throw '*did not produce live canonical ready.json*'
        Should -Invoke Stop-Process -ParameterFilter { $Id -eq 9876 -and $Force } -Times 1 -Exactly
        Should -Not -Invoke Invoke-WebRequest
    }

    It 'maps only readiness timeout to 12 and immediate start failures to generic 1' {
        Mock Get-XEDataDirectory { Join-Path $script:TempRoot 'data' }
        Mock Start-XEEngine { throw [InvalidOperationException]::new('prelaunch failed') }
        $message = $null
        try {
            Invoke-XEPostInstallAction -Exe (Join-Path $script:TempRoot 'engine.exe') -ResolvedVersion v1 `
                -ApiBase 'https://api.github.com' -Scratch $script:TempRoot -DoSetup $false -DoStart $true `
                -DoAutostart $false -DoInstallSkill $false -NonInteractive $true
        }
        catch { $message = $_.Exception.Message }
        $message | Should -Match '^XE_INSTALLER_CODE=1 prelaunch failed'

        Mock Start-XEEngine { throw [TimeoutException]::new('readiness timed out') }
        try {
            Invoke-XEPostInstallAction -Exe (Join-Path $script:TempRoot 'engine.exe') -ResolvedVersion v1 `
                -ApiBase 'https://api.github.com' -Scratch $script:TempRoot -DoSetup $false -DoStart $true `
                -DoAutostart $false -DoInstallSkill $false -NonInteractive $true
        }
        catch { $message = $_.Exception.Message }
        $message | Should -Match '^XE_INSTALLER_CODE=12 readiness timed out'
    }

    It 'resolves one custom data directory for setup and autostart, then restores the caller environment' {
        $customData = Join-Path $script:TempRoot 'custom data'
        $env:XE_DATA_DIR = "$customData/../custom data"
        $script:SetupDataDirectory = $null
        $script:AutostartDataDirectory = $null
        Mock Invoke-XESetup { $script:SetupDataDirectory = $env:XE_DATA_DIR }
        Mock Register-XEAutostart { $script:AutostartDataDirectory = $DataDirectory }

        Invoke-XEPostInstallAction -Exe (Join-Path $script:TempRoot 'engine.exe') -ResolvedVersion v1 `
            -ApiBase 'https://api.github.com' -Scratch $script:TempRoot -DoSetup $true -DoStart $false `
            -DoAutostart $true -DoInstallSkill $false -NonInteractive $true

        $resolved = Resolve-XEInstallPath -Path $customData
        $script:SetupDataDirectory | Should -BeExactly $resolved
        $script:AutostartDataDirectory | Should -BeExactly $resolved
        $env:XE_DATA_DIR | Should -BeExactly "$customData/../custom data"
    }

    It 'rejects control characters in persisted autostart paths' {
        { Assert-XEAutostartPath -Name 'Data' -Path "/tmp/data`nnext" } | Should -Throw '*control characters*'
        { Assert-XEAutostartPath -Name 'Data' -Path ("/tmp/data" + [char]0 + 'tail') } | Should -Throw '*control characters*'
    }

    It 'registers an idempotent limited current-user task through a PS 5.1-safe Unicode launcher' {
        $runningOnWindows = $env:OS -eq 'Windows_NT'
        Mock Get-ScheduledTask { $null }
        Mock New-ScheduledTaskAction { [pscustomobject]@{ Execute = $Execute; Arguments = $Argument } }
        Mock New-ScheduledTaskTrigger { 'trigger' }
        Mock New-ScheduledTaskPrincipal { 'principal' }
        Mock Register-ScheduledTask { }
        $env:USERNAME = 'fixture-user'
        $env:SystemRoot = 'C:\Windows'
        $engineName = if ($runningOnWindows) { "owner's engine ü%.cmd" } else { "owner's engine ü%.exe" }
        $exe = Join-Path $script:TempRoot "XE app ü%/$engineName"
        $data = Join-Path $script:TempRoot "XE data ü%/owner's files"
        New-Item -ItemType Directory -Path (Split-Path -Parent $exe), $data -Force | Out-Null
        $executionLog = Join-Path $script:TempRoot 'unicode-launch.log'
        if ($runningOnWindows) {
            @'
@echo off
>"%XE_UNICODE_LAUNCH_LOG%" echo(%XE_DATA_DIR%^|%*
exit /b 0
'@ | Set-Content -LiteralPath $exe -Encoding Ascii
        }
        else {
            @'
#!/usr/bin/env bash
printf '%s|%s\n' "$XE_DATA_DIR" "$*" >"$XE_UNICODE_LAUNCH_LOG"
'@ | Set-Content -LiteralPath $exe -NoNewline
            & chmod +x $exe
        }
        $env:XE_UNICODE_LAUNCH_LOG = $executionLog

        Register-XEAutostart -Exe $exe -DataDirectory $data
        $launcherDirectory = Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-Autostart'
        $launcher = @(Get-ChildItem -LiteralPath $launcherDirectory -Filter 'launch-*.ps1')[0].FullName
        $firstBytes = [IO.File]::ReadAllBytes($launcher)
        Register-XEAutostart -Exe $exe -DataDirectory $data

        @(Get-ChildItem -LiteralPath $launcherDirectory -Filter 'launch-*.ps1').Count | Should -Be 1
        $firstBytes[0..2] | Should -Be @(0xEF, 0xBB, 0xBF)
        $firstContent = Get-Content -LiteralPath $launcher -Raw
        $firstContent | Should -Match ([regex]::Escape("`$env:XE_DATA_DIR = '$($data.Replace("'", "''"))'"))
        $firstContent | Should -Match ([regex]::Escape("& '$($exe.Replace("'", "''"))' '--mcp-only'"))
        $firstContent | Should -Not -Match 'XE_ADMIN|password|credential'
        if ($runningOnWindows) {
            $windowsPowerShell = [IO.Path]::Combine(
                $env:SystemRoot, 'System32\WindowsPowerShell\v1.0\powershell.exe')
            Test-Path -LiteralPath $windowsPowerShell -PathType Leaf | Should -BeTrue
            $childVersion = & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -Command `
                '$PSVersionTable.PSEdition + ''|'' + $PSVersionTable.PSVersion.Major'
            $childVersion | Should -BeExactly 'Desktop|5'
            & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -File $launcher
        }
        else {
            Write-Warning 'Windows PowerShell 5.1 execution is unavailable; exercising the launcher with pwsh as an explicit non-Windows parser fallback.'
            & pwsh -NoLogo -NoProfile -File $launcher
        }
        (Get-Content -LiteralPath $executionLog -Raw).Trim() | Should -BeExactly "$data|--mcp-only"
        Should -Invoke New-ScheduledTaskAction -ParameterFilter {
            $Execute -eq [IO.Path]::Combine('C:\Windows', 'System32\WindowsPowerShell\v1.0\powershell.exe') -and
            $Argument -match '-NoProfile' -and $Argument -match ([regex]::Escape($launcher))
        } -Times 2 -Exactly
        Should -Invoke New-ScheduledTaskTrigger -ParameterFilter { $AtLogOn -and $User -eq 'fixture-user' } -Times 2 -Exactly
        Should -Invoke New-ScheduledTaskPrincipal -ParameterFilter { $RunLevel -eq 'Limited' -and $UserId -eq 'fixture-user' } -Times 2 -Exactly
        Should -Invoke Register-ScheduledTask -ParameterFilter { $TaskName -eq 'XE Local AI Engine' -and $Force } -Times 2 -Exactly
    }

    It 'rejects unowned or reparse launcher state' {
        $directory = Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-Autostart'
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $directory 'unexpected.txt') -Value unowned
        { Assert-XEAutostartOwnership -LauncherDirectory $directory -ExistingTask $null } |
            Should -Throw '*ownership marker*'
        Remove-Item -LiteralPath $directory -Recurse -Force
        if ($IsLinux) {
            $target = Join-Path $script:TempRoot 'link-target'
            New-Item -ItemType Directory -Path $target | Out-Null
            New-Item -ItemType SymbolicLink -Path $directory -Target $target | Out-Null
            { Assert-XEAutostartOwnership -LauncherDirectory $directory -ExistingTask $null } |
                Should -Throw '*regular installer-owned directory*'
        }
    }

    It 'restores prior launcher bytes, data directory, and task XML on registration failure' {
        $script:RegisteredTask = $null
        $script:RestoredTaskXml = $null
        Mock Get-ScheduledTask { $script:RegisteredTask }
        Mock Export-ScheduledTask { '<Task enabled="true">prior</Task>' }
        Mock New-ScheduledTaskAction { [pscustomobject]@{ Execute = $Execute; Arguments = $Argument } }
        Mock New-ScheduledTaskTrigger { 'trigger' }
        Mock New-ScheduledTaskPrincipal { 'principal' }
        Mock Register-ScheduledTask {
            if ($Xml) { $script:RestoredTaskXml = $Xml; return }
            $script:RegisteredTask = [pscustomobject]@{ Actions = @($Action) }
        }
        $env:USERNAME = 'fixture-user'
        $exe = Join-Path $script:TempRoot 'engine.exe'
        Set-Content -LiteralPath $exe -Value stub
        $oldData = Join-Path $script:TempRoot 'old data'
        $newData = Join-Path $script:TempRoot 'new data'
        Register-XEAutostart -Exe $exe -DataDirectory $oldData
        $directory = Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-Autostart'
        $oldLauncher = @(Get-ChildItem -LiteralPath $directory -Filter 'launch-*.ps1')[0].FullName
        $oldHash = (Get-FileHash -LiteralPath $oldLauncher -Algorithm SHA256).Hash

        $env:XE_TEST_FAIL_AUTOSTART_REGISTER = '1'
        { Register-XEAutostart -Exe $exe -DataDirectory $newData } | Should -Throw '*were restored*'
        $env:XE_TEST_FAIL_AUTOSTART_REGISTER = $null

        @(Get-ChildItem -LiteralPath $directory -Filter 'launch-*.ps1').Count | Should -Be 1
        (Get-FileHash -LiteralPath $oldLauncher -Algorithm SHA256).Hash | Should -BeExactly $oldHash
        $script:RestoredTaskXml | Should -BeExactly '<Task enabled="true">prior</Task>'
    }

    It 'extracts one release-pinned skill tree and rejects traversal and symlinks' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $validZip = Join-Path $script:TempRoot 'valid.zip'
        $stream = [IO.File]::Create($validZip)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        $entry = $archive.CreateEntry('prefix/skills/xe-local-ai-engine/SKILL.md')
        $writer = [IO.StreamWriter]::new($entry.Open()); $writer.Write('# skill'); $writer.Dispose()
        $archive.Dispose(); $stream.Dispose()
        $destination = Join-Path $script:TempRoot 'extracted'
        Expand-XESkillArchive -Archive $validZip -Destination $destination
        (Get-Content -LiteralPath (Join-Path $destination 'SKILL.md') -Raw) | Should -BeExactly '# skill'

        Mock Invoke-XEWebRequest {
            Copy-Item -LiteralPath $validZip -Destination $OutFile -Force
            [pscustomobject]@{ StatusCode = 200 }
        }
        Install-XESkill -Version 'v1.2.3' -ApiBase 'https://api.github.com' -Scratch $script:TempRoot
        Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.claude/skills/xe-local-ai-engine/SKILL.md') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.agents/skills/xe-local-ai-engine/SKILL.md') | Should -BeTrue
        Set-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude/skills/xe-local-ai-engine/stale.txt') -Value stale
        Install-XESkill -Version 'v1.2.3' -ApiBase 'https://api.github.com' -Scratch $script:TempRoot
        Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.claude/skills/xe-local-ai-engine/stale.txt') | Should -BeFalse
        Should -Invoke Invoke-XEWebRequest -ParameterFilter {
            $Uri -eq 'https://api.github.com/repos/w0rldx/XE-Local-AI-Engine.Source/zipball/v1.2.3'
        } -Times 2 -Exactly

        Set-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude/skills/xe-local-ai-engine/SKILL.md') -Value old-claude
        Set-Content -LiteralPath (Join-Path $env:USERPROFILE '.agents/skills/xe-local-ai-engine/SKILL.md') -Value old-agent
        $env:XE_TEST_FAIL_SKILL_SECOND_SWAP = '1'
        { Install-XESkill -Version 'v1.2.3' -ApiBase 'https://api.github.com' -Scratch $script:TempRoot } |
            Should -Throw '*Prior skill destinations were restored*'
        (Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.claude/skills/xe-local-ai-engine/SKILL.md') -Raw).Trim() |
            Should -BeExactly old-claude
        (Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.agents/skills/xe-local-ai-engine/SKILL.md') -Raw).Trim() |
            Should -BeExactly old-agent
        @(Get-ChildItem -LiteralPath $env:USERPROFILE -Recurse -Force | Where-Object Name -Like '.xe-skill-*').Count |
            Should -Be 0

        $env:XE_TEST_FAIL_SKILL_RESTORE = '1'
        $message = $null
        try { Install-XESkill -Version 'v1.2.3' -ApiBase 'https://api.github.com' -Scratch $script:TempRoot }
        catch { $message = $_.Exception.Message }
        $message | Should -Match 'Rollback failed; retained backup:'
        $retainedPath = ($message -split 'retained backup: ', 2)[1]
        Test-Path -LiteralPath $retainedPath -PathType Container | Should -BeTrue
        (Get-Content -LiteralPath (Join-Path $retainedPath 'SKILL.md') -Raw).Trim() | Should -BeExactly old-claude
        (Get-Content -LiteralPath (Join-Path $env:USERPROFILE '.agents/skills/xe-local-ai-engine/SKILL.md') -Raw).Trim() |
            Should -BeExactly old-agent

        $unsafeZip = Join-Path $script:TempRoot 'unsafe.zip'
        $stream = [IO.File]::Create($unsafeZip)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        $null = $archive.CreateEntry('prefix/skills/xe-local-ai-engine/../../escape.txt')
        $archive.Dispose(); $stream.Dispose()
        { Expand-XESkillArchive -Archive $unsafeZip -Destination (Join-Path $script:TempRoot 'unsafe') } |
            Should -Throw '*unsafe path*'

        $linkZip = Join-Path $script:TempRoot 'link.zip'
        $stream = [IO.File]::Create($linkZip)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        $entry = $archive.CreateEntry('prefix/skills/xe-local-ai-engine/SKILL.md'); $entry.ExternalAttributes = (0xA000 -shl 16)
        $archive.Dispose(); $stream.Dispose()
        { Expand-XESkillArchive -Archive $linkZip -Destination (Join-Path $script:TempRoot 'link') } |
            Should -Throw '*symbolic link*'
    }

    It 'does not contain password argv or elevated/system-wide autostart forms' {
        $source = Get-Content -LiteralPath $script:InstallerPath -Raw
        $source | Should -Not -Match "ArgumentList[^\r\n]+admin-password"
        $source | Should -Not -Match 'RunLevel\s+Highest'
        $source | Should -Not -Match 'New-ScheduledTaskTrigger\s+-AtStartup'
        $source | Should -Match 'XE_AUTOSTART'
        $source | Should -Match 'XE_INSTALL_SKILL'
        $source | Should -Match 'XE_START'
        $source | Should -Match 'XE_SETUP'
        $source | Should -Match '\(-not \$NoAutostart\.IsPresent\).+XE_AUTOSTART'
        $source | Should -Not -Match 'keyOutput\s*=\s*.*(New-TemporaryFile|GetTempFileName)'
    }
}
