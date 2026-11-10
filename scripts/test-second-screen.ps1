$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$output = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += '/reference:' + (Join-Path $projectRoot 'tools\qrcoder-1.8.1\lib\net40\QRCoder.dll')
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName
$resources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.xaml' -File | ForEach-Object { '/resource:' + $_.FullName + ',' + $_.Name }

$executable = Join-Path $output 'Morror.SecondScreenTests.exe'
& $compiler /nologo /target:exe /platform:x64 /main:SecondScreenTests /utf8output ("/out:" + $executable) $references $resources $sources (Join-Path $projectRoot 'tests\SecondScreenTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки проверок второго экрана.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\qrcoder-1.8.1\lib\net40\QRCoder.dll') -Destination $output -Force
$codecExe = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools\native-monitor\ffmpeg') -Filter ffmpeg.exe -Recurse -File | Select-Object -First 1 -ExpandProperty FullName
& $executable $codecExe (Join-Path $projectRoot 'tools\native-monitor\driver\VirtualDisplayDriver')
if ($LASTEXITCODE -ne 0) { throw 'Ошибка проверок второго экрана.' }
