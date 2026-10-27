using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal sealed record XaDecision
{
    public required string Table { get; init; }
    public required string GlobalId { get; init; }
    public required bool Commit { get; init; }
    internal OceanBaseXid Xid => new(0x4F4243, Convert.FromHexString(GlobalId), new byte[] { 1 });
    internal void Validate()
    {
        if (Table == null || !Regex.IsMatch(Table, "\\AOBA_[0-9A-F]{20}\\z") ||
            GlobalId == null || !Regex.IsMatch(GlobalId, "\\A[0-9A-F]{32}\\z"))
            throw new ArgumentException("Invalid owned XA identity.");
    }
}

internal sealed record XaCrashRequest
{
    public required string Action { get; init; }
    public required string ConnectionString { get; init; }
    public required string JournalPath { get; init; }
    public required bool Asynchronous { get; init; }
    public XaDecision? Decision { get; init; }
}

// Acceptance-only coordinator: secrets stay on stdin/in memory; only its decision survives death.
internal static class XaCrashWorker
{
    internal static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken token)
    {
        try
        {
            var text = new StringBuilder(); var character = new char[1];
            while (await input.ReadAsync(character.AsMemory(), token) != 0 && character[0] != '\n')
            {
                if (text.Length == 65536) throw new ArgumentException("Worker input limit exceeded.");
                text.Append(character[0]);
            }
            var request = Parse<XaCrashRequest>(text.ToString());
            if (request.Action is not ("prepare" or "recover") ||
                !Path.IsPathFullyQualified(request.JournalPath) || Path.GetFileName(request.JournalPath) != "decision.json")
                throw new ArgumentException("Invalid worker request.");
            XaDecision decision;
            if (request.Action == "prepare")
            {
                decision = request.Decision ?? throw new ArgumentException("A prepare decision is required.");
                decision.Validate();
            }
            else
            {
                if (request.Decision != null) throw new ArgumentException("Recovery must read the recorded decision.");
                decision = ReadDecision(request.JournalPath);
            }
            var builder = new OceanBaseConnectionStringBuilder(request.ConnectionString) { Pooling = false };
            if (builder.ConnectionTimeout <= 0 || builder.CommandTimeout <= 0 ||
                builder.ExpectedMode == OceanBaseCompatibilityMode.Oracle && !builder.UseOraclePrepareExecute)
                throw new ArgumentException("Worker requires finite timeouts and Oracle PrepareExecute.");
            await using var connection = new OceanBaseConnection(builder.ConnectionString);
            var asynchronous = request.Asynchronous;
            if (asynchronous) await connection.OpenAsync(token); else connection.Open();
            var resource = connection.GetXaResource(); var xid = decision.Xid;
            if (request.Action == "prepare")
            {
                if (asynchronous) await resource.StartAsync(xid, cancellationToken: token); else resource.Start(xid);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO " + decision.Table + " VALUES (1)";
                    if ((asynchronous ? await command.ExecuteNonQueryAsync(token) : command.ExecuteNonQuery()) != 1)
                        throw new AssertionFailure("Worker insert must affect exactly one row.");
                }
                if (asynchronous) await resource.EndAsync(xid, cancellationToken: token); else resource.End(xid);
                if ((asynchronous ? await resource.PrepareAsync(xid, token) : resource.Prepare(xid)) != OceanBaseXaVote.Prepared)
                    throw new AssertionFailure("Worker branch must vote prepared.");
                WriteDecision(request.JournalPath, decision);
                await output.WriteLineAsync("PREPARED"); await output.FlushAsync(token);
                // The supervisor kills this live process; no normal Close/Dispose substitutes for death.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                var recovered = asynchronous ? await resource.RecoverAsync(token) : resource.Recover();
                if (recovered.Count(item => item.Equals(xid)) != 1)
                    throw new AssertionFailure("Recovery must find exactly the recorded prepared branch.");
                await CompleteAsync(resource, decision, asynchronous, token);
                recovered = asynchronous ? await resource.RecoverAsync(token) : resource.Recover();
                if (recovered.Any(item => item.Equals(xid))) throw new AssertionFailure("Completed branch must leave recovery.");
                await output.WriteLineAsync("RECOVERED"); await output.FlushAsync(token);
            }
            return 0;
        }
        catch (Exception)
        {
            // Never echo exceptions, connection strings, SQL, worker input or journal paths.
            await output.WriteLineAsync("FAILED"); await output.FlushAsync(CancellationToken.None);
            return 1;
        }
    }

    internal static Task CompleteAsync(OceanBaseXaResource resource, XaDecision decision, bool asynchronous, CancellationToken token)
    {
        if (asynchronous) return decision.Commit ? resource.CommitAsync(decision.Xid, cancellationToken: token) : resource.RollbackAsync(decision.Xid, token);
        if (decision.Commit) resource.Commit(decision.Xid); else resource.Rollback(decision.Xid);
        return Task.CompletedTask;
    }

    internal static void WriteDecision(string path, XaDecision decision)
    {
        decision.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(decision);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(flushToDisk: true);
    }

    internal static XaDecision ReadDecision(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 4096) throw new ArgumentException("Invalid journal size.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new ArgumentException("Journal changed during read.");
        var decision = Parse<XaDecision>(new UTF8Encoding(false, true).GetString(bytes));
        decision.Validate(); return decision;
    }

    private static T Parse<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("Worker object is required.");
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate worker property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
