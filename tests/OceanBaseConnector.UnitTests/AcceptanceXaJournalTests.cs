using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceXaJournalTests
{
    [Theory]
    [InlineData("nested-duplicate")]
    [InlineData("root-duplicate")]
    [InlineData("missing-commit")]
    [InlineData("unknown-member")]
    [InlineData("invalid-table")]
    [InlineData("invalid-global")]
    [InlineData("invalid-action")]
    [InlineData("recover-with-memory-decision")]
    public async Task InvalidWorkerInputCannotConnectWriteJournalOrEchoSecrets(string defect)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var directory = Directory.CreateTempSubdirectory("ob-xa-test-");
        var path = Path.Combine(directory.FullName, "decision.json");
        try
        {
            var request = new XaCrashRequest
            {
                Action = "prepare",
                JournalPath = path,
                Asynchronous = true,
                ConnectionString = "Server=127.0.0.1;Port=" + ((IPEndPoint)listener.LocalEndpoint).Port + ";User ID=synthetic;Password=synthetic-private;TLS Mode=Disabled;Connection Timeout=1;Command Timeout=1",
                Decision = new XaDecision { Table = "OBA_0123456789ABCDEF0123", GlobalId = "0123456789ABCDEF0123456789ABCDEF", Commit = true },
            };
            var json = JsonSerializer.Serialize(request);
            json = defect switch
            {
                "nested-duplicate" => json.Replace("\"Commit\":true", "\"Commit\":false,\"Commit\":true"),
                "root-duplicate" => json.Replace("\"Action\":\"prepare\"", "\"Action\":\"prepare\",\"Action\":\"prepare\""),
                "missing-commit" => json.Replace(",\"Commit\":true", ""),
                "unknown-member" => json.Replace("\"Commit\":true", "\"Commit\":true,\"Unexpected\":1"),
                "invalid-table" => json.Replace("OBA_0123456789ABCDEF0123", "OBA_X; DROP TABLE private"),
                "invalid-global" => json.Replace("0123456789ABCDEF0123456789ABCDEF", "not-hex"),
                "invalid-action" => json.Replace("\"prepare\"", "\"unexpected\""),
                _ => json.Replace("\"prepare\"", "\"recover\""),
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = new StringWriter();
            Assert.Equal(1, await XaCrashWorker.RunAsync(new StringReader(json + "\n"), output, deadline.Token));
            Assert.Equal("FAILED", output.ToString().TrimEnd());
            Assert.False(listener.Pending(), "Invalid worker input must be rejected before any TCP connection.");
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public void RecordedDecisionIsDurableOnlyDataAndCannotBeOverwritten()
    {
        var directory = Directory.CreateTempSubdirectory("ob-xa-test-"); var path = Path.Combine(directory.FullName, "decision.json");
        try
        {
            var decision = new XaDecision { Table = "OBA_0123456789ABCDEF0123", GlobalId = "0123456789ABCDEF0123456789ABCDEF", Commit = true };
            XaCrashWorker.WriteDecision(path, decision);
            Assert.Equal(decision, XaCrashWorker.ReadDecision(path));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(3, json.RootElement.GetPropertyCount());
            Assert.Throws<IOException>(() => XaCrashWorker.WriteDecision(path, decision with { Commit = false }));
            Assert.True(XaCrashWorker.ReadDecision(path).Commit);
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Table\":\"OBA_0123456789ABCDEF0123\",\"GlobalId\":\"0123456789ABCDEF0123456789ABCDEF\"}")]
    [InlineData("{\"Table\":\"OBA_0123456789ABCDEF0123\",\"GlobalId\":\"0123456789ABCDEF0123456789ABCDEF\",\"Commit\":false,\"Commit\":true}")]
    [InlineData("{\"Table\":\"OBA_0123456789ABCDEF0123\",\"GlobalId\":\"0123456789ABCDEF0123456789ABCDEF\",\"Commit\":true,\"Unexpected\":0}")]
    public void MalformedJournalCannotBecomeARecoveryDecision(string text)
    {
        var directory = Directory.CreateTempSubdirectory("ob-xa-test-"); var path = Path.Combine(directory.FullName, "decision.json");
        try { File.WriteAllText(path, text); Assert.NotNull(Record.Exception(() => XaCrashWorker.ReadDecision(path))); }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public async Task OversizedWorkerAndJournalInputsFailClosed()
    {
        var output = new StringWriter();
        Assert.Equal(1, await XaCrashWorker.RunAsync(new StringReader(new string('x', 65537)), output, CancellationToken.None));
        Assert.Equal("FAILED", output.ToString().TrimEnd());
        var directory = Directory.CreateTempSubdirectory("ob-xa-test-"); var path = Path.Combine(directory.FullName, "decision.json");
        try { File.WriteAllText(path, new string(' ', 4097)); Assert.Throws<ArgumentException>(() => XaCrashWorker.ReadDecision(path)); }
        finally { File.Delete(path); directory.Delete(); }
    }
}
