$p1 = Get-Content 'scratch\prime_hd.json' -Raw | ConvertFrom-Json
Write-Host '=== PRIME HD DATASOURCES ==='
$p1.DataSources | ConvertTo-Json -Depth 5

$p2 = Get-Content 'scratch\highlights_4line.json' -Raw | ConvertFrom-Json
Write-Host '=== HIGHLIGHTS DATASOURCES ==='
$p2.DataSources | ConvertTo-Json -Depth 5
