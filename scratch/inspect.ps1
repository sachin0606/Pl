$p1 = Get-Content 'scratch\prime_hd.json' -Raw | ConvertFrom-Json
Write-Host '=== PRIME HD ==='
Write-Host ("Name: $($p1.Name) | Duration: $($p1.DurationSeconds) | FrameRate: $($p1.FrameRate)")
Write-Host ("DataSources: " + ($p1.DataSources | ForEach-Object { "$($_.Name) ($($_.SourceType) - $($_.Source))" } -join '; '))
Write-Host 'Layers:'
foreach ($l in $p1.Layers) {
    Write-Host (" - $($l.Name) [$($l.Type)] X=$($l.X) Y=$($l.Y) W=$($l.Width) H=$($l.Height) Start=$($l.StartSeconds) End=$($l.EndSeconds) In=$($l.AnimationIn) Out=$($l.AnimationOut) DS=$($l.DataSourceId) Field=$($l.DataField) DurationPerItem=$($l.DataItemDurationSeconds) Offset=$($l.DataItemOffset) Speed=$($l.Speed)")
}

$p2 = Get-Content 'scratch\highlights_4line.json' -Raw | ConvertFrom-Json
Write-Host '=== HIGHLIGHTS 4-LINE ==='
Write-Host ("Name: $($p2.Name) | Duration: $($p2.DurationSeconds) | FrameRate: $($p2.FrameRate)")
Write-Host ("DataSources: " + ($p2.DataSources | ForEach-Object { "$($_.Name) ($($_.SourceType) - $($_.Source))" } -join '; '))
Write-Host 'Layers:'
foreach ($l in $p2.Layers) {
    Write-Host (" - $($l.Name) [$($l.Type)] X=$($l.X) Y=$($l.Y) W=$($l.Width) H=$($l.Height) Start=$($l.StartSeconds) End=$($l.EndSeconds) In=$($l.AnimationIn) Out=$($l.AnimationOut) DS=$($l.DataSourceId) Field=$($l.DataField) DurationPerItem=$($l.DataItemDurationSeconds) Offset=$($l.DataItemOffset) Speed=$($l.Speed)")
}
