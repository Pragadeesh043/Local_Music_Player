[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Soundroom\Soundroom.csproj'
$output = Join-Path $PSScriptRoot "artifacts\$Configuration"
$localSdk = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_HOME = $PSScriptRoot
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

if (!$NoRestore) {
    & $dotnet restore $project
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
}
& $dotnet build $project -c $Configuration --no-restore -o $output
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
$localTools = Join-Path $PSScriptRoot '.tools\ffmpeg'
if (Test-Path -LiteralPath $localTools) {
    $mediaOutput = Join-Path $output 'Tools\FFmpeg'
    New-Item -ItemType Directory -Force $mediaOutput | Out-Null
    foreach ($file in 'ffmpeg.exe', 'ffprobe.exe') {
        Copy-Item -LiteralPath (Join-Path $localTools $file) -Destination $mediaOutput
    }
}
Write-Output (Join-Path $output 'Soundroom.exe')
