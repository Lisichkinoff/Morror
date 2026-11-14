$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot
$toolRoot = Join-Path $projectRoot 'tools\native-monitor'
$jdk = Get-ChildItem -LiteralPath (Join-Path $toolRoot 'jdk') -Directory | Select-Object -First 1 -ExpandProperty FullName
$androidTools = Get-ChildItem -LiteralPath (Join-Path $toolRoot 'android-tools') -Directory | Select-Object -First 1 -ExpandProperty FullName
$androidJar = Get-ChildItem -LiteralPath (Join-Path $toolRoot 'platform') -Filter android.jar -Recurse -File | Select-Object -First 1 -ExpandProperty FullName
$nativeSdkCache = Join-Path ([IO.Path]::GetTempPath()) 'Morror-Android-SDK35'
New-Item -ItemType Directory -Force -Path $nativeSdkCache | Out-Null
$nativeAndroidJar = Join-Path $nativeSdkCache 'android.jar'
if (!(Test-Path -LiteralPath $nativeAndroidJar) -or (Get-FileHash -LiteralPath $nativeAndroidJar).Hash -ne (Get-FileHash -LiteralPath $androidJar).Hash) { Copy-Item -LiteralPath $androidJar -Destination $nativeAndroidJar -Force }
$buildDir = Join-Path $projectRoot 'test-output\android-monitor'
$classes = Join-Path $buildDir 'classes'
$dex = Join-Path $buildDir 'dex'
New-Item -ItemType Directory -Force -Path $buildDir,$classes,$dex | Out-Null
& (Join-Path $jdk 'bin\javac.exe') -encoding UTF-8 -source 8 -target 8 -classpath $androidJar -d $classes (Join-Path $projectRoot 'android\src\ru\morror\monitor\MonitorActivity.java')
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции Android-клиента.' }
$classFiles = Get-ChildItem -LiteralPath $classes -Filter '*.class' -Recurse -File | ForEach-Object FullName
& (Join-Path $jdk 'bin\java.exe') -cp (Join-Path $androidTools 'lib\d8.jar') com.android.tools.r8.D8 --lib $androidJar --min-api 26 --output $dex $classFiles
if ($LASTEXITCODE -ne 0) { throw 'Ошибка D8.' }
$unsigned = Join-Path $buildDir 'unsigned.apk'
$aapt = Join-Path $androidTools 'aapt2.exe'
# AAPT2 cannot enumerate resource directories through every Windows system code page.
Push-Location -LiteralPath (Join-Path $projectRoot 'android')
try {
    & $aapt compile --dir res -o '..\test-output\android-monitor\resources.zip'
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции ресурсов Android.' }
    & $aapt link -I $nativeAndroidJar --manifest AndroidManifest.xml -o '..\test-output\android-monitor\unsigned.apk' '..\test-output\android-monitor\resources.zip'
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка упаковки APK.' }
} finally { Pop-Location }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($unsigned,[System.IO.Compression.ZipArchiveMode]::Update)
try { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $dex 'classes.dex'),'classes.dex') | Out-Null } finally { $archive.Dispose() }
$aligned = Join-Path $buildDir 'aligned.apk'
& (Join-Path $androidTools 'zipalign.exe') -f 4 $unsigned $aligned
if ($LASTEXITCODE -ne 0) { throw 'Ошибка zipalign.' }
$keyStore = Join-Path $toolRoot 'morror-monitor.keystore'
if (!(Test-Path -LiteralPath $keyStore)) {
    # Local development identity, not a production publishing certificate. Keep this file for APK updates.
    & (Join-Path $jdk 'bin\keytool.exe') -genkeypair -keystore $keyStore -storepass morror-local-build -keypass morror-local-build -alias morror -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Morror Local Build' -storetype JKS
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать локальный ключ APK.' }
}
$output = Join-Path $toolRoot 'MorrorMonitor.apk'
& (Join-Path $jdk 'bin\java.exe') -jar (Join-Path $androidTools 'lib\apksigner.jar') sign --ks $keyStore --ks-key-alias morror --ks-pass pass:morror-local-build --key-pass pass:morror-local-build --out $output $aligned
if ($LASTEXITCODE -ne 0) { throw 'Ошибка подписи APK.' }
& (Join-Path $jdk 'bin\java.exe') -jar (Join-Path $androidTools 'lib\apksigner.jar') verify $output
if ($LASTEXITCODE -ne 0) { throw 'APK не прошёл проверку подписи.' }
Write-Host ('Готов Android-клиент: ' + $output)
