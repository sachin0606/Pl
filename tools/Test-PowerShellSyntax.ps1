param(
    [string]$ToolsRoot = $PSScriptRoot,
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
$failures = @()
$files = Get-ChildItem -Path $ToolsRoot -Filter '*.ps1' -File
if ($BuildOnly) {
    $diagnosticNames = @('Live-Debug.ps1','Debug-Outputs.ps1','Test-NDI-Sender.ps1','Test-Chromium-CG.ps1')
    $files = @($files | Where-Object { $diagnosticNames -notcontains $_.Name })
}
$files | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)

    # Windows PowerShell 5.1 can parse a UTF-8 BOM. Ignore only that leading
    # marker while keeping the remainder of every build tool ASCII-safe.
    $start = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) {
        $start = 3
    }

    $nonAscii = $null
    $nonAsciiIndex = -1
    for ($i = $start; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) {
            $nonAscii = $bytes[$i]
            $nonAsciiIndex = $i
            break
        }
    }
    if ($null -ne $nonAscii) {
        $line = 1
        for ($j = $start; $j -lt $nonAsciiIndex; $j++) {
            if ($bytes[$j] -eq 10) { $line++ }
        }
        $failures += ($_.Name + ': contains non-ASCII byte ' + $nonAscii + ' at line ' + $line + ', byte offset ' + $nonAsciiIndex + '; build tool scripts must remain Windows PowerShell 5.1 safe.')
    }

    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        $failures += ($_.Name + ': ' + (($parseErrors | ForEach-Object { $_.Message }) -join ' | '))
    }
}
if ($failures.Count -gt 0) {
    throw ('PowerShell preflight failed: ' + ($failures -join '; '))
}
if ($BuildOnly) {
    Write-Host 'PowerShell 5.1 preflight passed for setup/build scripts. Diagnostic tools are validated separately.' -ForegroundColor Green
} else {
    Write-Host 'PowerShell 5.1 preflight passed for all tool scripts.' -ForegroundColor Green
}
