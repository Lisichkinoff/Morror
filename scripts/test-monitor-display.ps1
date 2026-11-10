param([int[]]$Modes=@(2400,1080,60,1920,864,60))
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework=Split-Path $compiler
$references=@('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object {'/reference:'+(Join-Path $framework $_)}
$references+='/reference:'+(Join-Path $projectRoot 'tools\qrcoder-1.8.1\lib\net40\QRCoder.dll')
$sources=Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName
$output=Join-Path $projectRoot 'test-output\Morror.NativeResolutionSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /main:NativeResolutionSmoke /utf8output ("/out:"+$output) $references $sources (Join-Path $projectRoot 'tests\NativeResolutionSmoke.cs')
if($LASTEXITCODE -ne 0){throw 'Display smoke compilation failed'}
& $output $Modes
if($LASTEXITCODE -ne 0){throw 'Display smoke failed'}
