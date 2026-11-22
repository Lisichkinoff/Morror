param([switch]$Download, [switch]$Test)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$stage = Join-Path $projectRoot 'tools\scrcpy-live'
$patch = Join-Path $PSScriptRoot 'scrcpy-windows-live-resize.patch'
$output = Join-Path $stage 'scrcpy.exe'
$stamp = Join-Path $stage 'build.sha256'
$fingerprint = (Get-FileHash -LiteralPath $patch).Hash + (Get-FileHash -LiteralPath $PSCommandPath).Hash
if (!$Test -and (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $stamp) -and
    ([IO.File]::ReadAllText($stamp) -eq $fingerprint)) {
    return $output
}

$runtime = Join-Path $projectRoot 'tools\scrcpy-win64-v5.0.1'
if (!(Test-Path -LiteralPath (Join-Path $runtime 'SDL3.dll'))) { throw 'Выполните scripts\get-scrcpy.ps1.' }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Get-PinnedArchive([string]$Name, [string]$Url, [string]$Hash) {
    $path = Join-Path $stage $Name
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $Hash) {
        if (!$Download) { throw "Нет проверенного архива $Name. Выполните scripts\get-scrcpy.ps1." }
        Write-Host "Загрузка $Name…"
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $path
        if ((Get-FileHash -LiteralPath $path).Hash -ne $Hash) { throw "Неверная контрольная сумма $Name." }
    }
    return $path
}

$toolchain = Join-Path $stage 'llvm-mingw-20260616-ucrt-x86_64'
$compiler = Join-Path $toolchain 'bin\x86_64-w64-mingw32-clang.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $llvmName = 'llvm-mingw-20260616-ucrt-x86_64.zip'
    $llvm = Get-PinnedArchive $llvmName "https://github.com/mstorsjo/llvm-mingw/releases/download/20260616/$llvmName" 'b9b68a4d276e16fa25802aaba458e4638f64b3884c290aaccdc2d87083b6ca35'
    Expand-Archive -LiteralPath $llvm -DestinationPath $stage -Force
}

# Use the same headers as the DLLs in the pinned scrcpy release.
$dependencies = @(
    @{ Folder='ffmpeg-9.0.2'; File='ffmpeg-9.0.2.tar.xz'; Url='https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz'; Hash='8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e' },
    @{ Folder='SDL-release-3.4.18'; File='sdl-3.4.18.tar.gz'; Url='https://codeload.github.com/libsdl-org/SDL/tar.gz/refs/tags/release-3.4.18'; Hash='c4b08b950bd29d83caae0ab8d884298d27046a9d30700fb42841c50992665c90' },
    @{ Folder='libusb-1.0.30'; File='libusb-1.0.30.tar.gz'; Url='https://codeload.github.com/libusb/libusb/tar.gz/refs/tags/v1.0.30'; Hash='2ae28adb0bb9558c86135c4e1c11b320b0805461e207a64a6e520a114094bf07' }
)
foreach ($dependency in $dependencies) {
    if (!(Test-Path -LiteralPath (Join-Path $stage $dependency.Folder))) {
        $archive = Get-PinnedArchive $dependency.File $dependency.Url $dependency.Hash
        & tar.exe -xf $archive -C $stage
        if ($LASTEXITCODE -ne 0) { throw "Не удалось распаковать $archive." }
    }
}

$sourceZip = Get-PinnedArchive 'scrcpy-5.0.1.zip' 'https://codeload.github.com/Genymobile/scrcpy/zip/refs/tags/v5.0.1' '79cd9d6eaf874f41cbff081014ddc15a4e4a3e156c7b338dd199b3bcb701302d'
$sourceStage = Join-Path $stage 'client-source'
Expand-Archive -LiteralPath $sourceZip -DestinationPath $sourceStage -Force
$relativeSource = 'tools/scrcpy-live/client-source/scrcpy-5.0.1'
$source = Join-Path $projectRoot $relativeSource
& git.exe -C $projectRoot apply "--directory=$relativeSource" --whitespace=nowarn $patch
if ($LASTEXITCODE -ne 0) { throw 'Не удалось применить исправление scrcpy.' }

