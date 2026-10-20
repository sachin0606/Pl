param(
    [string]$VariableName = "OPENWEATHER_API_KEY"
)

$ErrorActionPreference = "Stop"
Write-Host "Kashtrix OpenWeather credential setup" -ForegroundColor Cyan
Write-Host "The key is stored in your Windows USER environment, not in the project or CG demo files."
$secure = Read-Host "Enter OpenWeather API key" -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    if ([string]::IsNullOrWhiteSpace($plain)) { throw "API key cannot be empty." }
    [Environment]::SetEnvironmentVariable($VariableName, $plain, "User")
    Set-Item -Path ("Env:" + $VariableName) -Value $plain
    Write-Host "$VariableName configured for this user. Restart Kashtrix Playout/CG Editor if it is already open." -ForegroundColor Green
}
finally {
    if ($ptr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    $plain = $null
}
