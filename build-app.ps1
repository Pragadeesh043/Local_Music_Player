[CmdletBinding()]
param(
    [switch] $NoRestore,
    [string] $FFmpegDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Installer -NoRestore:$NoRestore -FFmpegDirectory $FFmpegDirectory
