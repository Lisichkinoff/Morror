param([string]$Archive)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = '1.8.3'
$asset = "iPhoneMirror-v$version-win-x64"
$expected = '97C5DED39A9437069CB24FD4EE59FE9775C1EBCF2700E3FD6305C9E424396BF0'
if (!$Archive) {
    $downloads = Join-Path $root 'downloads'
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $Archive = Join-Path $downloads "$asset.zip"
    if (!(Test-Path -LiteralPath $Archive) -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $expected) {
        $url = "https://github.com/RayrenSX/iPhoneMirror/releases/download/v$version/$asset.zip"
        $partial = "$Archive.part"
        & curl.exe --fail --location --retry 2 --connect-timeout 20 --max-time 300 --output $partial $url
        if ($LASTEXITCODE -ne 0) { throw "Загрузка прервалась. Можно скачать архив и передать его через -Archive. $url" }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $expected) { throw 'SHA-256 архива iPhoneMirror не совпадает.' }
        Move-Item -LiteralPath $partial -Destination $Archive -Force
    }
}
$Archive = (Resolve-Path -LiteralPath $Archive).Path
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $expected) { throw 'SHA-256 архива iPhoneMirror не совпадает.' }
$destination = Join-Path $root "tools\iphone-mirror-$version"
$files = @(
    'iPhoneMirror.Core.dll', 'iPhoneMirror.UsbConfigurationSwitch.exe', 'iPhoneMirror.Driver.exe',
    'libusb-1.0.dll', 'libusb0.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
    'LICENSE', 'THIRD_PARTY_NOTICES.md', 'DRIVER_DEPENDENCIES.md',
    'licenses/libusb-COPYING.txt', 'licenses/libusb-win32-COPYING-LGPL.txt',
    'licenses/WPF-UI-LICENSE.md', 'licenses/WPF-UI-ThirdPartyNotices.txt',
    'Wireless/iPhoneMirror.WirelessHost.exe', 'Wireless/airplay2dll.dll', 'Wireless/avcodec-58.dll',
    'Wireless/avutil-56.dll', 'Wireless/dnssd.dll', 'Wireless/swresample-3.dll', 'Wireless/swscale-5.dll',
    'Wireless/msvcp140.dll', 'Wireless/vcruntime140.dll', 'Wireless/vcruntime140_1.dll',
    'Wireless/licenses/LICENSE-FFMPEG-LGPL-2.1.txt', 'Wireless/licenses/LICENSE-MIT.txt',
    'Wireless/licenses/LICENSE-PLAYFAIR-GPL-3.0.md', 'Wireless/licenses/NOTICE-FDK-AAC.txt',
    'Wireless/licenses/SHA256SUMS.txt', 'Wireless/licenses/SOURCE.md'
)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    foreach ($file in $files) {
        $entry = $zip.GetEntry("$asset/$file")
        if (!$entry) { throw "В архиве отсутствует $file." }
        $target = Join-Path $destination ($file.Replace('/', '\'))
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $zip.Dispose() }
Write-Host "Компонент iPhone готов: $destination"
