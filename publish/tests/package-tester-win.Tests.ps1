#Requires -Modules Pester

<#
    Pester coverage for the pure/helper logic inside publish/package-tester-win.ps1.

    WHY THIS FILE BINDS TO THE SCRIPT VIA AST RATHER THAN COPYING ITS LOGIC
    ----------------------------------------------------------------------
    package-tester-win.ps1 is a straight-line script: `param()` at the top, helper functions in the
    middle, and the packaging pipeline at file scope. Dot-sourcing it to reach the helpers would RUN
    the whole release (clean-tree gate, restore, build, publish, vpk pack, GitHub upload), so the
    usual "dot-source and call" pattern is unavailable.

    The alternative — retyping the regexes and the audit pipeline into this file — produces tests
    that pass forever while the real script rots, which is the exact failure mode that let a broken
    vulnerability gate ship. So instead every subject under test is EXTRACTED FROM THE REAL SCRIPT
    with the PowerShell parser and executed verbatim:

      * helper functions      -> the FunctionDefinitionAst's own text is re-declared here
      * the audit pipeline    -> the `$vulnerablePackages = @(...)` assignment is re-executed
      * the two regexes       -> the string literals are read out of the AST

    Every extraction throws if its anchor is missing. If someone renames a function, restructures the
    audit loop, or edits a regex, these tests FAIL LOUDLY instead of quietly grading a stale copy.

    NOT COVERED HERE ON PURPOSE: the C# suite already pins the client-ID regex against
    AppUpdateChannelOptions.IsConfigured via IsConfigured_ClientIdVerdict_MatchesThePackagingScriptAssertion.
    This file asserts the script's own predicate behaviour; it does not restate that drift guard.

    Run:  pwsh -NoProfile -Command "Invoke-Pester publish/tests -Output Detailed"
          (or: scripts/lint-release-scripts.sh --pester)
#>

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:ScriptPath = Join-Path $script:RepoRoot 'publish/package-tester-win.ps1'

    if (-not (Test-Path $script:ScriptPath)) {
        throw "package-tester-win.ps1 not found at '$script:ScriptPath'. These tests bind to the real script."
    }

    $parseErrors = $null
    $script:ScriptAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:ScriptPath, [ref]$null, [ref]$parseErrors)

    if ($parseErrors -and @($parseErrors | Where-Object { $_ }).Count -gt 0) {
        throw "package-tester-win.ps1 does not parse:`n$($parseErrors -join "`n")"
    }

    # --- extraction helpers -------------------------------------------------------------------
    function Get-ScriptFunctionText {
        param([Parameter(Mandatory)][string]$Name)

        $match = @($script:ScriptAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $Name
                }, $true))

        if ($match.Count -ne 1) {
            throw "Expected exactly one function '$Name' in package-tester-win.ps1, found $($match.Count). " +
            "The script was restructured — update these tests rather than deleting them."
        }
        return $match[0].Extent.Text
    }

    function Get-ScriptAssignmentText {
        param([Parameter(Mandatory)][string]$VariableText)

        $match = @($script:ScriptAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left.Extent.Text -eq $VariableText
                }, $true))

        if ($match.Count -ne 1) {
            throw "Expected exactly one assignment to '$VariableText' in package-tester-win.ps1, found $($match.Count)."
        }
        return $match[0].Extent.Text
    }

    # Anchor is a plain SUBSTRING, never a wildcard/regex — the values being located are themselves
    # regexes full of [] and (), which -like and -match would reinterpret.
    function Get-ScriptStringLiteralMatching {
        param([Parameter(Mandatory)][string]$Anchor)

        $match = @($script:ScriptAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                    $node.Value.Contains($Anchor)
                }, $true) | ForEach-Object { $_.Value } | Select-Object -Unique)

        if ($match.Count -ne 1) {
            throw "Expected exactly one string literal containing '$Anchor' in package-tester-win.ps1, found $($match.Count)."
        }
        return $match[0]
    }

    # --- subjects under test, lifted verbatim from the script ---------------------------------
    $script:VulnerabilityPipeline = Get-ScriptAssignmentText -VariableText '$vulnerablePackages'
    $script:SemVerPattern = Get-ScriptStringLiteralMatching -Anchor '^[0-9]+\.[0-9]+\.[0-9]+'
    $script:ClientIdPattern = Get-ScriptStringLiteralMatching -Anchor '^Iv[0-9A-Za-z.]'
    $script:PlaceholderPattern = Get-ScriptStringLiteralMatching -Anchor '^(REPLACE_'

    . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Get-ProjectVersion')))
    . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Assert-LastExitCode')))

    # Runs the REAL audit pipeline over a supplied `dotnet package list --vulnerable` payload.
    function Invoke-VulnerabilityAudit {
        param([Parameter(Mandatory)][AllowNull()]$NugetAudit)

        $nugetAudit = $NugetAudit
        return @(& ([scriptblock]::Create("$script:VulnerabilityPipeline; `$vulnerablePackages")))
    }

    function New-PropsFile {
        param([string]$Prefix, [string]$Suffix)

        $dir = Join-Path ([System.IO.Path]::GetTempPath()) "xe-props-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null

        $prefixLine = if ($null -ne $Prefix) { "    <VersionPrefix>$Prefix</VersionPrefix>`n" } else { '' }
        $suffixLine = if ($Suffix) { "    <VersionSuffix>$Suffix</VersionSuffix>`n" } else { '' }
        @"
<Project>
  <PropertyGroup>
$prefixLine$suffixLine  </PropertyGroup>
</Project>
"@ | Set-Content -Path (Join-Path $dir 'Directory.Build.props') -Encoding utf8
        return $dir
    }
}

