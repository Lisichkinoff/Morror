$assemblyInfo = Join-Path (Split-Path $PSScriptRoot) 'src\AssemblyInfo.cs'
$match = [regex]::Match((Get-Content -LiteralPath $assemblyInfo -Raw), 'const string Number = "([0-9]+\.[0-9]+\.[0-9]+)";')
if (!$match.Success) { throw 'Не удалось определить версию Morror из src\AssemblyInfo.cs.' }
$match.Groups[1].Value
