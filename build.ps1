[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Raskladka.csproj'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path (Join-Path $framework 'MSBuild.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$builder = Join-Path $framework 'MSBuild.exe'
if (!(Test-Path $builder)) { throw 'Для сборки требуется .NET Framework 4.8 для Windows.' }
& $builder $project /nologo /v:minimal /tv:4.0 "/p:_TargetFrameworkDirectories=$framework" "/p:_FullFrameworkReferenceAssemblyPaths=$framework" "/p:WinFXAssemblyDirectory=$framework\WPF"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$browser = Join-Path $PSScriptRoot 'Integration\Browser'
if (Test-Path $browser) {
    Copy-Item -LiteralPath $browser -Destination (Join-Path $PSScriptRoot 'dist') -Recurse -Force
}
foreach ($document in @('README.md', 'Проверка.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination (Join-Path $PSScriptRoot 'dist') -Force
}
Write-Output (Join-Path $PSScriptRoot 'dist\Raskladka.exe')

