param(
    [Parameter(Mandatory)][string] $Payload,
    [Parameter(Mandatory)][string] $Output
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Payload).Path
function Get-NsisPath([string] $Path) {
    [IO.Path]::GetRelativePath($root, $Path).Replace('$', '$$')
}

$commands = @(
    Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = Get-NsisPath $_.FullName
        if ($relative -ne 'MP4A.exe') {
            $installed = '"$INSTDIR\' + $relative + '"'
            "IfFileExists $installed 0 +2"
            "Delete $installed"
        }
    }
    'IfErrors uninstall_failed'
    Get-ChildItem -LiteralPath $root -Directory -Recurse -Force |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
            'RMDir "$INSTDIR\' + (Get-NsisPath $_.FullName) + '"'
        }
)
Set-Content -LiteralPath $Output -Value $commands -Encoding utf8
