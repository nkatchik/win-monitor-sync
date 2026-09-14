[CmdletBinding()]
param(
    [switch]$DiagnosticsOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'The app and hardware diagnostics require Windows.' }

$repository = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $repository '.tools/dotnet/dotnet.exe'
$hostOptions = @()
if (Test-Path -LiteralPath $dotnet) {
    # Runtime search options apply to published app hosts. The debug publication
    # is three directories below the repository, in artifacts/debug/app.
    $hostOptions = @('-property:AppHostRelativeDotNet=../../../.tools/dotnet')
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Install the SDK pinned in global.json, or extract it into .tools/dotnet.' }
    $dotnet = $command.Source
}
$sdkRoot = Split-Path $dotnet -Parent
# App hosts (including the DDC worker) must use the same runtime as the SDK.
# A pre-existing DOTNET_ROOT may point to an unrelated tool's private runtime.
$environment = @{
    DOTNET_ROOT = $sdkRoot
    DOTNET_ROOT_X64 = $sdkRoot
    DOTNET_ROOT_ARM64 = $sdkRoot
    DOTNET_CLI_HOME = (Join-Path $repository '.tools/dotnet-home')
    NUGET_PACKAGES = (Join-Path $repository '.tools/nuget')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
}
$previousEnvironment = @{}
Push-Location $repository
try {
    foreach ($name in $environment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    & $dotnet build MonitorSync.slnx -c Debug --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE) { throw 'Debug build failed. Check that the SDK pinned in global.json is installed.' }
    & $dotnet tests/MonitorSync.Tests/bin/Debug/net10.0/MonitorSync.Tests.dll
    if ($LASTEXITCODE) { throw 'Synchronization tests failed.' }

    $publish = Join-Path $repository 'artifacts/debug/app'
    # Pass app-host properties directly to MSBuild's publication target.
    & $dotnet msbuild src/MonitorSync.App/MonitorSync.App.csproj -target:Publish -property:Configuration=Debug `
        -property:NoBuild=true "-property:PublishDir=$publish/" @hostOptions -verbosity:minimal
    if ($LASTEXITCODE) { throw 'Debug application publication failed.' }
    & $dotnet msbuild src/MonitorSync.Worker/MonitorSync.Worker.csproj -target:Publish -property:Configuration=Debug `
        -property:NoBuild=true "-property:PublishDir=$publish/" @hostOptions -verbosity:minimal
    if ($LASTEXITCODE) { throw 'Debug worker publication failed.' }
    $app = Join-Path $publish 'MonitorSync.exe'
    if ($DiagnosticsOnly) {
        $reportDirectory = Join-Path $repository 'artifacts/debug'
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
        $report = Join-Path $reportDirectory ('diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
        $process = Start-Process -FilePath $app -ArgumentList @('--diagnostics', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
        try {
            if (-not $process.WaitForExit(30000)) {
                $process.Kill()
                throw 'Diagnostics exceeded 30 seconds. Check %LOCALAPPDATA%\MonitorSync\app.log.'
            }
            if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) {
                throw 'Diagnostics failed. Check %LOCALAPPDATA%\MonitorSync\app.log.'
            }
        }
        finally { $process.Dispose() }
        Write-Output "Read-only diagnostic report: $report"
    }
    else {
        Start-Process -FilePath $app -WindowStyle Hidden
        Write-Output 'MonitorSync is running in the tray. Use its Exit menu item to stop it.'
    }
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
