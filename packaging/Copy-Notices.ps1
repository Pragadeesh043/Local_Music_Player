param(
    [Parameter(Mandatory)][string] $Payload,
    [Parameter(Mandatory)][string] $UiDirectory,
    [Parameter(Mandatory)][string] $Project
)

$ErrorActionPreference = 'Stop'
$notices = Join-Path $Payload 'licenses'
$pattern = '^(licen[cs]e|copying|notice|third[-_. ]?party)(\b|_)'

function Copy-LicenseFiles([string] $Source, [string] $Destination) {
    $files = @(Get-ChildItem -LiteralPath $Source -File | Where-Object Name -match $pattern)
    if ($files.Count -eq 0) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath $files.FullName -Destination $Destination
}

$npmLock = Join-Path $UiDirectory 'package-lock.json'
$packagePaths = & node -e "for (const [path, entry] of Object.entries(require(process.argv[1]).packages)) { if (path && (!entry.dev || path === 'node_modules/tailwindcss')) console.log(path) }" $npmLock
if ($LASTEXITCODE -ne 0) { throw 'Could not read the npm dependency list.' }
foreach ($package in $packagePaths) {
    $path = $package.Substring('node_modules/'.Length)
    Copy-LicenseFiles (Join-Path $UiDirectory $package) (Join-Path $notices "npm/$path")
}

$assetsPath = Join-Path (Split-Path -Parent $Project) 'obj\project.assets.json'
$nuget = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
foreach ($package in $nuget.libraries.PSObject.Properties) {
    if ($package.Value.type -ne 'package') { continue }
    foreach ($folder in $nuget.packageFolders.PSObject.Properties.Name) {
        $directory = Join-Path $folder $package.Value.path
        if (!(Test-Path -LiteralPath $directory -PathType Container)) { continue }
        Copy-LicenseFiles $directory (Join-Path $notices "nuget/$($package.Value.path)")
        break
    }
}
