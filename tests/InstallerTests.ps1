param([Parameter(Mandatory=$true)][string]$Compiler, [Parameter(Mandatory=$true)][string]$Payload, [Parameter(Mandatory=$true)][string]$Recipe, [Parameter(Mandatory=$true)][string]$Manifest, [Parameter(Mandatory=$true)][string]$AppVersion)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$testRoot = Join-Path $projectRoot ('test-output\installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
# The test variant redirects shortcuts into its own directory and creates no uninstall registry key.
& $Compiler /Qp /DTestBuild=1 ("/DMyAppVersion=" + $AppVersion) ("/DInstallerOutput=" + $testRoot) ("/DFileManifest=" + $Manifest) $Recipe
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки тестового установщика.' }
$testSetup = Join-Path $testRoot "Morror-$AppVersion-Setup-Test.exe"
$script:checkCount = 0
function Assert-Installer([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checkCount++
    Write-Host ('PASS ' + $Message)
}
function Run-Installer([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Превышено время ожидания тестового установщика.' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
Add-Type -AssemblyName System.Drawing
$executable = Join-Path $Payload 'Morror.exe'
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
Assert-Installer ($version.ProductName -eq 'Morror' -and $version.FileVersion -eq "$AppVersion.0") "Executable has product metadata and version $AppVersion.0"
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($executable)
try {
    $bitmap = $icon.ToBitmap()
    try {
        $lavenderPixels = 0
        for ($x=0; $x -lt $bitmap.Width; $x++) { for ($y=0; $y -lt $bitmap.Height; $y++) { $pixel=$bitmap.GetPixel($x,$y); if ($pixel.A -gt 200 -and $pixel.B -gt 180 -and $pixel.R -gt 130 -and $pixel.B -gt $pixel.G+20) { $lavenderPixels++ } } }
        Assert-Installer ($lavenderPixels -gt 15) 'Windows extracts the lavender Morror icon from the executable'
    } finally { $bitmap.Dispose() }
} finally { $icon.Dispose() }
$mirrorIcon = Get-FileHash -LiteralPath (Join-Path $Payload 'runtime\scrcpy.png')
$brandIcon = Get-FileHash -LiteralPath (Join-Path $projectRoot 'assets\morror.png')
Assert-Installer ($mirrorIcon.Hash -eq $brandIcon.Hash) 'Android mirroring window uses the Morror icon'
$brand = [Drawing.Icon]::ExtractAssociatedIcon($executable)
try {
    $expected = $brand.ToBitmap()
    try {
        foreach ($name in @('runtime\scrcpy.exe', 'runtime\iphone\Morror.iPhone.exe')) {
            $engineIcon = [Drawing.Icon]::ExtractAssociatedIcon((Join-Path $Payload $name))
            try {
                $actual = $engineIcon.ToBitmap()
                try {
                    $matches = $actual.Size -eq $expected.Size
                    for ($x = 0; $matches -and $x -lt $expected.Width; $x++) {
                        for ($y = 0; $matches -and $y -lt $expected.Height; $y++) {
                            $matches = $actual.GetPixel($x, $y).ToArgb() -eq $expected.GetPixel($x, $y).ToArgb()
                        }
                    }
                    Assert-Installer $matches "$name exposes the same Windows icon as Morror"
                } finally { $actual.Dispose() }
            } finally { $engineIcon.Dispose() }
        }
    } finally { $expected.Dispose() }
} finally { $brand.Dispose() }
$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($withDesktop in @($true,$false)) {
        $caseName = if ($withDesktop) { 'with-desktop' } else { 'without-desktop' }
        $destination = Join-Path $testRoot $caseName
        $tasks = if ($withDesktop) { '/TASKS=desktopicon' } else { '/TASKS=' }
        $installLog = Join-Path $testRoot ($caseName + '-install.log')
        $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $destination + '"'),$tasks,('/LOG="' + $installLog + '"'))
        $exitCode = Run-Installer $testSetup $arguments
        Assert-Installer ($exitCode -eq 0) ($caseName + ': installer completes successfully')
        $complete = $true
        foreach ($file in (Get-ChildItem -LiteralPath $Payload -File -Recurse)) {
            $relative = $file.FullName.Substring($Payload.TrimEnd('\').Length + 1)
            $installedFile = Join-Path $destination $relative
            if (!(Test-Path -LiteralPath $installedFile) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installedFile).Hash) { $complete = $false }
        }
        Assert-Installer $complete ($caseName + ': all application, QR and mirroring files are installed intact')
        $startLink = Join-Path $destination 'TestStartMenu\Morror.lnk'
        $desktopLink = Join-Path $destination 'TestDesktop\Morror.lnk'
        Assert-Installer (Test-Path -LiteralPath $startLink) ($caseName + ': Start menu shortcut is created')
        Assert-Installer ((Test-Path -LiteralPath $desktopLink) -eq $withDesktop) ($caseName + ': desktop shortcut respects the checkbox')
        $shortcut = $shell.CreateShortcut($startLink)
        $installedExe = Join-Path $destination 'Morror.exe'
        $installedIcon = Join-Path $destination 'morror.ico'
        Assert-Installer ($shortcut.TargetPath -eq $installedExe -and $shortcut.WorkingDirectory -eq $destination -and $shortcut.IconLocation -eq "$installedIcon,0") ($caseName + ': shortcut target, working folder and icon are correct')
        if ($withDesktop) {
            $desktopShortcut = $shell.CreateShortcut($desktopLink)
            Assert-Installer ($desktopShortcut.TargetPath -eq $installedExe -and $desktopShortcut.IconLocation -eq "$installedIcon,0") 'Desktop shortcut launches the installed application with the Morror icon'
            $lockedFile = [System.IO.File]::Open((Join-Path $destination 'runtime\adb.exe'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            try {
                $repeatCode = Run-Installer $testSetup $arguments
                Assert-Installer ($repeatCode -eq 0) 'Repeat installation handles an unchanged running connection component'
            } finally { $lockedFile.Dispose() }
        }
        $sentinel = Join-Path $destination 'user-created-file.txt'
        Set-Content -LiteralPath $sentinel -Value 'This file must survive uninstallation.'
        $uninstaller = Join-Path $destination 'unins000.exe'
        $uninstallLog = Join-Path $testRoot ($caseName + '-uninstall.log')
        $exitCode = Run-Installer $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + $uninstallLog + '"'))
        Assert-Installer ($exitCode -eq 0 -and !(Test-Path -LiteralPath $installedExe) -and !(Test-Path -LiteralPath $startLink) -and !(Test-Path -LiteralPath $desktopLink)) ($caseName + ': uninstaller removes the managed app and shortcuts')
        Assert-Installer (Test-Path -LiteralPath $sentinel) ($caseName + ': uninstaller preserves unrelated user files')
    }
} finally { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
$portable = Join-Path $testRoot 'portable copy'
New-Item -ItemType Directory -Path $portable | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $portable 'Morror.exe')
$sentinel = Join-Path $portable 'user-created-file.txt'
Set-Content -LiteralPath $sentinel -Value 'This file must survive an update.'
$exitCode = Run-Installer $testSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $portable + '"'),'/MORRORPORTABLEUPDATE')
Assert-Installer ($exitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $portable 'runtime\adb.exe'))) 'Portable update installs files in the existing folder'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $portable 'unins000.exe')) -and !(Test-Path -LiteralPath (Join-Path $portable 'TestStartMenu'))) 'Portable update keeps the copy portable'
Assert-Installer (Test-Path -LiteralPath $sentinel) 'Portable update preserves unrelated user files'
Write-Host ($script:checkCount.ToString() + ' installer checks passed. Test files: ' + $testRoot)