$include = Join-Path $stage 'include'
$usbInclude = Join-Path $include 'libusb-1.0'
$avInclude = Join-Path $include 'libavutil'
New-Item -ItemType Directory -Force -Path $usbInclude,$avInclude | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'libusb-1.0.30\libusb\libusb.h') -Destination $usbInclude -Force
Set-Content -LiteralPath (Join-Path $avInclude 'avconfig.h') -Encoding ascii -Value @(
    '#define AV_HAVE_BIGENDIAN 0'
    '#define AV_HAVE_FAST_UNALIGNED 1'
)
Set-Content -LiteralPath (Join-Path $include 'config.h') -Encoding ascii -Value @(
    '#define SCRCPY_VERSION "5.0.1"'
    '#define PREFIX ""'
    '#define PORTABLE 1'
    '#define DEFAULT_LOCAL_PORT_RANGE_FIRST 27183'
    '#define DEFAULT_LOCAL_PORT_RANGE_LAST 27199'
    '#define HAVE_STRDUP 1'
    '#define HAVE_ASPRINTF 1'
    '#define HAVE_VASPRINTF 1'
    '#define HAVE_D3D11VA 1'
    '#define HAVE_USB 1'
)

$client = Join-Path $source 'app'
$meson = Get-Content -LiteralPath (Join-Path $client 'meson.build') -Raw
$common = [regex]::Match($meson, '(?s)\Asrc = \[(.*?)\]').Groups[1].Value
$sources = @([regex]::Matches($common, "'(src/[^']+\.c)'") | ForEach-Object { $_.Groups[1].Value })
if (!$sources) { throw 'Не удалось прочитать список исходников scrcpy.' }
$sources += @(
    'src/util/command.c','src/sys/win/file.c','src/sys/win/process.c','src/interop/interop_d3d11va.c'
    'src/usb/aoa_hid.c','src/usb/gamepad_aoa.c','src/usb/keyboard_aoa.c','src/usb/mouse_aoa.c','src/usb/scrcpy_otg.c','src/usb/usb.c'
)
$resourceCompiler = Join-Path $toolchain 'bin\x86_64-w64-mingw32-windres.exe'
$resource = Join-Path $stage 'scrcpy-resource.o'
$temporary = Join-Path $stage 'scrcpy-building.exe'
$flags = @(
    '-std=c11','-O2','-DNDEBUG','-D_GNU_SOURCE','-D_POSIX_C_SOURCE=200809L','-D_XOPEN_SOURCE=700','-D_WIN32_WINNT=0x0600','-DWINVER=0x0600'
    '-Isrc','-I',$include,'-I',(Join-Path $stage 'ffmpeg-9.0.2'),'-I',(Join-Path $stage 'SDL-release-3.4.18\include')
)
$libraries = @(
    '-L',$runtime,'-l:avformat-63.dll','-l:avcodec-63.dll','-l:avutil-61.dll','-l:swresample-7.dll','-l:SDL3.dll','-l:libusb-1.0.dll','-lws2_32'
)
Push-Location -LiteralPath $client
try {
    & $resourceCompiler -i scrcpy-windows.rc -O coff -o $resource
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать ресурсы scrcpy.' }
    & $compiler @flags @sources $resource @libraries -o $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать клиент scrcpy.' }
    if ($Test) {
        $testExecutable = Join-Path $stage 'scrcpy-live-window-test.exe'
        $testSources = @($sources | Where-Object { $_ -ne 'src/main.c' }) + (Join-Path $projectRoot 'tests\ScrcpyLiveWindow.c')
        & $compiler @flags @testSources @libraries -o $testExecutable
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверку трансляции scrcpy.' }
        $start = [Diagnostics.ProcessStartInfo]::new($testExecutable)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.EnvironmentVariables['PATH'] = $runtime + ';' + $env:PATH
        $start.EnvironmentVariables['SCRCPY_ICON_DIR'] = $runtime
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (!$process.WaitForExit(15000)) {
                $process.Kill()
                throw 'Проверка окна трансляции не завершилась.'
            }
            Write-Host $process.StandardOutput.ReadToEnd()
            if ($process.ExitCode -ne 0) { throw 'Проверка окна трансляции не прошла.' }
        } finally { $process.Dispose() }
    }
} finally { Pop-Location }
Move-Item -LiteralPath $temporary -Destination $output -Force
[IO.File]::WriteAllText($stamp, $fingerprint)
return $output
