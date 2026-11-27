[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $MakeAppx,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$buildDirectory = Join-Path $output ('build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $buildDirectory 'payload'
$project = Join-Path $PSScriptRoot 'src\MP4A.Desktop\MP4A.Desktop.csproj'
$web = Join-Path $PSScriptRoot 'src\MP4A.Web'
$localSdk = Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_HOME = $PSScriptRoot
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

function Invoke-BuildTool([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $web
try {
    if (!$NoRestore -or !(Test-Path -LiteralPath 'node_modules\.bin\vite.cmd')) {
        Invoke-BuildTool npm.cmd @('ci')
    }
    Invoke-BuildTool npm.cmd @('run', 'build')
}
finally { Pop-Location }

$buildArguments = @('build', $project, '-c', 'Release', '-o', $payload, '-r', 'win-x64',
    '--self-contained', 'true', '-p:DebugType=embedded', '-p:RequireFFmpeg=true')
if ($NoRestore) { $buildArguments += '--no-restore' }
Invoke-BuildTool $dotnet $buildArguments

if (!$MakeAppx) {
    $MakeAppx = (Get-Command makeappx.exe -ErrorAction SilentlyContinue).Source
    if (!$MakeAppx) {
        $tools = Join-Path $env:NUGET_PACKAGES 'microsoft.windows.sdk.buildtools'
        $MakeAppx = Get-ChildItem -Path "$tools\*\bin\*\x64\makeappx.exe" -File |
            Sort-Object { [version]$_.Directory.Parent.Name }, { [version]$_.Directory.Parent.Parent.Parent.Name } -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if (!$MakeAppx) { throw 'Locate the Windows SDK x64 MakeAppx.exe and pass its path with -MakeAppx.' }

foreach ($file in 'MP4A.exe', 'MP4A.pri', 'App.xbf', 'UI\MainWindow.xbf', 'coreclr.dll',
    'phonon.dll', 'WebView2Loader.dll', 'Glassroom\index.html', 'Tools\FFmpeg\ffmpeg.exe', 'Tools\FFmpeg\ffprobe.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
        throw "Store payload is missing $file."
    }
}
foreach ($tool in 'ffmpeg', 'ffprobe') {
    $ffmpegDirectory = Join-Path $payload 'Tools\FFmpeg'
    Invoke-BuildTool (Join-Path $ffmpegDirectory "$tool.exe") @('-version') |
        Set-Content -LiteralPath (Join-Path $ffmpegDirectory "$tool-build.txt") -Encoding utf8
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
& (Join-Path $PSScriptRoot 'packaging\Copy-Notices.ps1') -Payload $payload -UiDirectory $web -Project $project

Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'src\MP4A.Desktop\Assets\MP4A.png'))
try {
    $logos = @{ 'StoreLogo.png' = 50; 'Square44x44Logo.png' = 44; 'Square150x150Logo.png' = 150 }
    foreach ($logo in $logos.GetEnumerator()) {
        $size = $logo.Value
        $scale = $size / [Math]::Max($source.Width, $source.Height)
        $width = [int]($source.Width * $scale)
        $height = [int]($source.Height * $scale)
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, [int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
            $bitmap.Save((Join-Path $payload "Assets\$($logo.Key)"), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $source.Dispose() }

$manifest = Join-Path $PSScriptRoot 'packaging\AppxManifest.xml'
Copy-Item -LiteralPath $manifest -Destination $payload
$version = ([xml](Get-Content -LiteralPath $manifest -Raw)).Package.Identity.Version
$packageName = "MP4A_${version}_x64.msix"
$stagedPackage = Join-Path $buildDirectory $packageName
Invoke-BuildTool $MakeAppx @('pack', '/d', $payload, '/p', $stagedPackage)
$package = Join-Path $output $packageName
Move-Item -LiteralPath $stagedPackage -Destination $package -Force
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $packageName" | Set-Content -LiteralPath "$package.sha256" -Encoding ascii
Write-Output $package