Describe 'Backend NuGet vulnerability audit parsing' {

    # THE REGRESSION THIS FILE EXISTS FOR. @($null).Count is 1 in PowerShell, not 0. `dotnet package
    # list --vulnerable` omits `frameworks` entirely from a clean project, so without the
    # Where-Object guards each clean project yielded phantom rows and the gate threw on a perfectly
    # clean solution — meaning nothing downstream of it (SPA build, pack, upload) had ever run.
    Context 'a clean solution' {

        It 'reports zero vulnerable packages when projects omit the frameworks key entirely' {
            $audit = [pscustomobject]@{
                projects = @(
                    [pscustomobject]@{ path = 'A.csproj' },
                    [pscustomobject]@{ path = 'B.csproj' }
                )
            }

            $result = Invoke-VulnerabilityAudit -NugetAudit $audit
            $result.Count | Should -Be 0
        }

        It 'reports zero when frameworks exist but carry no vulnerable packages' {
            $audit = [pscustomobject]@{
                projects = @(
                    [pscustomobject]@{
                        path       = 'A.csproj'
                        frameworks = @(
                            [pscustomobject]@{
                                framework           = 'net10.0'
                                topLevelPackages    = @()
                                transitivePackages  = @()
                            }
                        )
                    }
                )
            }

            $result = Invoke-VulnerabilityAudit -NugetAudit $audit
            $result.Count | Should -Be 0
        }

        It 'reports zero when a package carries a null vulnerabilities member' {
            # The precise @($null).Count -eq 1 trap, at the innermost level.
            $audit = [pscustomobject]@{
                projects = @(
                    [pscustomobject]@{
                        path       = 'A.csproj'
                        frameworks = @(
                            [pscustomobject]@{
                                topLevelPackages = @(
                                    [pscustomobject]@{ id = 'Clean.Package'; resolvedVersion = '1.0.0'; vulnerabilities = $null }
                                )
                            }
                        )
                    }
                )
            }

            $result = Invoke-VulnerabilityAudit -NugetAudit $audit
            $result.Count | Should -Be 0
        }

        It 'reports zero for a payload with no projects at all' {
            $result = Invoke-VulnerabilityAudit -NugetAudit ([pscustomobject]@{ projects = @() })
            $result.Count | Should -Be 0
        }
    }

    Context 'a solution with real vulnerabilities' {

        It 'detects a vulnerable top-level package and carries its identity through' {
            $audit = [pscustomobject]@{
                projects = @(
                    [pscustomobject]@{
                        path       = 'Vulnerable.csproj'
                        frameworks = @(
                            [pscustomobject]@{
                                topLevelPackages = @(
                                    [pscustomobject]@{
                                        id              = 'Bad.Package'
                                        resolvedVersion = '1.2.3'
                                        vulnerabilities = @(
                                            [pscustomobject]@{ severity = 'High'; advisoryurl = 'https://example.invalid/1' }
                                        )
                                    }
                                )
                            }
                        )
                    }
                )
            }

            $result = Invoke-VulnerabilityAudit -NugetAudit $audit

            $result.Count            | Should -Be 1
            $result[0].Project       | Should -Be 'Vulnerable.csproj'
            $result[0].Package       | Should -Be 'Bad.Package'
            $result[0].Version       | Should -Be '1.2.3'
            @($result[0].Vulnerabilities).Count | Should -Be 1
        }

        It 'detects a vulnerable transitive package' {
            $audit = [pscustomobject]@{
                projects = @(
                    [pscustomobject]@{
                        path       = 'Transitive.csproj'
                        frameworks = @(
                            [pscustomobject]@{
                                topLevelPackages   = @()
                                transitivePackages = @(
                                    [pscustomobject]@{
                                        id              = 'Deep.Package'
                                        resolvedVersion = '4.5.6'
                                        vulnerabilities = @([pscustomobject]@{ severity = 'Critical' })
                                    }
                                )
                            }
                        )
                    }
                )
            }

            $result = Invoke-VulnerabilityAudit -NugetAudit $audit
            $result.Count      | Should -Be 1
            $result[0].Package | Should -Be 'Deep.Package'
        }

        It 'aggregates across projects, frameworks, and both package collections' {
            $audit = [pscustomobject]@{
                projects = @(
                    [pscustomobject]@{ path = 'Clean.csproj' },
                    [pscustomobject]@{
                        path       = 'Mixed.csproj'
                        frameworks = @(
                            [pscustomobject]@{
                                topLevelPackages   = @(
                                    [pscustomobject]@{ id = 'Top'; resolvedVersion = '1.0.0'; vulnerabilities = @([pscustomobject]@{ severity = 'Low' }) }
                                )
                                transitivePackages = @(
                                    [pscustomobject]@{ id = 'Trans'; resolvedVersion = '2.0.0'; vulnerabilities = @([pscustomobject]@{ severity = 'High' }) }
                                )
                            },
                            [pscustomobject]@{
                                topLevelPackages = @(
                                    [pscustomobject]@{ id = 'Other'; resolvedVersion = '3.0.0'; vulnerabilities = @([pscustomobject]@{ severity = 'Moderate' }) }
                                )
                            }
                        )
                    }
                )
            }

            $result = Invoke-VulnerabilityAudit -NugetAudit $audit
            $result.Count | Should -Be 3
            ($result.Package | Sort-Object) -join ',' | Should -Be 'Other,Top,Trans'
        }
    }
}

