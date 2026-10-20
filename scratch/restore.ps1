Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path (Get-Location) "Kashtrix-FIX49H.zip"
$destPath = Get-Location
Write-Host "Opening $zipPath"
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$count = 0
foreach ($entry in $zip.Entries) {
    if ($entry.FullName.StartsWith("src/BroadcastPlayout.App/") -or $entry.FullName.StartsWith("src\BroadcastPlayout.App\")) {
        $targetFile = Join-Path $destPath $entry.FullName
        $targetDir = [System.IO.Path]::GetDirectoryName($targetFile)
        if (-not (Test-Path $targetDir)) {
            [System.IO.Directory]::CreateDirectory($targetDir) | Out-Null
        }
        if (-not $entry.FullName.EndsWith("/") -and -not $entry.FullName.EndsWith("\")) {
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $targetFile, $true)
            $count++
        }
    }
}
$zip.Dispose()
Write-Host "Extracted $count files for BroadcastPlayout.App"
