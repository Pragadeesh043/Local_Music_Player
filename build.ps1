[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [switch] $Installer,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MP4A.Desktop\MP4A.Desktop.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot "artifacts\$Configuration" }
$localSdk = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_HOME = $PSScriptRoot
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

$web = Join-Path $PSScriptRoot 'src\MP4A.Web'
Push-Location $web
try {
    if (!$NoRestore -or !(Test-Path -LiteralPath 'node_modules\.bin\vite.cmd')) {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw "Restoring web UI dependencies failed with exit code $LASTEXITCODE." }
    }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "Building the web UI failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

if ($Installer) {
    $localCompiler = Join-Path $PSScriptRoot '.tools\nsis\nsis-3.11\makensis.exe'
    $compiler = if (Test-Path -LiteralPath $localCompiler -PathType Leaf) { $localCompiler } else { (Get-Command makensis.exe).Source }
    $buildFolder = Join-Path $PSScriptRoot ('.verify\installer-' + [guid]::NewGuid().ToString('N'))
    $payload = Join-Path $buildFolder 'payload'
    $setup = Join-Path $PSScriptRoot 'artifacts\MP4A-Setup-x64.exe'
    $buildOptions = @('-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=embedded', '-p:RequireFFmpeg=true')
    if ($NoRestore) { $buildOptions += '--no-restore' }
    try {
        New-Item -ItemType Directory -Path $buildFolder -Force | Out-Null
        & $dotnet build $project -c $Configuration -o $payload @buildOptions
        if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
        foreach ($file in 'MP4A.exe', 'MP4A.pri', 'App.xbf', 'UI\MainWindow.xbf', 'Assets\MP4A.ico', 'coreclr.dll', 'phonon.dll',
            'WebView2Loader.dll', 'Native\SteamAudio-LICENSE.md', 'Native\SteamAudio-THIRDPARTY.md', 'Glassroom\index.html',
            'Tools\FFmpeg\ffmpeg.exe', 'Tools\FFmpeg\ffprobe.exe') {
            if (!(Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
                throw "Installer payload is missing $file."
            }
        }
        $ffmpegOutput = Join-Path $payload 'Tools\FFmpeg'
        foreach ($tool in 'ffmpeg', 'ffprobe') {
            $version = & (Join-Path $ffmpegOutput "$tool.exe") -version
            if ($LASTEXITCODE -ne 0) { throw "Bundled $tool could not start." }
            $version | Set-Content -LiteralPath (Join-Path $ffmpegOutput "$tool-build.txt") -Encoding utf8
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE'),
            (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $payload -Recurse
        & (Join-Path $PSScriptRoot 'packaging\Copy-Notices.ps1') -Payload $payload -UiDirectory $web -Project $project
        $installerScript = Join-Path $PSScriptRoot 'packaging\MP4A.nsi'
        $temporarySetup = Join-Path $buildFolder 'MP4A-Setup-x64.exe'
        $uninstallFiles = Join-Path $buildFolder 'UninstallFiles.nsh'
        & (Join-Path $PSScriptRoot 'packaging\Write-UninstallFiles.ps1') -Payload $payload -Output $uninstallFiles
        $appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'MP4A.exe')).FileVersion
        & $compiler "/DAPP_PAYLOAD=$payload" "/DAPP_VERSION=$appVersion" "/DUNINSTALL_FILES=$uninstallFiles" "/DSETUP_FILE=$temporarySetup" $installerScript
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
$sacd = Join-Path $PSScriptRoot '.tools\sacd\sacd_extract.exe'
if (Test-Path -LiteralPath $sacd -PathType Leaf) {
    $sacdOutput = Join-Path $output 'Tools\SACD'
    New-Item -ItemType Directory -Force $sacdOutput | Out-Null
    Copy-Item -LiteralPath $sacd -Destination $sacdOutput
}
Write-Output (Join-Path $output 'MP4A.exe')
