BeforeAll {
    $script:CaptureScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'capture_windows_vram.ps1')).Path
    $script:SchemaPath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..' 'docs' 'performance' 'schemas' 'windows-vram-evidence.schema.json')).Path
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:CaptureScriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Capture script has parse errors: $($parseErrors -join '; ')"
    }

    foreach ($functionName in @('Invoke-NativeCapture', 'Protect-CaptureText', 'ConvertFrom-NvidiaGlobalOutput')) {
        $functionAst = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
            }, $true)
        if ($null -eq $functionAst) {
            throw "$functionName was not found in capture_windows_vram.ps1."
        }

        $definition = [ScriptBlock]::Create($functionAst.Extent.Text)
        . $definition
    }
}

Describe 'capture_windows_vram privacy contract' {
    It 'does not emit machine names, absolute server paths, or GPU UUIDs' {
        $scriptText = Get-Content -Raw -LiteralPath $script:CaptureScriptPath
        $schema = Get-Content -Raw -LiteralPath $script:SchemaPath | ConvertFrom-Json -AsHashtable

        $scriptText | Should -Not -Match 'machine_name'
        $scriptText | Should -Not -Match '(?i)query-gpu=[^\r\n]*uuid'
        $schema.properties.host.required | Should -Not -Contain 'machine_name'
        $schema.properties.llama_server.required | Should -Contain 'file_name'
        $schema.properties.llama_server.required | Should -Not -Contain 'path'
        $schema.properties.samples.items.properties.global_vram.items.required | Should -Not -Contain 'uuid'
        $schema.properties.samples.items.properties.global_vram.minItems | Should -Be 1
        $schema.properties.samples.items.additionalProperties | Should -BeFalse
        $schema.properties.samples.items.properties.global_vram.items.additionalProperties | Should -BeFalse
        $schema.properties.samples.items.properties.global_vram.items.properties.free_mib.type | Should -Be 'integer'
        $schema.properties.samples.items.properties.process_budget_probe.additionalProperties | Should -BeFalse
        $scriptText | Should -Not -Match 'is external pressure/WDDM divergence'
        $scriptText | Should -Match 'ambient baseline'
        $scriptText | Should -Not -Match '\.WaitForExit\(\)'
    }

    It 'redacts configured and Windows user-profile paths from raw output' {
        $protected = Protect-CaptureText -Output @(
            '123, C:\Users\sam\AppData\Local\game.exe, 64',
            'probe=C:\llama\llama-server.exe'
        ) -SensitiveValues @('C:\llama\llama-server.exe')

        $protected | Should -Not -Match '(?i)C:\\Users\\sam'
        $protected | Should -Not -Match '(?i)C:\\llama'
        $protected | Should -Match '<redacted-user-path>'
        $protected | Should -Match '<redacted-path>'
    }

    It 'redacts canonical GPU and both MIG UUID forms' {
        $protected = Protect-CaptureText -Output @(
            'GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5',
            'MIG-GPU-d753e8bb-b687-daf2-f54f-79c1ed60cae5/7/3',
            'MIG-3f3f2b11-0f24-4f10-a2d8-65d7bd9a4c99'
        )

        $protected | Should -Not -Match '(?i)(?:GPU|MIG)-[0-9a-f]'
        ([regex]::Matches($protected, '<redacted-gpu-uuid>')).Count | Should -Be 3
    }
}

Describe 'ConvertFrom-NvidiaGlobalOutput' {
    It 'parses one or more UUID-free GPU rows' {
        $rows = @(ConvertFrom-NvidiaGlobalOutput -Output @(
            '0, NVIDIA GPU, 610.74, 32607, 28427, 3761, 5',
            '1, NVIDIA GPU 2, 610.74, 24576, 20000, 4576, 2'
        ))

        $rows.Count | Should -Be 2
        $rows[0].free_mib | Should -Be 28427
        $rows[1].used_mib | Should -Be 4576
    }

    It 'fails closed with sanitized diagnostic output when no GPU row parses' {
        {
            ConvertFrom-NvidiaGlobalOutput -Output @(
                'unexpected output C:\Users\sam\private\probe.txt'
            )
        } | Should -Throw -ExpectedMessage '*<redacted-user-path>*'
    }

    It 'fails closed when any non-empty row in a multi-GPU response is malformed' {
        {
            ConvertFrom-NvidiaGlobalOutput -Output @(
                '0, NVIDIA GPU, 610.74, 32607, 28427, 3761, 5',
                '1, malformed'
            )
        } | Should -Throw -ExpectedMessage '*1, malformed*'
    }
}

Describe 'Invoke-NativeCapture' {
    It 'preserves native output as individual lines' {
        $result = Invoke-NativeCapture -FilePath '/bin/sh' -ArgumentList @(
            '-c',
            'printf "first\nsecond\n"'
        )

        $result.Output.Count | Should -Be 2
        $result.Output[0] | Should -Be 'first'
        $result.Output[1] | Should -Be 'second'
    }

    It 'preserves output and exit code when native errors are terminating' {
        $priorPreference = $global:PSNativeCommandUseErrorActionPreference
        try {
            $global:PSNativeCommandUseErrorActionPreference = $true
            $ErrorActionPreference = 'Stop'

            $result = Invoke-NativeCapture -FilePath '/bin/sh' -ArgumentList @(
                '-c',
                'printf native-failure-output >&2; exit 23'
            )

            $result.ExitCode | Should -Be 23
            ($result.Output -join [Environment]::NewLine) | Should -Match 'native-failure-output'
        }
        finally {
            $global:PSNativeCommandUseErrorActionPreference = $priorPreference
        }
    }

    It 'times out and terminates the native process tree' {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

        $result = Invoke-NativeCapture -FilePath '/bin/sh' -ArgumentList @(
            '-c',
            'sleep 30 & child=$!; printf "%s\n" "$child"; wait'
        ) -TimeoutMilliseconds 200

        $stopwatch.Stop()
        $result.TimedOut | Should -BeTrue
        $stopwatch.Elapsed | Should -BeLessThan ([TimeSpan]::FromSeconds(5))
        if (Test-Path -LiteralPath '/proc') {
            $childProcessId = [int]$result.Output[0]
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
            while ((Test-Path -LiteralPath "/proc/$childProcessId") -and [DateTimeOffset]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 20
            }
            Test-Path -LiteralPath "/proc/$childProcessId" | Should -BeFalse
        }
    }
}
