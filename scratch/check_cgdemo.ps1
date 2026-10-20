Add-Type -AssemblyName System.Drawing
Get-ChildItem -Path "cgdemo-templates" -Recurse -Filter "*.png" -ErrorAction SilentlyContinue |
    Group-Object { $_.Directory.FullName } | ForEach-Object {
        $first = $_.Group[0]
        $img = [System.Drawing.Image]::FromFile($first.FullName)
        Write-Host "$($first.Directory.FullName) ($($_.Count) files) = $($img.Width)x$($img.Height)"
        $img.Dispose()
    }
