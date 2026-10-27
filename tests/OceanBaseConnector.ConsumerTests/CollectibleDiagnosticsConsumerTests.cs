using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class CollectibleDiagnosticsConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var exitThread in new[] { false, true }) yield return new object[] { target, exitThread };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetPreservesStaticsAfterDiagnosticContextUnloads(string target, bool exitThread)
        {
            var probe = Path.Combine(RepositoryLocator.FindRoot(), "tests", "OceanBaseConnector.CollectibleProbe", "bin", "Release", "net10.0", "OceanBaseConnector.CollectibleProbe.dll");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(probe);
            start.ArgumentList.Add(RepositoryLocator.GetProductAssemblyPath(target));
            start.ArgumentList.Add(exitThread ? "exit" : "control");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(budget.Token); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            var report = await output + await error;
            Assert.True(process.ExitCode == 0, "Collectible diagnostic probe exited " + process.ExitCode + ": " + report);
            Assert.Contains("Old context collected: True", report);
            Assert.Contains("Corrupted statics after GC: 0", report);
        }
    }
}
