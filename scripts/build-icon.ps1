$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$temporary = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$generator = Join-Path $temporary 'IconBuilder.exe'
& $compiler /nologo /target:exe ("/out:" + $generator) /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'IconBuilder.cs')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать генератор иконки.' }
& $generator (Join-Path $projectRoot 'assets')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать иконку.' }
Write-Host 'Иконка Morror: 10 размеров, от 16 до 256 px.'
