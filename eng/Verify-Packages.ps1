#Requires -Version 7.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$VerifyNetFramework
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($VerifyNetFramework -and !$IsWindows) { throw '-VerifyNetFramework requires Windows and the .NET Framework 4.8 targeting pack.' }
$repository = Split-Path $PSScriptRoot -Parent
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
[xml]$rootProperties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$version = [string]$rootProperties.Project.PropertyGroup.Version
$targets = @('netstandard2.0', 'netstandard2.1', 'net8.0', 'net9.0', 'net10.0')
$packages = @('OceanBaseConnector', 'OceanBaseConnector.DependencyInjection')
$hashes = @{}

function Assert-Package($condition, [string]$message) {
    if (!$condition) { throw $message }
}

function Invoke-Dotnet([string[]]$arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($arguments -join ' ')" }
}

foreach ($id in $packages) {
    $packagePath = Join-Path $feed "$id.$version.nupkg"
    $hashes[$id] = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($packagePath)))
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $manifest = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        Assert-Package ($manifest.Count -eq 1) "$id must have exactly one nuspec."
        $reader = [IO.StreamReader]::new($manifest[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        Assert-Package ($metadata.id -eq $id -and $metadata.version -eq $version) "$id identity/version mismatch."
        $readme = $metadata.SelectSingleNode('*[local-name()="readme"]')
        Assert-Package ($null -ne $readme -and $readme.InnerText -eq 'README.md') "$id must declare README.md."
        foreach ($document in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
            $entry = $archive.GetEntry($document)
            Assert-Package ($null -ne $entry -and $entry.Length -gt 0) "$id is missing $document."
        }
        Assert-Package ($metadata.repository.type -eq 'git' -and $metadata.repository.url -eq 'https://github.com/cocosip/obconnector-dotnet') "$id repository metadata mismatch."
        $packageTargets = if ($id -eq $packages[0]) { $targets } else { $targets[2..4] }
        $actualAssets = @($archive.Entries.FullName | Where-Object { $_ -like 'lib/*/*.dll' })
        $expectedAssets = @($packageTargets | ForEach-Object { "lib/$_/$id.dll" })
        Assert-Package (@(Compare-Object $actualAssets $expectedAssets).Count -eq 0) "$id has unexpected lib assets."
        $groups = @($metadata.dependencies.group)
        Assert-Package ($groups.Count -eq $packageTargets.Count) "$id dependency group count mismatch."
        foreach ($target in $packageTargets) {
            $nuspecTarget = $target.Replace('netstandard', '.NETStandard')
            $group = @($groups | Where-Object targetFramework -eq $nuspecTarget)
            Assert-Package ($group.Count -eq 1) "$id is missing dependency group $target."
            $dependencies = @($group[0].dependency)
            $major = if ($target -like 'netstandard*') { 8 } else { [int]$target.Substring(3).Split('.')[0] }
            $extensions = @($dependencies | Where-Object id -Like 'Microsoft.Extensions.*')
            Assert-Package ($extensions.Count -gt 0) "$id/$target is missing Microsoft.Extensions dependencies."
            foreach ($dependency in $extensions) {
                Assert-Package ($dependency.version -match "^\[?$major\.") "$id/$target has wrong Microsoft.Extensions major."
            }
            if ($id -eq $packages[1]) {
                $core = @($dependencies | Where-Object id -eq $packages[0])
                Assert-Package ($core.Count -eq 1 -and $core[0].version -eq "[$version]") 'DI must pin the exact matching core version.'
            } else {
                $diagnostics = @($dependencies | Where-Object id -eq 'System.Diagnostics.DiagnosticSource')
                if ($target -like 'netstandard*') {
                    Assert-Package ($diagnostics.Count -eq 1 -and $diagnostics[0].version -eq '8.0.1') "$target must reference DiagnosticSource 8.0.1."
                } else {
                    Assert-Package ($diagnostics.Count -eq 0) "$target must use runtime DiagnosticSource."
                }
            }
        }
        Write-Host "Verified $id $version package contents and dependency groups."
    } finally { $archive.Dispose() }
}

# A new cache prevents an older, same-version local package from satisfying this run.
$run = Join-Path $repository ('TestResults/package-validation/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-ConsumerFile([string]$name, [string]$content) {
    [IO.File]::WriteAllText((Join-Path $run $name), $content.Replace("`r`n", "`n").Replace("`n", "`r`n"), $utf8)
}

$sources = [Collections.Generic.List[string]]::new()
$sources.Add($feed)
# Existing dependency caches can serve as offline feeds; the product IDs map only to this run's feed.
foreach ($project in @('src/OceanBaseConnector', 'src/OceanBaseConnector.DependencyInjection')) {
    $assets = Get-Content -LiteralPath (Join-Path $repository "$project/obj/project.assets.json") -Raw | ConvertFrom-Json
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        if ((Test-Path -LiteralPath $folder) -and !$sources.Contains($folder)) { $sources.Add($folder) }
    }
}
$sources.Add('https://api.nuget.org/v3/index.json')
$sourceXml = for ($i = 0; $i -lt $sources.Count; $i++) {
    '<add key="source' + $i + '" value="' + [Security.SecurityElement]::Escape($sources[$i]) + '" />'
}
$mappingXml = for ($i = 1; $i -lt $sources.Count; $i++) {
    '<packageSource key="source' + $i + '"><package pattern="*" /></packageSource>'
}
Write-ConsumerFile 'NuGet.Config' @"
<configuration>
  <packageSources><clear />$($sourceXml -join '')</packageSources>
  <packageSourceMapping><clear />
    <packageSource key="source0"><package pattern="OceanBaseConnector" /><package pattern="OceanBaseConnector.DependencyInjection" /></packageSource>
    $($mappingXml -join '')
  </packageSourceMapping>
