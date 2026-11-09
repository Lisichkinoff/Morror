$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$version = '5.0.1'
$expected = 'B12A2C4EE8BE317422451FC7DCF8EE20A71B5EA7EF9AD73DDD825A25316227A5'
$downloads = Join-Path $projectRoot 'downloads'
$tools = Join-Path $projectRoot 'tools'
New-Item -ItemType Directory -Force -Path $downloads,$tools | Out-Null
$archive = Join-Path $downloads "scrcpy-win64-v$version.zip"
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/Genymobile/scrcpy/releases/download/v$version/scrcpy-win64-v$version.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Контрольная сумма scrcpy не совпадает. Загрузка не будет распакована.' }
Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force
Write-Host "scrcpy $version: SHA-256 проверен, компоненты готовы."
