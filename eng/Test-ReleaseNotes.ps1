#Requires -Version 7.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script = Join-Path $PSScriptRoot 'New-ReleaseNotes.ps1'
$fixture = Join-Path (Split-Path $PSScriptRoot -Parent) ('TestResults/release-notes/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force

function Invoke-Git([string[]]$Arguments) {
    & git @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git failed: $($Arguments -join ' ')" }
}

function Assert-Contains([string]$Text, [string]$Value) {
    if (!$Text.Contains($Value)) { throw "Release notes are missing: $Value" }
}

Push-Location $fixture
try {
    Invoke-Git @('init', '--quiet')
    Invoke-Git @('config', 'user.name', 'Release test')
    Invoke-Git @('config', 'user.email', 'release-test@example.invalid')
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>'
    Invoke-Git @('add', '.')
    Invoke-Git @('commit', '--quiet', '-m', 'Initial package')
    Invoke-Git @('commit', '--quiet', '--allow-empty', '-m', 'Prepare first release')
    Invoke-Git @('tag', '-a', 'v0.1.0', '-m', 'First release')
    & $script -Tag v0.1.0 -OutputPath first.md
    $first = Get-Content first.md -Raw
    Assert-Contains $first 'First release'
    Assert-Contains $first 'Initial package'
    Assert-Contains $first 'dotnet add package OceanBaseConnector --version 0.1.0'

    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.2.0-rc.1</Version></PropertyGroup></Project>'
    Invoke-Git @('add', 'Directory.Build.props')
    Invoke-Git @('commit', '--quiet', '-m', 'Fix protocol handling')
    Invoke-Git @('tag', 'checkpoint')
    Invoke-Git @('tag', '0.2.0-rc.1')
    & $script -Tag 0.2.0-rc.1 -OutputPath next.md
    $next = Get-Content next.md -Raw
    Assert-Contains $next 'Fix protocol handling'
    Assert-Contains $next 'compare/v0.1.0...0.2.0-rc.1'
    Assert-Contains $next 'OceanBaseConnector.DependencyInjection --version 0.2.0-rc.1'
    if ($next.Contains('Initial package')) { throw 'Previous release commits leaked into the new release.' }

    $rejected = $false
    try { & $script -Tag v9.9.9 -OutputPath invalid.md } catch { $rejected = $true }
    if (!$rejected -or (Test-Path invalid.md)) { throw 'A mismatched version must fail before writing notes.' }
    Invoke-Git @('commit', '--quiet', '--allow-empty', '-m', 'Unreleased change')
    $rejected = $false
    try { & $script -Tag 0.2.0-rc.1 -OutputPath wrong-commit.md } catch { $rejected = $true }
    if (!$rejected -or (Test-Path wrong-commit.md)) { throw 'A tag at a different commit must be rejected.' }
    Set-Content Directory.Build.props '<Project><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>'
    Invoke-Git @('add', 'Directory.Build.props')
    Invoke-Git @('commit', '--quiet', '-m', 'Promote stable release')
    Invoke-Git @('tag', 'v0.2.0')
    & $script -Tag v0.2.0 -OutputPath stable.md
    Assert-Contains (Get-Content stable.md -Raw) 'compare/0.2.0-rc.1...v0.2.0'
    Write-Host 'Release notes checks passed (first release, version ranges, prerelease, invalid version/commit).'
} finally {
    Pop-Location
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
