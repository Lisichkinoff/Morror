param([switch]$BuildTools)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path $PSScriptRoot
$downloadRoot = Join-Path $projectRoot 'downloads\native-monitor'
$toolRoot = Join-Path $projectRoot 'tools\native-monitor'
New-Item -ItemType Directory -Force -Path $downloadRoot,$toolRoot | Out-Null
function Get-VerifiedArchive([string]$Name, [string]$Url, [string]$Hash, [string]$Algorithm = 'SHA256') {
    $archivePath = Join-Path $downloadRoot $Name
    if (!(Test-Path -LiteralPath $archivePath)) { Invoke-WebRequest -Uri $Url -OutFile $archivePath }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm $Algorithm).Hash -ne $Hash) { throw ('Неверная контрольная сумма: ' + $Name) }
    Write-Host ('Verified ' + $Name)
    return $archivePath
}
$ffmpeg = Get-VerifiedArchive 'ffmpeg-n8.1-win64-lgpl.zip' 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-09-12-42/ffmpeg-n8.1.3-34-ga35157abb9-win64-lgpl-8.1.zip' '7BDEC3860D33145A7CD68146EB8E1414E8881CAC76205401211FBDAD38EF0ED3'
if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'ffmpeg'))) {
    Expand-Archive -LiteralPath $ffmpeg -DestinationPath (Join-Path $toolRoot 'ffmpeg')
}
$driver = Get-VerifiedArchive 'VirtualDisplayDriver-25.7.23.zip' 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip' 'E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A'
if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'driver'))) { Expand-Archive -LiteralPath $driver -DestinationPath (Join-Path $toolRoot 'driver') }
if ($BuildTools) {
    $jdk = Get-VerifiedArchive 'jdk17.zip' 'https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.21%2B9/OpenJDK17U-jdk_x64_windows_hotspot_17.0.21_9.zip' '98518ECF554465057EE5509462631DEEEE374BC368C12A411D225CD29381F84B'
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'jdk'))) { Expand-Archive -LiteralPath $jdk -DestinationPath (Join-Path $toolRoot 'jdk') }
    $platform = Get-VerifiedArchive 'platform35.zip' 'https://dl.google.com/android/repository/platform-35_r02.zip' '0988CACAD01B38A18A47BAC14A0695F246BC76C1B06C0EEB8EB0DC825AB0C8E0'
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'platform'))) { Expand-Archive -LiteralPath $platform -DestinationPath (Join-Path $toolRoot 'platform') }
    $androidTools = Get-VerifiedArchive 'build-tools35.zip' 'https://dl.google.com/android/repository/build-tools_r35_windows.zip' '5753C679A1B90BCF6FBC9945A2CE39DFB9E74F1DF0831A1E1866AE5B594326F0'
    if (!(Test-Path -LiteralPath (Join-Path $toolRoot 'android-tools'))) { Expand-Archive -LiteralPath $androidTools -DestinationPath (Join-Path $toolRoot 'android-tools') }
}
Write-Host 'Native monitor dependencies are ready.'
