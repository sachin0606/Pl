Select-String -Path 'tools\Verify-And-Build.ps1' -Pattern "throw 'FIX" | ForEach-Object {
    Write-Host "$($_.LineNumber): $($_.Line.Trim())"
}
