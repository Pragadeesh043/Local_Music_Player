[CmdletBinding()]
param([switch] $NoRestore)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Installer -NoRestore:$NoRestore
