# Builds build/OpenClevo-Setup.exe: publishes the app, zips it and embeds the zip into the setup program.
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$payload = Join-Path $root 'build\payload'
$zip     = Join-Path $root 'src\OpenClevo.Setup\Payload.zip'
$out     = Join-Path $root 'build'

Remove-Item $payload, $zip -Recurse -Force -ErrorAction SilentlyContinue
# self-contained: the .NET runtime is inside the exe, so nothing has to be installed on the user's PC. One compressed file.
dotnet publish (Join-Path $root 'src\OpenClevo.App') -c Release -r win-x64 --self-contained true -o $payload --nologo -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# Acpi Bridge driver (ACPI\CLV0001): the app talks to it, so the setup installs it. Taken from drivers\AcpiBridge, or
# from the package already installed on this machine. Clevo's binaries, not ours: keep drivers\ out of version control.
$drv = Join-Path $root 'drivers\AcpiBridge'
if (-not (Test-Path (Join-Path $drv 'AcpiBridge.sys'))) {
    $store = Get-ChildItem "$env:windir\System32\DriverStore\FileRepository" -Directory -Filter 'acpibridge.inf_amd64_*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($store) {
        New-Item -ItemType Directory -Force $drv | Out-Null
        foreach ($f in 'AcpiBridge.Inf', 'AcpiBridge.sys', 'AcpiBridge.cat') { Copy-Item (Join-Path $store.FullName $f) $drv -Force }
    }
}
if (Test-Path (Join-Path $drv 'AcpiBridge.sys')) {
    $drvOut = Join-Path $payload 'Drivers\AcpiBridge'
    New-Item -ItemType Directory -Force $drvOut | Out-Null
    Copy-Item (Join-Path $drv '*') $drvOut -Force
} else {
    Write-Warning 'drivers\AcpiBridge not found: this setup will not install the Acpi Bridge driver.'
}

Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -Force

dotnet publish (Join-Path $root 'src\OpenClevo.Setup') -c Release -r win-x64 --self-contained true -o $out --nologo -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }

Remove-Item $zip -Force
Write-Output "built: $(Join-Path $out 'OpenClevo-Setup.exe')"
