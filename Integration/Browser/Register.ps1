[CmdletBinding()]
param(
    [string] $ExecutablePath = (Join-Path $PSScriptRoot '..\Raskladka.exe'),
    [switch] $Remove
)
$ErrorActionPreference = 'Stop'
$name = 'org.raskladka.editor'
$folder = Join-Path $env:LOCALAPPDATA 'Raskladka\Browser'
$manifest = Join-Path $folder 'host.json'
$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$name",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$name",
    "HKCU:\Software\Chromium\NativeMessagingHosts\$name"
)
if ($Remove) {
    foreach ($key in $keys) {
        if ((Test-Path -LiteralPath $key) -and (Get-Item -LiteralPath $key).GetValue('') -eq $manifest) {
            Remove-Item -LiteralPath $key
        }
    }
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
    return
}
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($exe) -ne 'Raskladka.exe') { throw 'Select Raskladka.exe from the portable build.' }
[IO.Directory]::CreateDirectory($folder) | Out-Null
$definition = [ordered]@{
    name = $name
    description = 'Raskladka desktop editor'
    path = $exe
    type = 'stdio'
    allowed_origins = @('chrome-extension://mcpenfmmkfcimaeobcgebnhkbeemhdaf/')
}
[IO.File]::WriteAllText($manifest, ($definition | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
foreach ($key in $keys) {
    New-Item -Path $key -Force | Out-Null
    Set-Item -LiteralPath $key -Value $manifest
}
Write-Output (Join-Path $PSScriptRoot 'Extension')
