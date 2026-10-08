#Requires -Modules Pester

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $scriptPath = Join-Path $script:RepoRoot 'publish/windows/uninstall-xe-local-ai-engine.ps1'
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
    if (@($parseErrors | Where-Object { $_ }).Count -gt 0) { throw "uninstall-xe-local-ai-engine.ps1 does not parse:`n$($parseErrors -join "`n")" }

    foreach ($name in @('Test-UnderDirectory', 'Get-XEUninstallTarget')) {
        $found = @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true))
        if ($found.Count -ne 1) { throw "Expected one function '$name', found $($found.Count)." }
        . ([scriptblock]::Create($found[0].Extent.Text))
    }

    $script:DataDir = Join-Path ([IO.Path]::GetTempPath()) 'xe-uninstall-data'
    $script:Elsewhere = Join-Path ([IO.Path]::GetTempPath()) 'xe-uninstall-elsewhere'
    $script:InstallDir = Join-Path ([IO.Path]::GetTempPath()) 'xe-uninstall-install'
    function New-FakeProcess {
        param([int] $Id, [string] $Name, [string] $Path)
        [pscustomobject]@{ Id = $Id; Name = $Name; Path = $Path }
    }
}

Describe 'Get-XEUninstallTarget' {
    It 'stops the shell and launcher first, then the app, then owned whisper-server and trainer processes' {
        Mock Get-Process -ParameterFilter { $Name -contains 'XE-Local-AI-Engine.Desktop' } -MockWith {
            New-FakeProcess -Id 10 -Name 'XE-Local-AI-Engine.Desktop' -Path (Join-Path $script:InstallDir 'XE-Local-AI-Engine.Desktop.exe')
            New-FakeProcess -Id 12 -Name 'XE-Local-AI-Engine.Desktop' -Path (Join-Path $script:Elsewhere 'XE-Local-AI-Engine.Desktop.exe')
        }
        Mock Get-Process -ParameterFilter { $Name -contains 'XE-Local-AI-Engine.WindowsLauncher' } -MockWith {
            New-FakeProcess -Id 11 -Name 'XE-Local-AI-Engine.WindowsLauncher' -Path (Join-Path $script:InstallDir 'XE-Local-AI-Engine.WindowsLauncher.exe')
        }
        # A same-named host of another install or checkout (22) must keep running; one inside the data dir (21) is ours.
        Mock Get-Process -ParameterFilter { $Name -contains 'XE-Local-AI-Engine.Client' } -MockWith {
            New-FakeProcess -Id 20 -Name 'XE-Local-AI-Engine.Client' -Path (Join-Path $script:InstallDir 'current/XE-Local-AI-Engine.Client.exe')
            New-FakeProcess -Id 21 -Name 'XE-Local-AI-Engine.Client' -Path (Join-Path $script:DataDir 'current/XE-Local-AI-Engine.Client.exe')
            New-FakeProcess -Id 22 -Name 'XE-Local-AI-Engine.Client' -Path (Join-Path $script:Elsewhere 'checkout/XE-Local-AI-Engine.Client.exe')
        }
        Mock Get-Process -ParameterFilter { $Name -contains 'whisper-server' } -MockWith {
            New-FakeProcess -Id 30 -Name 'whisper-server' -Path (Join-Path $script:DataDir 'whisper.cpp/whisper-server.exe')
            New-FakeProcess -Id 31 -Name 'python' -Path (Join-Path $script:DataDir 'training-runtime/venv/Scripts/python.exe')
            New-FakeProcess -Id 40 -Name 'python' -Path (Join-Path $script:Elsewhere 'Python312/python.exe')
            New-FakeProcess -Id 41 -Name 'llama-server' -Path (Join-Path "$($script:DataDir)-sibling" 'llama-server.exe')
        }

        $ids = @(Get-XEUninstallTarget -DataDirectory $script:DataDir -InstallDirectory @($script:InstallDir) | ForEach-Object { $_.Id })

        $ids | Should -Be @(10, 11, 20, 21, 30, 31)
    }

    It 'returns an empty list when nothing runs' {
        Mock Get-Process -MockWith { }

        @(Get-XEUninstallTarget -DataDirectory $script:DataDir).Count | Should -Be 0
    }
}