</configuration>
"@
Write-ConsumerFile 'Directory.Build.props' @'
<Project />
'@
Write-ConsumerFile 'Directory.Packages.props' @"
<Project>
  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="OceanBaseConnector" Version="[$version]" />
    <PackageVersion Include="OceanBaseConnector.DependencyInjection" Version="[$version]" />
  </ItemGroup>
</Project>
"@
Write-ConsumerFile 'PackageConsumer.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0</TargetFrameworks>
    <OutputType Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0' Or '$(TargetFramework)' == 'net10.0'">Exe</OutputType>
    <LangVersion>latest</LangVersion><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><CheckEolTargetFramework>false</CheckEolTargetFramework>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="framework-host/**/*.cs" />
    <PackageReference Include="OceanBaseConnector" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0' Or '$(TargetFramework)' == 'net9.0' Or '$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="OceanBaseConnector.DependencyInjection" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
'@
Copy-Item -LiteralPath (Join-Path $repository 'tests/PackageConsumer/Program.cs') -Destination $run
$projectPath = Join-Path $run 'PackageConsumer.csproj'
Invoke-Dotnet @('restore', $projectPath, '--configfile', (Join-Path $run 'NuGet.Config'), '--packages', (Join-Path $run 'packages'))
$restored = Get-Content -LiteralPath (Join-Path $run 'obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($target in $targets) {
    $graph = $restored.targets.$target
    foreach ($id in $packages) {
        if ($target -like 'netstandard*' -and $id -eq $packages[1]) { continue }
        $key = "$id/$version"
        $library = $restored.libraries.$key
        Assert-Package ($library.type -eq 'package' -and $library.sha512 -ceq $hashes[$id]) "$target did not restore this run's $id package."
        $selected = @($graph.$key.compile.PSObject.Properties.Name)
        Assert-Package ($selected.Count -eq 1 -and $selected[0] -eq "lib/$target/$id.dll") "$target selected an incorrect $id asset."
    }
    $major = if ($target -like 'netstandard*') { 8 } else { [int]$target.Substring(3).Split('.')[0] }
    foreach ($dependency in $graph.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.Extensions.*' }) {
        Assert-Package ($dependency.Split('/')[1].StartsWith("$major.")) "$target resolved an incompatible $dependency."
    }
}
Invoke-Dotnet @('build', $projectPath, '-c', 'Release', '--no-restore')
foreach ($target in $targets[2..4]) {
    Invoke-Dotnet @((Join-Path $run "bin/Release/$target/PackageConsumer.dll"))
}
foreach ($target in $targets[0..1]) {
    Invoke-Dotnet @((Join-Path $run 'bin/Release/net10.0/PackageConsumer.dll'), (Join-Path $run "bin/Release/$target/PackageConsumer.dll"))
}
if ($VerifyNetFramework) {
    $frameworkDirectory = Join-Path $run 'framework-host'
    $null = New-Item -ItemType Directory -Path $frameworkDirectory
    Write-ConsumerFile 'framework-host/FrameworkHost.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework><OutputType>Exe</OutputType>
    <LangVersion>latest</LangVersion><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OceanBaseConnector" />
    <Reference Include="PackageConsumer"><HintPath>../bin/Release/netstandard2.0/PackageConsumer.dll</HintPath></Reference>
  </ItemGroup>
</Project>
'@
    Write-ConsumerFile 'framework-host/Program.cs' @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class Program
{
    private static void Main()
    {
        if (!RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal))
            throw new InvalidOperationException("The sample must run on the desktop CLR.");
        Console.WriteLine(PackageConsumer.Verify());
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        Console.WriteLine("Host=" + RuntimeInformation.FrameworkDescription + " Release=" + key?.GetValue("Release"));
    }
}
'@
    $frameworkProject = Join-Path $frameworkDirectory 'FrameworkHost.csproj'
    Invoke-Dotnet @('restore', $frameworkProject, '--configfile', (Join-Path $run 'NuGet.Config'), '--packages', (Join-Path $run 'packages'))
    $frameworkAssets = Get-Content -LiteralPath (Join-Path $frameworkDirectory 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $coreKey = "$($packages[0])/$version"
    $frameworkGraph = $frameworkAssets.targets.net48
    $coreLibrary = $frameworkAssets.libraries.$coreKey
    Assert-Package ($coreLibrary.type -eq 'package' -and $coreLibrary.sha512 -ceq $hashes[$packages[0]]) "Framework host did not restore this run's core package."
    $selected = @($frameworkGraph.$coreKey.compile.PSObject.Properties.Name)
    Assert-Package ($selected.Count -eq 1 -and $selected[0] -eq 'lib/netstandard2.0/OceanBaseConnector.dll') 'Framework host must select the netstandard2.0 asset.'
    foreach ($dependency in $frameworkGraph.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.Extensions.*' }) {
        Assert-Package ($dependency.Split('/')[1].StartsWith('8.')) "Framework host resolved an incompatible $dependency."
    }
    Invoke-Dotnet @('build', $frameworkProject, '-c', 'Release', '--no-restore')
    & (Join-Path $frameworkDirectory 'bin/Release/net48/FrameworkHost.exe')
    if ($LASTEXITCODE -ne 0) { throw "Framework package consumer failed ($LASTEXITCODE)." }
}
Write-Host "Package verification passed. Retained consumers, packages and restore graphs: $run"
