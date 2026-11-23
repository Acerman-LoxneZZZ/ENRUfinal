[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test = Join-Path ([IO.Path]::GetTempPath()) ('Raskladka-CoreTest-' + [guid]::NewGuid().ToString('N') + '.exe')
$sources = @((Get-ChildItem -LiteralPath (Join-Path $project 'Core') -Filter '*.cs').FullName)
$sources += Join-Path $project 'Tests\CoreTests.cs'
try {
    & $compiler /nologo /langversion:5 /codepage:65001 /optimize+ /target:exe "/out:$test" $sources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & $test
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
} finally {
    if (Test-Path -LiteralPath $test) { Remove-Item -LiteralPath $test }
}
