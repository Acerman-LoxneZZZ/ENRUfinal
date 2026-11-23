[CmdletBinding()]
param([string] $Executable)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $project 'dist\Raskladka.exe' }
$binary = (Get-Item -LiteralPath $Executable).FullName
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath "$framework\csc.exe")) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('Raskladka-MotionTest-' + [guid]::NewGuid().ToString('N')))
$copy = Join-Path $fixture.FullName 'Raskladka.exe'
$test = Join-Path $fixture.FullName 'WindowMotionTests.exe'
try {
    Copy-Item -LiteralPath $binary -Destination $copy
    $references = @('WindowsBase','PresentationCore','PresentationFramework') | ForEach-Object {
        '/r:' + (Join-Path $framework "WPF\$_.dll")
    }
    $sources = @('Tests\WindowMotionTests.cs', 'Tests\PrivateDesktop.cs') | ForEach-Object { Join-Path $project $_ }
    & "$framework\csc.exe" /nologo /langversion:5 /codepage:65001 /optimize+ "/out:$test" "/r:$copy" /r:System.Xaml.dll $references $sources
    if ($LASTEXITCODE -ne 0) { throw 'Motion test compilation failed.' }
    & $test
    if ($LASTEXITCODE -ne 0) { throw 'Window motion regression.' }
}
finally {
    foreach ($file in @($test, $copy)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    $fixture.Delete()
}
