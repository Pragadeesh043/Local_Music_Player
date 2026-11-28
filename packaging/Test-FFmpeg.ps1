[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Directory,
    [switch] $WriteBuildInfo
)

$ErrorActionPreference = 'Stop'
$folder = (Resolve-Path -LiteralPath $Directory).Path
$versions = @{}
foreach ($tool in 'ffmpeg', 'ffprobe') {
    $executable = Join-Path $folder "$tool.exe"
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "FFmpeg payload is missing $tool.exe."
    }
    $versions[$tool] = & $executable -version
    if ($LASTEXITCODE -ne 0) { throw "Bundled $tool could not start." }
}

$ffmpeg = Join-Path $folder 'ffmpeg.exe'
$decoders = & $ffmpeg -hide_banner -decoders
if ($LASTEXITCODE -ne 0) { throw 'Could not read the bundled FFmpeg decoder list.' }
if (!($decoders -match '^\s*A\S*\s+eac3\s')) {
    throw 'Bundled FFmpeg lacks the E-AC-3 (eac3) audio decoder. Use an E-AC-3-capable build or rebuild the release input with eac3 enabled.'
}
$demuxers = & $ffmpeg -hide_banner -demuxers
if ($LASTEXITCODE -ne 0) { throw 'Could not read the bundled FFmpeg demuxer list.' }
if (!($demuxers -match '^\s*D\s+mov(?:[,\s]|$)')) {
    throw 'Bundled FFmpeg lacks the MOV/MP4/M4A (mov) demuxer.'
}

if ($WriteBuildInfo) {
    foreach ($tool in 'ffmpeg', 'ffprobe') {
        $versions[$tool] | Set-Content -LiteralPath (Join-Path $folder "$tool-build.txt") -Encoding utf8
    }
}
