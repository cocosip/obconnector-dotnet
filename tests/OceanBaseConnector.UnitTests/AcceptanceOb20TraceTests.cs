using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceOb20TraceTests
{
    [Theory]
    [InlineData("ob20")]
    [InlineData("trace-legacy")]
    [InlineData("trace-modern")]
    [InlineData("declined-ob20")]
    [InlineData("declined-trace")]
    public async Task Ob20TraceProfilesSelectOnlyTheirTwoCasesAndDefaultSkips(string profile)
    {
        var cases = DatabaseCases.All().Where(c => c.Name.StartsWith("matrix.ob20-trace-negotiation.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(10, cases.Length);
        Assert.All(cases, c => { Assert.Null(c.DeferredReason); Assert.False(c.RequiresObjects); });
        var config = AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionString\":\"Server=unused;User ID=synthetic\"}");
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.Equal(10, report.Skipped); Assert.Equal(0, report.Passed);
        config = AcceptanceConfig.Parse("{\"mode\":\"MySql\",\"connectionString\":\"unused\",\"ob20TraceProfile\":\"" + profile + "\"}");
        var builder = new OceanBaseConnectionStringBuilder("Server=unused;User ID=synthetic;Use OB20=true;Enable Full Link Trace=" + (profile != "ob20"));
        var selected = cases.Where(c => AcceptanceSuite.SkipReason(config, builder, c) == null).ToArray();
        Assert.Equal(new[] { "matrix.ob20-trace-negotiation." + profile + ".sync", "matrix.ob20-trace-negotiation." + profile + ".async" }, selected.Select(c => c.Name));
        config.ConnectionString = builder.ConnectionString;
        Assert.NotEmpty(config.ResolveConnectionString(_ => null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("Trace-Modern")]
    public void Ob20TraceProfileRejectsUnknownNames(string profile)
        => Assert.Throws<ArgumentException>(() => AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionString\":\"unused\",\"ob20TraceProfile\":\"" + profile + "\"}"));

    [Theory]
    [InlineData("ob20", "Use OB20=false")]
    [InlineData("ob20", "Use OB20=true;Enable Full Link Trace=true")]
    [InlineData("trace-modern", "Use OB20=true;Enable Full Link Trace=false")]
    [InlineData("declined-trace", "Use Compression=true")]
    public void Ob20TraceProfileRejectsConflictingOptionsBeforeNetworking(string profile, string options)
    {
        var config = AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionString\":\"Server=unused;User ID=synthetic;" + options + "\",\"ob20TraceProfile\":\"" + profile + "\"}");
        Assert.Throws<ArgumentException>(() => config.ResolveConnectionString(_ => null));
    }

    [Theory]
    [InlineData("ob20")]
    [InlineData("trace-legacy")]
    [InlineData("trace-modern")]
    [InlineData("declined-ob20")]
    [InlineData("declined-trace")]
    public void Ob20TraceCaptureRequiresSelectedNegotiationAndCommandFrames(string profile)
    {
        using var capture = new Ob20TraceCapture(profile);
        using var listener = new DiagnosticListener("OceanBaseConnector.Protocol");
        var fields = Negotiation(profile);
        listener.Write("OceanBaseConnector.Protocol.Negotiated", fields);
        SendFrame(listener, profile, "read"); SendFrame(listener, profile, "write");
        Assert.Throws<AssertionFailure>(capture.Verify); // Handshake frames cannot prove command transport.
        capture.BeginQueries();
        SendFrame(listener, profile, "read"); SendFrame(listener, profile, "write");
        capture.Verify();
    }

    [Theory]
    [InlineData("missing-negotiation")]
    [InlineData("duplicate")]
    [InlineData("wrong-channel")]
    [InlineData("wrong-listener")]
    [InlineData("missing-read")]
    [InlineData("missing-write")]
    [InlineData("missing-extra")]
    [InlineData("wrong-modern")]
    [InlineData("wrong-scalar")]
    [InlineData("malformed-frame")]
    public void Ob20TraceCaptureRejectsFalsePositiveEvidence(string kind)
    {
        using var capture = new Ob20TraceCapture("trace-modern");
        using var first = new DiagnosticListener("OceanBaseConnector.Protocol");
        using var second = new DiagnosticListener("OceanBaseConnector.Protocol");
        var fields = Negotiation("trace-modern");
        if (kind == "wrong-scalar") fields["FullLinkTraceEnabled"] = "true";
        if (kind != "missing-negotiation") first.Write("OceanBaseConnector.Protocol.Negotiated", fields);
        if (kind == "duplicate") first.Write("OceanBaseConnector.Protocol.Negotiated", fields);
        capture.BeginQueries();
        var listener = kind == "wrong-listener" ? second : first;
        if (kind != "missing-read") SendFrame(listener, "trace-modern", "read");
        if (kind != "missing-write")
        {
            var frame = Frame("trace-modern", "write");
            if (kind == "wrong-channel") frame["ChannelId"] = 8L;
            if (kind == "missing-extra") frame["Flags"] = 10u;
            if (kind == "wrong-modern") frame["Flags"] = 3u;
            if (kind == "malformed-frame") frame["Flags"] = 11;
            listener.Write("OceanBaseConnector.Protocol.Frame", frame);
        }
        Assert.Throws<AssertionFailure>(capture.Verify);
    }

    [Fact]
    public async Task Ob20TraceCaptureIgnoresUnrelatedLogicalFlowsAndDisposes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new DiagnosticListener("OceanBaseConnector.Protocol");
        var unrelated = Task.Run(async () =>
        {
            await release.Task;
            listener.Write("OceanBaseConnector.Protocol.Negotiated", new object());
        });
        var capture = new Ob20TraceCapture("ob20");
        using (capture)
        {
            release.SetResult(); await unrelated;
            listener.Write("OceanBaseConnector.Protocol.Negotiated", Negotiation("ob20"));
            capture.BeginQueries(); SendFrame(listener, "ob20", "read"); SendFrame(listener, "ob20", "write");
            capture.Verify();
        }
        listener.Write("OceanBaseConnector.Protocol.Negotiated", new object());
        capture.Verify();
    }

    private static Dictionary<string, object?> Negotiation(string profile) => new()
    {
        ["ChannelId"] = 7L,
        ["Layer"] = profile == "declined-ob20" ? "mysql" : "ob20",
        ["RequestedLayer"] = "ob20",
        ["FullLinkTraceRequested"] = profile != "ob20",
        ["FullLinkTraceEnabled"] = profile is "trace-legacy" or "trace-modern",
        ["ModernTraceFormat"] = profile == "trace-modern",
    };
    private static Dictionary<string, object?> Frame(string profile, string direction) => new()
    {
        ["ChannelId"] = 7L,
        ["Layer"] = profile == "declined-ob20" ? "mysql" : "ob20",
        ["Direction"] = direction,
        ["Flags"] = profile == "trace-modern" ? 11u : profile == "trace-legacy" ? 3u : 2u,
    };
    private static void SendFrame(DiagnosticListener listener, string profile, string direction)
        => listener.Write("OceanBaseConnector.Protocol.Frame", Frame(profile, direction));
}
