param([switch]$Test, [string]$OutputDirectory = 'dist\Morror-0.9.0')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
if (!(Test-Path -LiteralPath $compiler)) { throw 'Нужен .NET Framework 4.8 (встроен в Windows 10/11).' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$qrLibrary = Join-Path $projectRoot 'tools\qrcoder-1.8.1\lib\net40\QRCoder.dll'
if (!(Test-Path -LiteralPath $qrLibrary)) { throw 'Сначала выполните scripts\get-qr-dependencies.ps1.' }
$references += '/reference:' + $qrLibrary
$sources = @('Core.cs','App.cs','TrayController.cs','WirelessPairing.cs','QrPairingDialog.cs','MirrorWindow.cs','DeviceCapabilities.cs','FileTransfer.cs','ClipboardTransfer.cs','TransferDialog.cs','AssemblyInfo.cs') | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$resources = @('MainWindow.xaml','QrWindow.xaml','TransferWindow.xaml') | ForEach-Object { '/resource:' + (Join-Path $projectRoot ('src\' + $_)) + ',' + $_ }
$icon = Join-Path $projectRoot 'assets\morror.ico'
if (!(Test-Path -LiteralPath $icon)) { & (Join-Path $projectRoot 'scripts\build-icon.ps1') }
$iconResource = '/resource:' + $icon + ',Morror.ico'
$resources += $iconResource
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output ("/out:" + (Join-Path $output 'Morror.exe')) ("/win32icon:" + $icon) $resources ("/win32manifest:" + (Join-Path $projectRoot 'src\app.manifest')) $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции Morror.' }
$engine = Join-Path $projectRoot 'tools\scrcpy-win64-v5.0.1'
if (!(Test-Path -LiteralPath (Join-Path $engine 'scrcpy.exe'))) { throw 'Сначала выполните scripts\get-scrcpy.ps1.' }
$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Get-ChildItem -LiteralPath $engine -File | Where-Object { $_.Extension -in @('.exe','.dll','.txt','.png') -or $_.Name -eq 'scrcpy-server' } | ForEach-Object {
    $destination = Join-Path $runtime $_.Name
    # ADB may be serving this or another app: do not overwrite identical running components.
    if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
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



