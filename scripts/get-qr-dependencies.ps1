param([switch]$IncludeTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$packages = @(@{ Id='qrcoder'; Version='1.8.1'; Folder='qrcoder-1.8.1'; Hash='D3FE5C80C742B6A2EF8A31734C06600E2D674806A97F36F4AF97B0D2871DE309' })
if ($IncludeTests) { $packages += @{ Id='zxing.net'; Version='0.16.11'; Folder='zxing-0.16.11'; Hash='7D39234D668E558B3D374D18116ED57021D7C0DF482801F1E1DB4F1DB9314EC3' } }
New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'downloads') | Out-Null
foreach ($package in $packages) {
    $archive = Join-Path $projectRoot ('downloads\' + $package.Folder + '.zip')
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri ('https://api.nuget.org/v3-flatcontainer/' + $package.Id + '/' + $package.Version + '/' + $package.Id + '.' + $package.Version + '.nupkg') -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Hash) { throw ('Контрольная сумма не совпадает: ' + $package.Id) }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $projectRoot ('tools\' + $package.Folder)) -Force
    Write-Host ($package.Id + ': SHA-256 проверен.')
}
