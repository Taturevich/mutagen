param(
    [ValidateSet('build', 'test', 'restore', 'format')]
    [string] $Operation
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskProject = Join-Path $taskRoot 'MutationFramework.slnx'
$forwarded = @($args)
if ($forwarded.Count -ge 2 -and $forwarded[0] -eq '--project') {
    $taskProject = $forwarded[1]
    $forwarded = @($forwarded | Select-Object -Skip 2)
}
if ($Operation -eq 'format' -and $forwarded.Count -gt 0 -and $forwarded[0] -in @('whitespace', 'style', 'analyzers')) {
    $formatCommand = $forwarded[0]
    $forwarded = @($forwarded | Select-Object -Skip 1)
    & dotnet format $formatCommand $taskProject @forwarded
} else {
    & dotnet $Operation $taskProject @forwarded
}
exit $LASTEXITCODE
