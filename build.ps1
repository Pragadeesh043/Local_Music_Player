[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [switch] $Installer,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Soundroom\Soundroom.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot "artifacts\$Configuration" }
$localSdk = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_HOME = $PSScriptRoot
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$glassroom = Join-Path $PSScriptRoot 'src\Glassroom\ui'
Push-Location $glassroom
try {
    if (!$NoRestore -or !(Test-Path -LiteralPath 'node_modules\.bin\vite.cmd')) {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw "Restoring Glassroom dependencies failed with exit code $LASTEXITCODE." }
    }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "Building Glassroom failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

if ($Installer) {
    $localCompiler = Join-Path $PSScriptRoot '.tools\nsis\nsis-3.11\makensis.exe'
    $compiler = if (Test-Path -LiteralPath $localCompiler -PathType Leaf) { $localCompiler } else { (Get-Command makensis.exe).Source }
    $buildFolder = Join-Path $PSScriptRoot ('.verify\installer-' + [guid]::NewGuid().ToString('N'))
    $payload = Join-Path $buildFolder 'payload'
    $setup = Join-Path $PSScriptRoot 'artifacts\MP4A-Setup-x64.exe'
    $buildOptions = @('-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=embedded')
    if ($NoRestore) { $buildOptions += '--no-restore' }
    try {
        New-Item -ItemType Directory -Path $buildFolder -Force | Out-Null
        & $dotnet build $project -c $Configuration -o $payload @buildOptions
        if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
        foreach ($file in 'MP4A.exe', 'MP4A.pri', 'App.xbf', 'UI\MainWindow.xbf', 'coreclr.dll', 'phonon.dll', 'Glassroom\index.html') {
            if (!(Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
                throw "Installer payload is missing $file."
            }
        }
        $mediaOutput = Join-Path $payload 'Tools\FFmpeg'
        New-Item -ItemType Directory -Path $mediaOutput -Force | Out-Null
        foreach ($file in 'ffmpeg.exe', 'ffprobe.exe') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ".tools\ffmpeg\$file") -Destination $mediaOutput
        }
        $installerScript = Join-Path $PSScriptRoot 'packaging\MP4A.nsi'
        $temporarySetup = Join-Path $buildFolder 'MP4A-Setup-x64.exe'
        & $compiler "/DAPP_PAYLOAD=$payload" "/DSETUP_FILE=$temporarySetup" $installerScript
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed with exit code $LASTEXITCODE." }
        New-Item -ItemType Directory -Path (Split-Path -Parent $setup) -Force | Out-Null
        Move-Item -LiteralPath $temporarySetup -Destination $setup -Force
        Write-Output $setup
    }
    finally {
        if (Test-Path -LiteralPath $buildFolder) {
            $resolved = (Resolve-Path -LiteralPath $buildFolder).Path
            $parent = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '.verify')).Path
            if ((Split-Path -Parent $resolved) -eq $parent -and (Split-Path -Leaf $resolved) -match '^installer-[a-f0-9]{32}$') {
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
    return
}

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
Write-Output (Join-Path $output 'MP4A.exe')
