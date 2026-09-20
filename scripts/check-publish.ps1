[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publish = (Resolve-Path -LiteralPath $Path).Path

foreach ($name in @('MonitorSync', 'MonitorSync.Worker')) {
    $config = Get-Content -LiteralPath (Join-Path $publish "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) { throw "$name must bundle its runtime." }
}

# Core's WindowsBase facade has a different assembly identity from the WPF
# implementation, even though their filenames and file versions are identical.
$windowsBase = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'WindowsBase.dll'))
$token = -join ($windowsBase.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') })
if ($token -ne '31bf3856ad364e35' -or $windowsBase.Version.Major -ne 10) {
    throw "The package contains the wrong WindowsBase.dll: $($windowsBase.FullName)"
}

$hostArchitecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($Runtime -eq 'win-arm64' -and $hostArchitecture -ne 'ARM64') {
    Write-Warning 'ARM64 payload identity checked; run check-publish.ps1 on ARM64 to test startup.'
    return
}

$process = Start-Process -FilePath (Join-Path $publish 'MonitorSync.exe') -ArgumentList '--smoke-test' `
    -WorkingDirectory $publish -WindowStyle Hidden -PassThru
try {
    $null = $process.Handle
    if (-not $process.WaitForExit(20000)) {
        $process.Kill()
        $process.WaitForExit()
        throw 'Published app startup check timed out.'
    }
    if ($process.ExitCode -ne 0) { throw "Published app startup check failed (exit $($process.ExitCode))." }
}
finally { $process.Dispose() }
Write-Host 'Published package passed: bundled WPF runtime, tray resources, and worker IPC.'
