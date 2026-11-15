# Builds build/OpenClevo-Setup.exe: publishes the app, zips it and embeds the zip into the setup program.
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$payload = Join-Path $root 'build\payload'
$zip     = Join-Path $root 'src\OpenClevo.Setup\Payload.zip'
$out     = Join-Path $root 'build'

Remove-Item $payload, $zip -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\OpenClevo.App') -c Release -r win-x64 --self-contained false -o $payload --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -Force

dotnet publish (Join-Path $root 'src\OpenClevo.Setup') -c Release -r win-x64 --self-contained false -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }

Remove-Item $zip -Force
Write-Output "built: $(Join-Path $out 'OpenClevo-Setup.exe')"
