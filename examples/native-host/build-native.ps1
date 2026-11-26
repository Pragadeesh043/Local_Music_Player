[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $WebView2Root,
    [string] $BuildDirectory = (Join-Path $PSScriptRoot 'native/bin'),
    [string] $JsonIncludeDirectory
)

$ErrorActionPreference = 'Stop'
$version = '1.0.3179.45'

if (!$WebView2Root) {
    $repo = Resolve-Path (Join-Path $PSScriptRoot '../..')
    $cache = Join-Path $repo ".nuget/packages/microsoft.web.webview2/$version"
    if (Test-Path (Join-Path $cache 'build/native/include/WebView2.h')) {
        $WebView2Root = $cache
    } else {
        $WebView2Root = Join-Path $BuildDirectory "webview2/$version"
        if (!(Test-Path (Join-Path $WebView2Root 'build/native/include/WebView2.h'))) {
            $null = New-Item -ItemType Directory -Force -Path $BuildDirectory
            $archive = Join-Path $BuildDirectory "webview2-$version.zip"
            $url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg"
            Invoke-WebRequest -Uri $url -OutFile $archive
            Expand-Archive -LiteralPath $archive -DestinationPath $WebView2Root -Force
        }
    }
}

Push-Location (Join-Path $PSScriptRoot '../../src/MP4A.Web')
try {
    npm.cmd ci
    if ($LASTEXITCODE) { throw 'Installing the UI dependencies failed.' }
    npm.cmd run build
    if ($LASTEXITCODE) { throw 'Building the UI failed.' }
} finally {
    Pop-Location
}

$options = @(
    '-S', (Join-Path $PSScriptRoot 'native'),
    '-B', $BuildDirectory,
    '-A', 'x64',
    "-DWEBVIEW2_ROOT=$((Resolve-Path $WebView2Root).Path)"
)
if ($JsonIncludeDirectory) {
    $options += "-DNLOHMANN_INCLUDE_DIR=$((Resolve-Path $JsonIncludeDirectory).Path)"
}
cmake @options
if ($LASTEXITCODE) { throw 'Configuring the C++ host failed.' }
cmake --build $BuildDirectory --config $Configuration
if ($LASTEXITCODE) { throw 'Building the C++ host failed.' }

Write-Host "Launch $(Join-Path $BuildDirectory "$Configuration/MP4ADemo.exe")"
