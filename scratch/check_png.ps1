Add-Type -AssemblyName System.Drawing
Get-ChildItem -Path "src\BroadcastPlayout.App\demos", "demos", "cg-demo" -Recurse -Filter "*.png" -ErrorAction SilentlyContinue | 
    Group-Object { $_.Directory.FullName } | ForEach-Object {
        $first = $_.Group[0]
        $img = [System.Drawing.Image]::FromFile($first.FullName)
        Write-Host "$($first.Directory.Name)\$($first.Name) ($($_.Count) files) = $($img.Width)x$($img.Height)"
        $img.Dispose()
    }
