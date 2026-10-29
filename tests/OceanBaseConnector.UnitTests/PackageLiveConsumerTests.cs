using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class PackageLiveConsumerTests
{
    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "not-a-connection-string-private-marker")]
    [InlineData(false, "Server=127.0.0.1;User ID=private-marker;TLS Mode=Disabled;ExpectedMode=Oracle")]
    [InlineData(true, "Server=127.0.0.1;User ID=private-marker;TLS Mode=Disabled;ExpectedMode=MySql")]
    [InlineData(false, "Server=127.0.0.1;User ID=private-marker;TLS Mode=Required")]
    [InlineData(true, "Server=127.0.0.1;User ID=private-marker;TLS Mode=Disabled;Connection Timeout=0")]
    [InlineData(false, "Server=127.0.0.1;User ID=private-marker;TLS Mode=Disabled;Command Timeout=0")]
    public void LivePackageProfilesRejectInvalidOrOutOfScopeSettingsBeforeOpen(bool oracle, string profile)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LiveScenarios.Verify(oracle, profile));
        Assert.Contains("profile", error.Message);
        Assert.DoesNotContain("private-marker", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LivePackageOpenFailureDoesNotExposeConnectionOrNestedErrors(bool oracle)
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var profile = "Server=127.0.0.1;Port=" + ((IPEndPoint)reserved.LocalEndPoint!).Port +
            ";User ID=private-marker;Password=private-password;Database=private-database;TLS Mode=Disabled;Connection Timeout=1;Command Timeout=1";
        var error = Assert.Throws<InvalidOperationException>(() => LiveScenarios.Verify(oracle, profile));
        Assert.Contains("open", error.Message);
        foreach (var secret in new[] { "private-marker", "private-password", "private-database", "127.0.0.1" })
            Assert.DoesNotContain(secret, error.ToString());
        Assert.Null(error.InnerException);
    }
}

public sealed partial class TextCommandTests
{
    public static IEnumerable<object[]> PackageFinalRows()
    {
        foreach (var asynchronous in new[] { false, true })
        foreach (var sequential in new[] { false, true })
        foreach (var fault in new[] { "none", "identity", "text" })
            yield return new object[] { asynchronous, sequential, fault };
    }

    [Theory]
    [MemberData(nameof(PackageFinalRows))]
    public async Task LivePackageFinalReadbackRejectsSameCountCorruption(bool asynchronous, bool sequential, string fault)
    {
        var server = MySqlProcedureServer(async channel =>
        {
            var request = await ReadClientCommand(channel, false);
            Assert.Equal(3, request[0]);
            var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
            if (sql == "SELECT COUNT(*) FROM OBPK_TEST")
            {
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("count", 8));
                await channel.WritePacketAsync(Eof(2)); await channel.WritePacketAsync(new byte[] { 1, 50 });
            }
            else
            {
                Assert.Equal("SELECT id,n,s,b FROM OBPK_TEST ORDER BY id", sql);
                await channel.WritePacketAsync(new byte[] { 4 });
                await channel.WritePacketAsync(Column("id", 8));
                await channel.WritePacketAsync(SchemaColumn("n", 246, 24, 20, 4));
                await channel.WritePacketAsync(Column("s", 253)); await channel.WritePacketAsync(Column("b", 252, 63));
                await channel.WritePacketAsync(Eof(2));
                byte[] Row(params byte[]?[] values)
                {
                    using var bytes = new MemoryStream();
                    foreach (var value in values)
                    {
                        if (value == null) bytes.WriteByte(251);
                        else { bytes.WriteByte((byte)value.Length); bytes.Write(value); }
                    }
                    return bytes.ToArray();
                }
                await channel.WritePacketAsync(Row(Encoding.ASCII.GetBytes("1"), Encoding.ASCII.GetBytes("1234567890123456.7890"),
                    Encoding.UTF8.GetBytes(fault == "text" ? "corrupted" : "中'\\?;value"), new byte[] { 0, 1, 255, 39, 92, 128 }));
                await channel.WritePacketAsync(Row(Encoding.ASCII.GetBytes(fault == "identity" ? "3" : "2"), null, null, null));
            }
            await channel.WritePacketAsync(Eof(2));
        });
        using var connection = new OceanBaseConnection(Configuration(server.Port));
        try
        {
            await connection.OpenAsync();
            Func<Task> read = () => LiveScenarios.ReadStoredRows(connection, "OBPK_TEST", false, asynchronous, sequential, false, CancellationToken.None);
            if (fault == "none") await read();
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(read);
                Assert.Equal(fault == "identity" ? "row identity" : "exact stored values", error.Message);
            }
        }
        finally { connection.Close(); server.Stop(); }
        try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
        // A verified corrupt row closes the unread response while the peer may still be writing it.
        catch (IOException error) when (fault != "none" && error.InnerException is SocketException socket &&
            (socket.SocketErrorCode == SocketError.ConnectionReset || socket.SocketErrorCode == SocketError.ConnectionAborted)) { }
    }
}
