#Requires -Modules Pester

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $scriptPath = Join-Path $script:RepoRoot 'publish/windows/uninstall-xe-local-ai-engine.ps1'
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
    if (@($parseErrors | Where-Object { $_ }).Count -gt 0) { throw "uninstall-xe-local-ai-engine.ps1 does not parse:`n$($parseErrors -join "`n")" }

    $script:UninstallScript = $scriptPath
    foreach ($name in @('Test-UnderDirectory', 'Get-XEUninstallTarget', 'Confirm-XEDataRemoval', 'Resolve-XEPhysicalPath', 'Get-XERemovalBlocker')) {
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

Describe 'Confirm-XEDataRemoval' {
    BeforeEach {
        $script:Folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:Folder | Out-Null
        Mock Write-Host { }
    }

    # Each row: -Yes, node.key present, the replies in order, expected decision, prompts asked.
    It 'yes=<Yes> key=<Key> replies=<Replies> -> <Expected> after <Prompts> prompt(s)' -ForEach @(
        @{ Yes = $false; Key = $false; Replies = @('n'); Expected = $false; Prompts = 1 }
        @{ Yes = $false; Key = $false; Replies = @('y'); Expected = $true; Prompts = 1 }
        @{ Yes = $false; Key = $true; Replies = @('y', 'y'); Expected = $false; Prompts = 2 }
        @{ Yes = $false; Key = $true; Replies = @('y', 'delete key'); Expected = $true; Prompts = 2 }
        @{ Yes = $true; Key = $false; Replies = @(); Expected = $true; Prompts = 0 }
        @{ Yes = $true; Key = $true; Replies = @(''); Expected = $false; Prompts = 1 }
        @{ Yes = $true; Key = $true; Replies = @('delete key'); Expected = $true; Prompts = 1 }
    ) {
        if ($Key) { Set-Content -LiteralPath (Join-Path $script:Folder 'node.key') -Value 'key' }
        $script:Queue = [System.Collections.Generic.Queue[string]]::new([string[]]$Replies)
        Mock Read-Host { $script:Queue.Dequeue() }

        Confirm-XEDataRemoval -DataDirectory $script:Folder -Yes:$Yes | Should -Be $Expected
        Should -Invoke Read-Host -Exactly -Times $Prompts
        Test-Path -LiteralPath $script:Folder | Should -BeTrue -Because 'the decision itself never deletes'
    }
}

Describe 'Get-XERemovalBlocker' {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:Data = Join-Path $script:Root 'data'
        New-Item -ItemType Directory -Path $script:Data | Out-Null
    }

    It 'allows a plain data folder with the app outside it' {
        Get-XERemovalBlocker -DataDirectory $script:Data -InstallDirectory (Join-Path $script:Root 'app/current') | Should -BeNullOrEmpty
    }

    It 'refuses a malformed root: <Case>' -ForEach @(
        @{ Case = 'empty'; Path = '' }
        @{ Case = 'relative'; Path = 'XE-Local-AI-Engine' }
        @{ Case = 'drive root'; Path = [IO.Path]::GetPathRoot([IO.Path]::GetTempPath()) }
    ) {
        Get-XERemovalBlocker -DataDirectory $Path -InstallDirectory $null | Should -Not -BeNullOrEmpty
    }

    It 'refuses when the app folder lies inside the data folder' {
        Get-XERemovalBlocker -DataDirectory $script:Data -InstallDirectory (Join-Path $script:Data 'portable/current') |
            Should -Match 'inside the data folder'
    }

    It 'refuses when the app folder reaches into the data folder through a link' {
        $link = Join-Path $script:Root 'apps'
        New-Item -ItemType SymbolicLink -Path $link -Target $script:Data | Out-Null
        Get-XERemovalBlocker -DataDirectory $script:Data -InstallDirectory (Join-Path $link 'portable/current') |
            Should -Match 'inside the data folder'
    }

    It 'refuses a symbolic-link root' {
        $link = Join-Path $script:Root 'linked-data'
        New-Item -ItemType SymbolicLink -Path $link -Target $script:Data | Out-Null
        Get-XERemovalBlocker -DataDirectory $link -InstallDirectory $null | Should -Match 'is a link'
    }

    It 'refuses a junction root' -Skip:(-not $IsWindows) {
        # Junctions exist only on NTFS; the symbolic-link case above covers the same check elsewhere.
        $link = Join-Path $script:Root 'junction-data'
        New-Item -ItemType Junction -Path $link -Target $script:Data | Out-Null
        Get-XERemovalBlocker -DataDirectory $link -InstallDirectory $null | Should -Match 'is a link'
    }
}

# Runs the real script against a data folder redirected into TestDrive. Only Linux lets LocalApplicationData be
# redirected (XDG_DATA_HOME); on Windows these would aim at the real %LOCALAPPDATA%, so they are skipped there.
Describe 'uninstall script end to end' -Skip:(-not $IsLinux) {
    BeforeEach {
        $script:Root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:SavedXdg = $env:XDG_DATA_HOME
        $env:XDG_DATA_HOME = Join-Path $script:Root 'xdg'
        # GetFolderPath returns an empty string for a folder that does not exist yet.
        New-Item -ItemType Directory -Path $env:XDG_DATA_HOME | Out-Null
        $local = [Environment]::GetFolderPath('LocalApplicationData')
        if ($local -ne $env:XDG_DATA_HOME) { throw "LocalApplicationData resolved to '$local', not the test folder." }
        $script:Data = Join-Path $local 'XE-Local-AI-Engine'
        New-Item -ItemType Directory -Path $script:Data | Out-Null
        Set-Content -LiteralPath (Join-Path $script:Data 'node.sqlite') -Value 'db'
        $script:Outside = Join-Path $script:Root 'outside.txt'
        Set-Content -LiteralPath $script:Outside -Value 'keep'
        $script:App = Join-Path $script:Root 'app/current'
        New-Item -ItemType Directory -Path $script:App | Out-Null
        Copy-Item -LiteralPath $script:UninstallScript -Destination $script:App
        $script:Run = Join-Path $script:App 'uninstall-xe-local-ai-engine.ps1'
        Mock Write-Host { }
        Mock Start-Sleep { }
        Mock Get-Process { }
        Mock Read-Host { 'n' }
        # Every run must set its own exit code; a stale one from an earlier test would pass silently.
        $global:LASTEXITCODE = -1
    }

    AfterEach {
        $env:XDG_DATA_HOME = $script:SavedXdg
    }

    It 'deletes the data folder after -Yes when it holds no node.key, and nothing outside it' {
        & $script:Run -Yes -AllowAdmin
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath $script:Data | Should -BeFalse
        Test-Path -LiteralPath $script:Outside | Should -BeTrue
        Test-Path -LiteralPath $script:Run | Should -BeTrue
    }

    It 'keeps a portable install''s data folder under -Yes -KeepData without asking' {
        & $script:Run -Yes -KeepData -AllowAdmin
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath (Join-Path $script:Data 'node.sqlite') | Should -BeTrue
        Should -Invoke Read-Host -Exactly -Times 0
    }

    It 'keeps a data folder holding node.key under -Yes until "delete key" is typed' {
        Set-Content -LiteralPath (Join-Path $script:Data 'node.key') -Value 'key'
        & $script:Run -Yes -AllowAdmin
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath (Join-Path $script:Data 'node.key') | Should -BeTrue
        Should -Invoke Read-Host -Exactly -Times 1
    }

    It 'never runs a managed install''s uninstaller and deletes nothing (<Flags>)' -ForEach @(
        @{ Flags = '-Yes'; Arguments = @{ Yes = $true } }
        @{ Flags = '-Yes -KeepData'; Arguments = @{ Yes = $true; KeepData = $true } }
        @{ Flags = 'interactive'; Arguments = @{} }
    ) {
        $managed = Join-Path $env:XDG_DATA_HOME 'XE-Local-AI-Engine-app'
        New-Item -ItemType Directory -Path $managed | Out-Null
        $marker = Join-Path $script:Root 'uninstaller-ran'
        $updateExe = Join-Path $managed 'Update.exe'
        Set-Content -LiteralPath $updateExe -Value "#!/bin/sh`ntouch '$marker'"
        [IO.File]::SetUnixFileMode($updateExe, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute')

        & $script:Run @Arguments -AllowAdmin
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath $marker | Should -BeFalse -Because 'Update.exe --uninstall must never be invoked'
        Test-Path -LiteralPath (Join-Path $script:Data 'node.sqlite') | Should -BeTrue
        Should -Invoke Read-Host -Exactly -Times 0
    }

    It 'treats a data folder that is itself a managed root as managed and deletes nothing' {
        New-Item -ItemType Directory -Path (Join-Path $script:Data 'current') | Out-Null
        & $script:Run -Yes -AllowAdmin
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath (Join-Path $script:Data 'node.sqlite') | Should -BeTrue
    }

    It 'refuses when the app was extracted inside the data folder' {
        $nested = Join-Path $script:Data 'portable/current'
        New-Item -ItemType Directory -Path $nested | Out-Null
        Copy-Item -LiteralPath $script:UninstallScript -Destination $nested
        & (Join-Path $nested 'uninstall-xe-local-ai-engine.ps1') -Yes -AllowAdmin
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath (Join-Path $script:Data 'node.sqlite') | Should -BeTrue
        Test-Path -LiteralPath $script:Outside | Should -BeTrue
    }

    It 'refuses a data folder that is a symbolic link and leaves its target alone' {
        $real = Join-Path $script:Root 'real-data'
        Move-Item -LiteralPath $script:Data -Destination $real
        New-Item -ItemType SymbolicLink -Path $script:Data -Target $real | Out-Null
        & $script:Run -Yes -AllowAdmin
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath (Join-Path $real 'node.sqlite') | Should -BeTrue
    }

    It 'reports a process it could not stop and deletes nothing' {
        $client = Join-Path $script:Data 'current/XE-Local-AI-Engine.Client'
        Mock Get-Process -ParameterFilter { $Name -contains 'XE-Local-AI-Engine.Client' } -MockWith {
            [pscustomobject]@{ Id = 4242; Name = 'XE-Local-AI-Engine.Client'; Path = $client }
        }
        Mock Stop-Process { throw 'Access is denied.' }
        & $script:Run -Yes -AllowAdmin
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath (Join-Path $script:Data 'node.sqlite') | Should -BeTrue
        Should -Invoke Write-Host -ParameterFilter { "$Object" -like '*Could not stop: pid 4242*' }
        Should -Invoke Write-Host -Exactly -Times 0 -ParameterFilter { "$Object" -like '*Processes stopped*' }
    }
}
