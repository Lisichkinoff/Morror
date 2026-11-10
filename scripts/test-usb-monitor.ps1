param([Parameter(Mandatory=$true)][string]$Serial)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework=Split-Path $compiler
$references=@('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object {'/reference:'+(Join-Path $framework $_)}
$references+='/reference:'+(Join-Path $projectRoot 'tools\qrcoder-1.8.1\lib\net40\QRCoder.dll')
$sources=Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName
$output=Join-Path $projectRoot 'test-output\Morror.NativeUsbMonitorSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /main:NativeUsbMonitorSmoke /utf8output ("/out:"+$output) ("/win32manifest:"+(Join-Path $projectRoot 'src\app.manifest')) $references $sources (Join-Path $projectRoot 'tests\NativeResolutionSmoke.cs') (Join-Path $projectRoot 'tests\NativeUsbMonitorSmoke.cs')
if($LASTEXITCODE -ne 0){throw 'USB monitor smoke compilation failed'}
$ffmpeg=Get-ChildItem (Join-Path $projectRoot 'tools\native-monitor\ffmpeg') -Filter ffmpeg.exe -Recurse -File | Select-Object -First 1 -ExpandProperty FullName
& $output (Join-Path $projectRoot 'tools\scrcpy-win64-v5.0.1\adb.exe') $Serial (Join-Path $projectRoot 'tools\native-monitor\MorrorMonitor.apk') $ffmpeg
if($LASTEXITCODE -ne 0){throw 'USB monitor smoke failed'}
