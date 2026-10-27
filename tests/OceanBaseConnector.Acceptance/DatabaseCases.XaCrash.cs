using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddXaCrash(System.Collections.Generic.List<AcceptanceCase> cases)
    {
        foreach (var asynchronous in new[] { false, true })
            foreach (var commit in new[] { false, true })
                cases.Add(new("matrix.xa-crash-recovery." + (commit ? "commit." : "rollback.") + (asynchronous ? "async" : "sync"),
                    "xa", (c, t) => XaCrash(c, asynchronous, commit, t), RequiresObjects: true, RequiresXaCrashProfile: true));
    }

    private static async Task XaCrash(CaseContext c, bool asynchronous, bool commit, CancellationToken token)
    {
        await using var observer = await c.OpenAsync(token);
        var table = await c.TableAsync(observer, "id INTEGER PRIMARY KEY", token);
        var decision = new XaDecision { Table = table, GlobalId = Guid.NewGuid().ToString("N").ToUpperInvariant(), Commit = commit };
        var directory = Directory.CreateTempSubdirectory("ob-xa-" + decision.GlobalId + "-");
        var path = Path.Combine(directory.FullName, "decision.json");
        var resolved = false;
        c.RegisterCleanup("XA_JOURNAL", decision.GlobalId, () =>
        {
            if (!resolved) throw new AssertionFailure("Unresolved XA branch requires its retained local decision journal.");
            File.Delete(path); directory.Delete(); return Task.CompletedTask;
        });
        c.RegisterCleanup("XA", decision.GlobalId, async () =>
        {
            await using var connection = await c.OpenAsync(CancellationToken.None);
            var resource = connection.GetXaResource();
            if ((await resource.RecoverAsync()).Any(item => item.Equals(decision.Xid)))
            {
                var recorded = File.Exists(path) ? XaCrashWorker.ReadDecision(path) : decision with { Commit = false };
                c.Check(recorded.Table == decision.Table && recorded.GlobalId == decision.GlobalId, "Cleanup journal must identify only this owned branch.");
                await XaCrashWorker.CompleteAsync(resource, recorded, true, CancellationToken.None);
                c.Check(!(await resource.RecoverAsync()).Any(item => item.Equals(decision.Xid)), "Cleanup must resolve its owned prepared branch.");
            }
            resolved = true;
        });
        using var configuration = c.CreateConnection();
        var connectionString = configuration.ConnectionString;
        await RunXaWorker(new XaCrashRequest
        {
            Action = "prepare",
            ConnectionString = connectionString,
            JournalPath = path,
            Asynchronous = asynchronous,
            Decision = decision
        }, crash: true, token);
        c.Check(await c.CountAsync(observer, table, token) == 0, "Prepared write must remain invisible after coordinator death.");
        await RunXaWorker(new XaCrashRequest
        {
            Action = "recover",
            ConnectionString = connectionString,
            JournalPath = path,
            Asynchronous = asynchronous
        }, crash: false, token);
        c.Check(await c.CountAsync(observer, table, token) == (commit ? 1 : 0), "Restarted coordinator must make its recorded decision durable.");
        await using var recovery = await c.OpenAsync(token);
        c.Check(!(await recovery.GetXaResource().RecoverAsync(token)).Any(item => item.Equals(decision.Xid)), "Finished branch must be absent on an independent recovery session.");
        resolved = true;
    }

    private static async Task RunXaWorker(XaCrashRequest request, bool crash, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        process.StartInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--xa-crash-worker");
        var started = false; Task? stderr = null; Exception? failure = null;
        try
        {
            started = process.Start();
            if (!started) throw new AssertionFailure("XA worker must start.");
            stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            var marker = await process.StandardOutput.ReadLineAsync(deadline.Token);
            if (marker != (crash ? "PREPARED" : "RECOVERED")) throw new AssertionFailure("XA worker did not confirm its required phase.");
            if (crash)
            {
                if (process.HasExited) throw new AssertionFailure("Prepared XA worker must remain alive until forced termination.");
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(deadline.Token);
            if (crash ? process.ExitCode == 0 : process.ExitCode != 0) throw new AssertionFailure("XA worker exit must match its forced-death/recovery phase.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                if (started)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (stderr != null) await stderr.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception error) { if (failure != null) throw new AggregateException(failure, error); throw; }
        }
    }
}
