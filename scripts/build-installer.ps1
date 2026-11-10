param([switch]$Test, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$compiler = Join-Path $projectRoot 'tools\innosetup-6.7.3\tools\ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Сначала выполните scripts\get-installer-tools.ps1.' }
if (!$SkipBuild) { & (Join-Path $projectRoot 'build.ps1') }
$payload = Join-Path $projectRoot 'dist\Morror-0.9.2'
if (!(Test-Path -LiteralPath (Join-Path $payload 'Morror.exe'))) { throw 'Не найдена сборка Morror 0.9.2.' }
$recipe = Join-Path $projectRoot 'installer\Morror.iss'
$temporary = Join-Path $projectRoot 'test-output'
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$manifest = Join-Path $temporary 'installer-payload.iss'
$fileEntries = foreach ($file in (Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($payload.TrimEnd('\').Length + 1)
    $subdirectory = Split-Path $relative
    $destination = if ($subdirectory) { '{app}\' + $subdirectory } else { '{app}' }
    $checksum = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $pascalPath = $relative.Replace("'", "''")
    'Source: "' + $file.FullName + '"; DestDir: "' + $destination + '"; Flags: ignoreversion; Check: NeedsFile(''' + $pascalPath + ''', ''' + $checksum + ''')'
}
Set-Content -LiteralPath $manifest -Value $fileEntries -Encoding utf8
& $compiler /Qp ("/DPayloadDir=" + $payload) ("/DFileManifest=" + $manifest) $recipe
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать установщик.' }
if ($Test) { & (Join-Path $projectRoot 'tests\InstallerTests.ps1') -Compiler $compiler -Payload $payload -Recipe $recipe -Manifest $manifest }
$setup = Join-Path $projectRoot 'dist\Morror-0.9.2-Setup-win64.exe'
Write-Host "Готово: $setup"
