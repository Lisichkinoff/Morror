param([switch]$Test, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$compiler = Join-Path $projectRoot 'tools\innosetup-6.7.3\tools\ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Сначала выполните scripts\get-installer-tools.ps1.' }
if (!$SkipBuild) { & (Join-Path $projectRoot 'build.ps1') }
$version = & (Join-Path $PSScriptRoot 'get-app-version.ps1')
$payload = Join-Path $projectRoot "dist\Morror-$version"
$executable = Join-Path $payload 'Morror.exe'
if (!(Test-Path -LiteralPath $executable)) { throw "Не найдена сборка Morror $version." }
if ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion -ne "$version.0") { throw 'Версия сборки Morror не совпадает с версией установщика.' }
$recipe = Join-Path $projectRoot 'installer\Morror.iss'
$temporary = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$manifest = Join-Path $temporary 'installer-payload.iss'
# Replace the versioned executable after the runtime files so a failed update can be retried.
$fileEntries = foreach ($file in (Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object { $_.FullName -eq $executable }, FullName)) {
    $relative = $file.FullName.Substring($payload.TrimEnd('\').Length + 1)
    $subdirectory = Split-Path $relative
    $destination = if ($subdirectory) { '{app}\' + $subdirectory } else { '{app}' }
    $checksum = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $pascalPath = $relative.Replace("'", "''")
    'Source: "' + $file.FullName + '"; DestDir: "' + $destination + '"; Flags: ignoreversion; Check: NeedsFile(''' + $pascalPath + ''', ''' + $checksum + ''')'
}
Set-Content -LiteralPath $manifest -Value $fileEntries -Encoding utf8
& $compiler /Qp ("/DMyAppVersion=" + $version) ("/DFileManifest=" + $manifest) $recipe
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать установщик.' }
if ($Test) { & (Join-Path $projectRoot 'tests\InstallerTests.ps1') -Compiler $compiler -Payload $payload -Recipe $recipe -Manifest $manifest -AppVersion $version }
$setup = Join-Path $projectRoot "dist\Morror-$version-Setup-win64.exe"
Write-Host "Готово: $setup"
