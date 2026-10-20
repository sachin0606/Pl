param(
  [Parameter(Mandatory=$true)][string]$SdkRoot,
  [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..")
)
$ErrorActionPreference='Stop'
$idl=Get-ChildItem $SdkRoot -Filter DeckLinkAPI.idl -Recurse | Select-Object -First 1
if (-not $idl) { throw 'DeckLinkAPI.idl not found. Point -SdkRoot to Blackmagic Desktop Video SDK 16.0.' }
$vswhere="$env:ProgramFiles(x86)\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio 2026 with Desktop development with C++ and Windows SDK.' }
$vs=& $vswhere -latest -products * -property installationPath
$devcmd=Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path $devcmd)) { throw "VsDevCmd.bat not found: $devcmd" }
$out=Join-Path $ProjectRoot '.decklink-build'; New-Item $out -ItemType Directory -Force | Out-Null
$cmd="`"$devcmd`" -arch=x64 -host_arch=x64 && cd /d `"$out`" && midl /env x64 /tlb DeckLinkAPI64.tlb `"$($idl.FullName)`" && tlbimp DeckLinkAPI64.tlb /out:Interop.DeckLinkAPI64.dll /namespace:DeckLinkAPI /machine:X64"
cmd.exe /c $cmd
$dll=Join-Path $out 'Interop.DeckLinkAPI64.dll'; if (-not (Test-Path $dll)) { throw 'Interop generation did not produce the DLL.' }
$dest=Join-Path $ProjectRoot 'src\BroadcastPlayout.App\vendor\decklink'; New-Item $dest -ItemType Directory -Force | Out-Null
Copy-Item $dll $dest -Force
Write-Host "DeckLink interop generated. Clean/Rebuild solution so DECKLINK_SDK is defined." -ForegroundColor Green
Write-Host "Important: Blackmagic recommends MTA for DeckLink API calls. For production, move hardware output to a dedicated MTA worker thread and use scheduled playback callbacks." -ForegroundColor Yellow
