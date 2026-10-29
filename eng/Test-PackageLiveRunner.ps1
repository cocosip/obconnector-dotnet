#Requires -Version 7.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Exercise the real runner with a shorter deadline and a child that never reads stdin.
$repository = Split-Path $PSScriptRoot -Parent
$syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Verify-Packages.ps1'), [ref]$null, [ref]$null)
$definition = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-LiveConsumer' }, $true).Extent.Text
if ([regex]::Matches($definition, '\b180\b').Count -ne 1) { throw 'Expected one production child deadline.' }
$definition = [regex]::Replace($definition, '\b180\b', '1')
$scratch = Join-Path $repository ('TestResults/package-live-runner/' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $scratch)
$runner = Join-Path $scratch 'runner.ps1'
Set-Content -LiteralPath (Join-Path $scratch 'peer.ps1') -Value @'
[void][Console]::In.ReadToEnd()
foreach ($mode in @('MySql', 'Oracle')) {
    foreach ($execution in @('sync', 'async')) {
        foreach ($reading in @('buffered', 'sequential')) { [Console]::WriteLine("PASS live $mode $execution $reading cleanup") }
    }
}
[Console]::WriteLine('PASS live package scenarios=8')
'@
Set-Content -LiteralPath $runner -Value ($definition + @'

$liveProfiles = @(('x' * 262144), ('y' * 262144))
try {
    Invoke-LiveConsumer (Get-Process -Id $PID).Path @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 30')
    exit 2
} catch {
    if ($_.Exception.Message -cne 'Live package consumer exceeded its deadline; cleanup is unconfirmed.') { exit 3 }
    Write-Output 'PASS blocked stdin deadline'
}
$unexpected = @(Invoke-LiveConsumer (Get-Process -Id $PID).Path @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'peer.ps1')) 6>$null)
if ($unexpected.Count -ne 0) { exit 4 }
Write-Output 'PASS completed stdin output'
'@)
$start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $runner)) { $start.ArgumentList.Add($argument) }
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
try {
    $output = $process.StandardOutput.ReadToEndAsync(); $errors = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(10000)) { $process.Kill($true); throw 'FAIL blocked stdin escaped the child deadline.' }
    $lines = @($output.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_ -ne '' })
    if ($process.ExitCode -ne 0 -or ($lines -join '|') -cne 'PASS blocked stdin deadline|PASS completed stdin output' -or $errors.GetAwaiter().GetResult().Length -ne 0) {
        throw 'FAIL blocked stdin did not report the bounded sanitized failure.'
    }
    Write-Host 'PASS blocked stdin deadline'
    Write-Host 'PASS completed stdin output'
} finally { if (!$process.HasExited) { $process.Kill($true) }; $process.Dispose() }
