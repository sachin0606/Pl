$p3 = Get-Content 'scratch\category_ticker.json' -Raw | ConvertFrom-Json
Write-Host '=== CATEGORY TICKER ==='
Write-Host ("Name: $($p3.Name) | Duration: $($p3.DurationSeconds) | FrameRate: $($p3.FrameRate) | Loop: $($p3.Loop)")
Write-Host 'DataSources:'
$p3.DataSources | ForEach-Object { Write-Host (" - $($_.Name) [$($_.SourceType) / $($_.Source)]") }
Write-Host 'Layers:'
foreach ($l in $p3.Layers) {
    Write-Host (" - $($l.Name) [$($l.Type)] X=$($l.X) Y=$($l.Y) W=$($l.Width) H=$($l.Height) Start=$($l.StartSeconds) End=$($l.EndSeconds) In=$($l.AnimationIn) Out=$($l.AnimationOut) DS=$($l.DataSourceId) Field=$($l.DataField) ItemDur=$($l.DataItemDurationSeconds) TickerSpeed=$($l.TickerSpeed) CatBadgeBg=$($l.TickerCategoryBadgeBackground) CatEnabled=$($l.TickerCategoriesEnabled)")
}
