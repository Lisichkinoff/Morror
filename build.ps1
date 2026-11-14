param([switch]$Test, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
if (!$OutputDirectory) { $OutputDirectory = 'dist\Morror-' + (& (Join-Path $projectRoot 'scripts\get-app-version.ps1')) }
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
if (!(Test-Path -LiteralPath $compiler)) { throw 'Нужен .NET Framework 4.8 (встроен в Windows 10/11).' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$qrLibrary = Join-Path $projectRoot 'tools\qrcoder-1.8.1\lib\net40\QRCoder.dll'
if (!(Test-Path -LiteralPath $qrLibrary)) { throw 'Сначала выполните scripts\get-qr-dependencies.ps1.' }
$references += '/reference:' + $qrLibrary
$sources = @('Core.cs','App.cs','TrayController.cs','WirelessPairing.cs','QrPairingDialog.cs','MirrorWindow.cs','DeviceCapabilities.cs','FileTransfer.cs','ClipboardTransfer.cs','TransferDialog.cs','SecondScreenProtocol.cs','MonitorResolution.cs','ScreenTouchInjection.cs','NativeMonitor.cs','DisplayTopology.cs','VirtualMonitorDriver.cs','WasapiMonitorAudio.cs','AndroidMonitorClient.cs','SecondScreenDialog.cs','AssemblyInfo.cs') | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$sources += 'AppUpdate.cs','MonitorCapture.cs','StreamSettings.cs','MonitorApkDownload.cs','MonitorApkFirewall.cs' | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$resources = @('MainWindow.xaml','QrWindow.xaml','TransferWindow.xaml','SecondScreenWindow.xaml') | ForEach-Object { '/resource:' + (Join-Path $projectRoot ('src\' + $_)) + ',' + $_ }
$icon = Join-Path $projectRoot 'assets\morror.ico'
$mirrorIcon = Join-Path $projectRoot 'assets\morror.png'
if (!(Test-Path -LiteralPath $icon) -or !(Test-Path -LiteralPath $mirrorIcon)) { & (Join-Path $projectRoot 'scripts\build-icon.ps1') }
$iconResource = '/resource:' + $icon + ',Morror.ico'
$resources += $iconResource
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ("/out:" + (Join-Path $output 'Morror.exe')) ("/win32icon:" + $icon) $resources ("/win32manifest:" + (Join-Path $projectRoot 'src\app.manifest')) $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции Morror.' }
$engine = Join-Path $projectRoot 'tools\scrcpy-win64-v5.0.1'
if (!(Test-Path -LiteralPath (Join-Path $engine 'scrcpy.exe'))) { throw 'Сначала выполните scripts\get-scrcpy.ps1.' }
$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
$nativeTools = Join-Path $projectRoot 'tools\native-monitor'
$iphoneSource = Join-Path $projectRoot 'tools\iphone-mirror-1.8.3'
if (!(Test-Path -LiteralPath (Join-Path $iphoneSource 'iPhoneMirror.Core.dll'))) { throw 'Сначала выполните scripts\get-iphone-dependencies.ps1.' }
$iphoneRuntime = Join-Path $runtime 'iphone'
New-Item -ItemType Directory -Path $iphoneRuntime -Force | Out-Null
Copy-Item -Path (Join-Path $iphoneSource '*') -Destination $iphoneRuntime -Recurse -Force
$iphoneSources = @('IPhoneApp.cs','IPhoneMirrorDialog.cs','IPhoneMirrorEngine.cs','AssemblyInfo.cs') | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$iphoneWindow = '/resource:' + (Join-Path $projectRoot 'src\IPhoneWindow.xaml') + ',IPhoneWindow.xaml'
$iphoneTheme = '/resource:' + (Join-Path $projectRoot 'src\MainWindow.xaml') + ',MainWindow.xaml'
& $compiler /nologo /target:winexe /platform:x64 /main:Morror.IPhoneApp /optimize+ /utf8output ("/out:" + (Join-Path $iphoneRuntime 'Morror.iPhone.exe')) ("/win32icon:" + $icon) $iphoneTheme $iphoneWindow $iconResource ("/win32manifest:" + (Join-Path $projectRoot 'src\app.manifest')) $references $iphoneSources
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции компонента iPhone.' }
$nativeApk = Join-Path $nativeTools 'MorrorMonitor.apk'
$nativeEncoder = Get-ChildItem -LiteralPath (Join-Path $nativeTools 'ffmpeg') -Filter ffmpeg.exe -Recurse -File | Select-Object -First 1 -ExpandProperty FullName
if (!(Test-Path -LiteralPath $nativeApk) -or !$nativeEncoder) { throw 'Выполните scripts\get-native-monitor-dependencies.ps1 -BuildTools и scripts\build-android-monitor.ps1.' }
Copy-Item -LiteralPath $nativeApk -Destination $runtime -Force
Copy-Item -LiteralPath $nativeEncoder -Destination (Join-Path $runtime 'ffmpeg.exe') -Force
$driverRuntime = Join-Path $runtime 'monitor-driver'
New-Item -ItemType Directory -Force -Path $driverRuntime | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeTools 'driver\VirtualDisplayDriver\MttVDD.inf'),(Join-Path $nativeTools 'driver\VirtualDisplayDriver\MttVDD.dll'),(Join-Path $nativeTools 'driver\VirtualDisplayDriver\mttvdd.cat'),(Join-Path $nativeTools 'driver\VirtualDisplayDriver\vdd_settings.xml') -Destination $driverRuntime -Force
Get-ChildItem -LiteralPath $engine -File | Where-Object {
    $_.Name -notin @('scrcpy.exe','scrcpy.png') -and ($_.Extension -in @('.exe','.dll','.txt','.png') -or $_.Name -eq 'scrcpy-server')
} | ForEach-Object {
    $destination = Join-Path $runtime $_.Name
    # ADB may be serving this or another app: do not overwrite identical running components.
    if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
# scrcpy loads its window icon separately from the Morror executable.
Copy-Item -LiteralPath $mirrorIcon -Destination (Join-Path $runtime 'scrcpy.png') -Force
$brandingDir = Join-Path (Join-Path $projectRoot 'test-output') ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $brandingDir | Out-Null
try {
    $iconTool = Join-Path $brandingDir 'SetScrcpyIcon.exe'
    $brandedEngine = Join-Path $brandingDir 'scrcpy.exe'
    & $compiler /nologo /target:exe ("/out:" + $iconTool) (Join-Path $projectRoot 'scripts\SetScrcpyIcon.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать замену значка scrcpy.' }
    Copy-Item -LiteralPath (Join-Path $engine 'scrcpy.exe') -Destination $brandedEngine
    & $iconTool $brandedEngine $icon
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось заменить значок scrcpy.' }
    $destination = Join-Path $runtime 'scrcpy.exe'
    if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $brandedEngine).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $brandedEngine -Destination $destination -Force
    }
} finally {
    Remove-Item -LiteralPath (Join-Path $brandingDir 'SetScrcpyIcon.exe'),(Join-Path $brandingDir 'scrcpy.exe') -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $brandingDir -ErrorAction SilentlyContinue
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\second-screen.md') -Destination (Join-Path $output 'SECOND-SCREEN.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $output -Recurse -Force
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\scrcpy-LICENSE.txt') -Destination $licenses -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\VDD-LICENSE.txt'),(Join-Path $projectRoot 'licenses\FFmpeg-LICENSE.txt'),(Join-Path $projectRoot 'licenses\FFmpeg-GPLv3.txt') -Destination $licenses -Force
Copy-Item -LiteralPath $qrLibrary -Destination $output -Force
Copy-Item -LiteralPath $icon -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\qrcoder-1.8.1\LICENSE.txt') -Destination (Join-Path $output 'LICENSE-QRCoder.txt') -Force
if ($Test) {
    $testOutput = Join-Path $projectRoot 'test-output'
    New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
    $testExe = Join-Path $testOutput 'Morror.Tests.exe'
    & $compiler /nologo /target:exe /platform:x64 /utf8output ("/out:" + $testExe) /reference:System.dll /reference:System.Core.dll (Join-Path $projectRoot 'src\Core.cs') (Join-Path $projectRoot 'tests\CoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции проверок.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Проверки завершились с ошибкой.' }
    $deviceTestExe = Join-Path $testOutput 'Morror.DeviceTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /utf8output ("/out:" + $deviceTestExe) /reference:System.dll /reference:System.Core.dll (Join-Path $projectRoot 'src\Core.cs') (Join-Path $projectRoot 'src\DeviceCapabilities.cs') (Join-Path $projectRoot 'tests\DeviceTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции проверок устройств.' }
    & $deviceTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Проверки устройств завершились с ошибкой.' }
    $trayTestExe = Join-Path $testOutput 'Morror.TrayTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /utf8output ("/out:" + $trayTestExe) $iconResource /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $projectRoot 'src\TrayController.cs') (Join-Path $projectRoot 'tests\TrayTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции проверок трея.' }
    & $trayTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Проверки трея завершились с ошибкой.' }
    $fakeRuntime = Join-Path $testOutput 'FakeRuntime'
    New-Item -ItemType Directory -Force -Path $fakeRuntime | Out-Null
    $fakeAdb = Join-Path $fakeRuntime 'adb.exe'
    & $compiler /nologo /target:exe /platform:x64 /utf8output ("/out:" + $fakeAdb) $references (Join-Path $projectRoot 'tests\FakeDevice.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции тестового устройства.' }
    Copy-Item -LiteralPath $fakeAdb -Destination (Join-Path $fakeRuntime 'scrcpy.exe') -Force
    Set-Content -LiteralPath (Join-Path $fakeRuntime 'scrcpy-server') -Value 'Test fixture only'
    $desktopTestExe = Join-Path $testOutput 'Morror.DesktopTests.exe'
    Copy-Item -LiteralPath $qrLibrary -Destination $testOutput -Force
    & $compiler /nologo /target:exe /platform:x64 /main:DesktopTests /utf8output ("/out:" + $desktopTestExe) $resources $references $sources (Join-Path $projectRoot 'tests\DesktopTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции проверок окна.' }
    & $desktopTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Проверки окна завершились с ошибкой.' }
    $decoderLibrary = Join-Path $projectRoot 'tools\zxing-0.16.11\lib\net40\zxing.dll'
    if (!(Test-Path -LiteralPath $decoderLibrary)) { throw 'Для проверок QR выполните scripts\get-qr-dependencies.ps1 -IncludeTests.' }
    Copy-Item -LiteralPath $decoderLibrary -Destination $testOutput -Force
    $pairingTestExe = Join-Path $testOutput 'Morror.PairingTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /main:PairingTests /utf8output ("/out:" + $pairingTestExe) $resources $references ("/reference:" + $decoderLibrary) $sources (Join-Path $projectRoot 'tests\PairingTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции проверок QR.' }
    & $pairingTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Проверки QR завершились с ошибкой.' }
    $transferTestExe = Join-Path $testOutput 'Morror.TransferTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /main:TransferTests /utf8output ("/out:" + $transferTestExe) $resources $references $sources (Join-Path $projectRoot 'tests\TransferTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции проверок передачи файлов.' }
    & $transferTestExe
    if ($LASTEXITCODE -ne 0) { throw 'Проверки передачи файлов завершились с ошибкой.' }
}
Write-Host "Готово: $output\Morror.exe"
