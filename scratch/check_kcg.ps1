$file = (Get-ChildItem -Path ".\cg-demo" -Filter "*Prime*Breaking News Live*.kcg").FullName
$lines = Get-Content $file
$json = ($lines[1..($lines.Length-1)]) -join "`r`n"
$obj = $json | ConvertFrom-Json
$obj.Layers | Select-Object Name, Type, Source, StartSeconds, EndSeconds, SequenceFps, SequenceStartFrame, SequenceEndFrame, SequenceLoop, SequenceHoldLastFrame, SequenceAdvanceDataItem | Format-List
