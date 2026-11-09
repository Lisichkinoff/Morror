$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$archive = Join-Path $projectRoot 'downloads\innosetup-6.7.3.zip'
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'downloads') | Out-Null
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4') { throw 'Контрольная сумма компилятора установщика не совпадает.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $projectRoot 'tools\innosetup-6.7.3') -Force
Write-Host 'Переносимый компилятор Inno Setup готов; установка в Windows не требуется.'
