param(
    [ValidateSet('Safe','IP','HA-Primary','HA-Backup','All')]
    [string]$Profile='Safe',
    [string]$SettingsPath="$env:APPDATA\KashtrixPlayout\settings.json"
)
$ErrorActionPreference='Stop'
$dir=Split-Path $SettingsPath -Parent; New-Item $dir -ItemType Directory -Force | Out-Null
if(Test-Path $SettingsPath){ $s=Get-Content $SettingsPath -Raw | ConvertFrom-Json } else { $s=[pscustomobject]@{} }
if(-not $s.PSObject.Properties['ProfessionalBroadcast']){$s|Add-Member NoteProperty ProfessionalBroadcast ([pscustomobject]@{})}
$p=$s.ProfessionalBroadcast
function Set-P($name,$value){ if($p.PSObject.Properties[$name]){$p.$name=$value}else{$p|Add-Member NoteProperty $name $value} }
Set-P OutputArmed $true
Set-P EnableSt2110 ($Profile -in @('IP','All'))
Set-P EnableSt2022_7 ($Profile -in @('IP','All'))
Set-P EnablePtpMonitor ($Profile -in @('IP','All'))
Set-P EnableNmos ($Profile -in @('IP','All'))
Set-P EnableScte35Ts ($Profile -in @('IP','All'))
Set-P EnableScte104Anc $true
Set-P EnableCea608 $true
Set-P EnableCea708 $true
Set-P EnableDvbSubtitles ($Profile -eq 'All')
Set-P EnableGpio ($Profile -eq 'All')
Set-P EnableRs422 ($Profile -eq 'All')
Set-P EnableVdcp ($Profile -eq 'All')
Set-P EnableSnmp ($Profile -eq 'All')
Set-P HaMode ($(if($Profile -eq 'HA-Primary'){'Primary'}elseif($Profile -eq 'HA-Backup'){'Backup'}else{'Standalone'}))
Set-P HaPriority ($(if($Profile -eq 'HA-Primary'){200}elseif($Profile -eq 'HA-Backup'){100}else{100}))
Set-P HaNodeId $env:COMPUTERNAME
Set-P St2110VideoDestinationA '239.100.20.1'; Set-P St2110VideoDestinationB '239.100.20.2'; Set-P St2110VideoPort 50020
Set-P St2110AudioDestinationA '239.100.30.1'; Set-P St2110AudioDestinationB '239.100.30.2'; Set-P St2110AudioPort 50030
Set-P St2110AncDestinationA '239.100.40.1'; Set-P St2110AncDestinationB '239.100.40.2'; Set-P St2110AncPort 50040
Set-P NmosHttpPort 3210; Set-P Scte35TsDestination '239.100.35.1'; Set-P Scte35TsPort 50350
Set-P Scte35Pid 7936; Set-P Scte35PmtPid 4096; Set-P DvbSubtitlePid 4608
Set-P GpioComPort 'COM3'; Set-P Rs422ComPort 'COM4'; Set-P Rs422Baud 38400
Set-P SnmpAgentPort 1161; Set-P SnmpReadCommunity 'public'; Set-P SnmpTrapHost '127.0.0.1'; Set-P SnmpTrapPort 162; Set-P SnmpTrapCommunity 'public'
Set-P DolbyMode 'Passthrough'
$s | ConvertTo-Json -Depth 20 | Set-Content $SettingsPath -Encoding UTF8
Write-Host "FIX42 test profile '$Profile' written to $SettingsPath" -ForegroundColor Green
Write-Host 'Review multicast IPs, COM ports, SNMP communities and HA priorities before starting Playout.' -ForegroundColor Yellow
