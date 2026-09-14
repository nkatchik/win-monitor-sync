[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0',
    [string]$Publisher = 'MonitorSync contributors',
    [string]$CertificateThumbprint = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$RequireSigned
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& "$PSScriptRoot/check-version.ps1" -Version $Version | Out-Null
if (-not $IsWindows) { throw 'MSI packaging and Authenticode verification must run on Windows.' }
if ($RequireSigned -and -not $CertificateThumbprint) { throw 'A signing certificate is required for a trusted release.' }
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    $publish = Join-Path $repository "artifacts/publish/$Runtime"
    $worker = Join-Path $repository "artifacts/worker/$Runtime"
    $release = Join-Path $repository 'artifacts/releases'
    foreach ($directory in @($publish, $worker)) {
        if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
        New-Item $directory -ItemType Directory -Force | Out-Null
    }
    New-Item $release -ItemType Directory -Force | Out-Null

    dotnet restore MonitorSync.slnx
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet build MonitorSync.slnx -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:Version=$Version
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet tests/MonitorSync.Tests/bin/Release/net10.0/MonitorSync.Tests.dll
    if ($LASTEXITCODE) { throw 'Synchronization tests failed.' }

    dotnet publish src/MonitorSync.App -c Release -r $Runtime --self-contained true -o $publish `
        -p:Version=$Version -p:DebugType=None --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE) { throw 'Application publish failed.' }
    dotnet publish src/MonitorSync.Worker -c Release -r $Runtime --self-contained true -o $worker `
        -p:Version=$Version -p:DebugType=None --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE) { throw 'Worker publish failed.' }
    # Share the same pinned runtime in the installed directory.
    Copy-Item (Join-Path $worker '*') $publish -Recurse -Force
    foreach ($file in @('MonitorSync.exe', 'MonitorSync.Worker.exe', 'MonitorSync.runtimeconfig.json', 'MonitorSync.Worker.runtimeconfig.json')) {
        if (-not (Test-Path (Join-Path $publish $file))) { throw "Missing payload file: $file" }
    }

    $signTool = $null
    if ($CertificateThumbprint) {
        $signTool = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64/signtool.exe" |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        if (-not $signTool) { throw 'SignTool is required. Install the Windows SDK.' }
        foreach ($file in Get-ChildItem $publish -Recurse -File | Where-Object Extension -In '.exe', '.dll') {
            $signature = Get-AuthenticodeSignature $file.FullName
            if ($signature.Status -eq 'Valid') { continue }
            if ($signature.Status -ne 'NotSigned') { throw "Invalid existing signature: $($file.FullName)" }
            & $signTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $CertificateThumbprint $file.FullName
            if ($LASTEXITCODE) { throw "Signing failed: $($file.FullName)" }
        }
    }
    $suffix = if ($CertificateThumbprint) { '' } else { '-unsigned' }
    $name = "MonitorSync-$Version-$Runtime$suffix"
    $msi = Join-Path $release "$name.msi"
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'WiX restore failed.' }
    $architecture = if ($Runtime -eq 'win-x64') { 'x64' } else { 'arm64' }
    $payloadSource = Join-Path $release "Payload-$Runtime.wxs"
    dotnet installer/MonitorSync.Packaging/bin/Release/net10.0/MonitorSync.Packaging.dll $publish $payloadSource $architecture
    if ($LASTEXITCODE) { throw 'Installer payload generation failed.' }
    dotnet tool run wix build installer/Package.wxs $payloadSource -arch $architecture `
        -d "Version=$Version" -d "Publisher=$Publisher" -o $msi
    if ($LASTEXITCODE) { throw 'MSI build failed.' }
    if ($CertificateThumbprint) {
        & $signTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $CertificateThumbprint $msi
        if ($LASTEXITCODE) { throw 'MSI signing failed.' }
        & $signTool verify /pa /all $msi
        if ($LASTEXITCODE) { throw 'MSI signature verification failed.' }
        foreach ($file in Get-ChildItem $publish -Recurse -File | Where-Object Extension -In '.exe', '.dll') {
            if ((Get-AuthenticodeSignature $file.FullName).Status -ne 'Valid') { throw "Payload signature verification failed: $($file.FullName)" }
        }
    }
    $zip = Join-Path $release "$name.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
    foreach ($artifact in @($msi, $zip)) {
        $hash = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$artifact.sha256" -Value "$hash  $(Split-Path $artifact -Leaf)`n" -Encoding ascii -NoNewline
    }
    Write-Host "Created $msi"
    if (-not $CertificateThumbprint) { Write-Warning 'Unsigned development build. This is not a trusted public release.' }
}
finally { Pop-Location }