Describe 'Get-ProjectVersion' {

    It 'composes prefix and suffix into a prerelease version' {
        $dir = New-PropsFile -Prefix '0.1.0' -Suffix 'rc.4.2'
        Push-Location $dir
        try { Get-ProjectVersion | Should -Be '0.1.0-rc.4.2' }
        finally { Pop-Location; Remove-Item $dir -Recurse -Force }
    }

    It 'returns a bare prefix when no suffix is present' {
        $dir = New-PropsFile -Prefix '1.2.3' -Suffix $null
        Push-Location $dir
        try { Get-ProjectVersion | Should -Be '1.2.3' }
        finally { Pop-Location; Remove-Item $dir -Recurse -Force }
    }

    It 'throws when VersionPrefix is missing rather than emitting a bare dash' {
        $dir = New-PropsFile -Prefix $null -Suffix 'rc.1'
        Push-Location $dir
        try { { Get-ProjectVersion } | Should -Throw '*VersionPrefix*' }
        finally { Pop-Location; Remove-Item $dir -Recurse -Force }
    }

    It 'agrees with the repository''s own Directory.Build.props' {
        Push-Location $script:RepoRoot
        try { Get-ProjectVersion | Should -Match $script:SemVerPattern }
        finally { Pop-Location }
    }
}

Describe 'Project version SemVer gate' {

    It 'accepts <_>' -ForEach @('0.1.0', '1.2.3', '0.1.0-rc.4.2', '10.20.30-alpha', '1.0.0-rc.1.2.3') {
        $_ -match $script:SemVerPattern | Should -BeTrue
    }

    It 'rejects <_>' -ForEach @('1.2', '1.2.3.4', 'v1.2.3', '1.2.3-', '', 'not-a-version', '1.2.3-rc.1+build') {
        $_ -match $script:SemVerPattern | Should -BeFalse
    }
}

Describe 'GitHub App client ID predicate' {

    # Applies the script's own two extracted patterns in the script's own order. Must live in
    # BeforeAll: in Pester 6 a function declared directly in a Describe body is not visible to It.
    BeforeAll {
        function Test-ClientIdAccepted {
            param([string]$Value)
            if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
            if ($Value -match $script:PlaceholderPattern -or $Value -notmatch $script:ClientIdPattern) { return $false }
            return $true
        }
    }

    It 'accepts a real Iv-prefixed client ID' {
        Test-ClientIdAccepted -Value 'Iv23lIabcdefghijklmn' | Should -BeTrue
    }

    It 'rejects placeholder <_>' -ForEach @('REPLACE_ME', 'CHANGE_ME', 'TODO', 'REPLACE_WITH_CLIENT_ID') {
        Test-ClientIdAccepted -Value $_ | Should -BeFalse
    }

    It 'rejects a numeric App ID, which is not a client ID' {
        Test-ClientIdAccepted -Value '123456' | Should -BeFalse
    }

    It 'rejects an Iv value that is too short' {
        Test-ClientIdAccepted -Value 'Iv1short' | Should -BeFalse
    }

    It 'rejects empty and whitespace' {
        Test-ClientIdAccepted -Value ''    | Should -BeFalse
        Test-ClientIdAccepted -Value '   ' | Should -BeFalse
    }
}

