[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '\A(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})\z') {
    throw 'Version must be three integers such as 1.2.3, without v, leading zeros, or a prerelease suffix.'
}
$parsed = [version]::Parse($Version)
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) {
    throw 'MSI requires major and minor versions <= 255 and the third version number <= 65535.'
}
Write-Output $Version
