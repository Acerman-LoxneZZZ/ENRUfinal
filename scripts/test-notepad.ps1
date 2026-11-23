$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$testExe = Join-Path ([IO.Path]::GetTempPath()) ('Raskladka-NotepadTest-' + [guid]::NewGuid().ToString('N') + '.exe')
$sources = @((Get-ChildItem -LiteralPath (Join-Path $project 'Core') -Filter '*.cs').FullName)
$sources += (Get-ChildItem -LiteralPath (Join-Path $project 'Integration') -Filter '*.cs').FullName
$sources += (Get-ChildItem -LiteralPath (Join-Path $project 'Tests') -Filter 'NotepadScenario*.cs').FullName
$sources += Join-Path $project 'Tests\PrivateDesktop.cs'
$references = @('WindowsBase', 'UIAutomationClient', 'UIAutomationTypes') | ForEach-Object { '/r:' + (Join-Path $framework ('WPF\' + $_ + '.dll')) }
try {
    & $compiler /nologo /langversion:5 /codepage:65001 /optimize+ /target:exe /r:System.Web.Extensions.dll "/out:$testExe" $references $sources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Notepad scenario failed' }
}
finally {
    if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe }
}
