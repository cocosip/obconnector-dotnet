#Requires -Version 7.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputPath = 'TestResults/packages/RELEASE_NOTES.md'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Git([string[]]$Arguments) {
    $result = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $($Arguments -join ' ')" }
    return $result
}

$root = Invoke-Git @('rev-parse', '--show-toplevel')
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if (!$version -or ($Tag -cne "v$version" -and $Tag -cne $version)) {
    throw "Tag '$Tag' must match the project version '$version' (with an optional v prefix)."
}
$commit = Invoke-Git @('rev-parse', '--verify', "refs/tags/$Tag^{commit}")
if ($commit -cne (Invoke-Git @('rev-parse', 'HEAD'))) { throw 'The release tag must point to HEAD.' }

$previous = $null
$parents = (Invoke-Git @('rev-list', '--parents', '-n', '1', $commit)).Split(' ')
if ($parents.Count -gt 1) {
    # Ignore unrelated tags and start at the parent so the current release is excluded.
    $previous = & git describe --tags --abbrev=0 --match 'v[0-9]*' --match '[0-9]*' $parents[1] 2>$null
    if ($LASTEXITCODE -ne 0) { $previous = $null }
}
$range = if ($previous) { "$previous..$Tag" } else { $Tag }
$commits = @(Invoke-Git @('log', $range, '--no-merges', '--format=- %h %s (%an)'))
$url = 'https://github.com/cocosip/obconnector-dotnet'
$comparison = if ($previous) {
    "Changes since [$previous]($url/releases/tag/$previous). [Full changelog]($url/compare/$previous...$Tag)."
} else {
    "First release. [Commit history]($url/commits/$Tag)."
}
$notes = @(
    "# OceanBaseConnector $version"
    ''
    '## Packages'
    ''
    '```shell'
    "dotnet add package OceanBaseConnector --version $version"
    "dotnet add package OceanBaseConnector.DependencyInjection --version $version"
    '```'
    ''
    '## Changes'
    ''
    $comparison
    ''
    "$($commits.Count) commits (excluding merges)."
    ''
    $commits
    ''
    "See [compatibility and known limitations]($url/blob/$Tag/docs/compatibility.md)."
)
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
[IO.File]::WriteAllText($destination, ($notes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Validated release $version; wrote $destination"