Describe 'Find-GitHubRelease tag-form resolution' {

    # Historical tester releases carry a BARE tag ("0.1.0-rc.4.1") while this script uploads with
    # --tag v<version>. A lookup that probes only one form makes the already-published guard blind
    # to the live release and lets `vpk upload --merge` push untested assets into a shipped feed.
    BeforeEach {
        $script:RequestedTags = [System.Collections.Generic.List[string]]::new()
    }

    It 'finds a release published under the v-prefixed tag' {
        function Get-GitHubReleaseByTag {
            param([string]$ReleaseTag, [string]$RepositorySlug)
            $script:RequestedTags.Add($ReleaseTag)
            if ($ReleaseTag -eq 'v0.1.0-rc.4.2') { return [pscustomobject]@{ tagName = $ReleaseTag } }
            return $null
        }
        . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Find-GitHubRelease')))

        $found = Find-GitHubRelease -ReleaseVersion '0.1.0-rc.4.2' -RepositorySlug 'owner/repo'

        $found.tagName | Should -Be 'v0.1.0-rc.4.2'
        $script:RequestedTags[0] | Should -Be 'v0.1.0-rc.4.2'
    }

    It 'falls back to the bare tag when the v-prefixed form does not exist' {
        function Get-GitHubReleaseByTag {
            param([string]$ReleaseTag, [string]$RepositorySlug)
            $script:RequestedTags.Add($ReleaseTag)
            if ($ReleaseTag -eq '0.1.0-rc.4.1') { return [pscustomobject]@{ tagName = $ReleaseTag } }
            return $null
        }
        . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Find-GitHubRelease')))

        $found = Find-GitHubRelease -ReleaseVersion '0.1.0-rc.4.1' -RepositorySlug 'owner/repo'

        $found.tagName | Should -Be '0.1.0-rc.4.1'
        $script:RequestedTags -join ',' | Should -Be 'v0.1.0-rc.4.1,0.1.0-rc.4.1'
    }

    It 'probes BOTH tag forms before giving up' {
        function Get-GitHubReleaseByTag {
            param([string]$ReleaseTag, [string]$RepositorySlug)
            $script:RequestedTags.Add($ReleaseTag)
            return $null
        }
        # No release matches by tag or by name, so the list fallback also misses.
        function gh { $global:LASTEXITCODE = 0; return '[]' }
        . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Find-GitHubRelease')))

        $found = Find-GitHubRelease -ReleaseVersion '9.9.9' -RepositorySlug 'owner/repo'

        $found | Should -BeNullOrEmpty
        $script:RequestedTags -join ',' | Should -Be 'v9.9.9,9.9.9'
    }

    It 'falls back to matching a release by NAME when the tag is spelled a third way' {
        function Get-GitHubReleaseByTag {
            param([string]$ReleaseTag, [string]$RepositorySlug)
            $script:RequestedTags.Add($ReleaseTag)
            if ($ReleaseTag -eq 'release-0.1.0-rc.5') { return [pscustomobject]@{ tagName = $ReleaseTag; viaName = $true } }
            return $null
        }
        function gh {
            $global:LASTEXITCODE = 0
            return (@([pscustomobject]@{ name = 'v0.1.0-rc.5'; tagName = 'release-0.1.0-rc.5' }) | ConvertTo-Json -AsArray)
        }
        . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Find-GitHubRelease')))

        $found = Find-GitHubRelease -ReleaseVersion '0.1.0-rc.5' -RepositorySlug 'owner/repo'

        $found.viaName | Should -BeTrue
        # It re-fetches by the REAL tag, because `gh release list --json` cannot return assets.
        $script:RequestedTags[-1] | Should -Be 'release-0.1.0-rc.5'
    }

    It 'throws when the release list itself fails, rather than reporting "not published"' {
        function Get-GitHubReleaseByTag {
            param([string]$ReleaseTag, [string]$RepositorySlug)
            return $null
        }
        function gh { $global:LASTEXITCODE = 1; return 'gh: authentication required' }
        . ([scriptblock]::Create((Get-ScriptFunctionText -Name 'Find-GitHubRelease')))

        { Find-GitHubRelease -ReleaseVersion '0.1.0' -RepositorySlug 'owner/repo' } |
            Should -Throw '*release list failed*'
    }
}

Describe 'Assert-LastExitCode' {

    It 'throws naming the operation when the last exit code is non-zero' {
        $global:LASTEXITCODE = 3
        { Assert-LastExitCode -Operation 'Backend restore' } | Should -Throw '*Backend restore*3*'
    }

    It 'is silent on success' {
        $global:LASTEXITCODE = 0
        { Assert-LastExitCode -Operation 'Backend restore' } | Should -Not -Throw
    }
}
