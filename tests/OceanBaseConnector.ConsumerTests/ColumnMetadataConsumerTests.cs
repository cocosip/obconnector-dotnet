using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests;

public sealed class ColumnMetadataConsumerTests : Ob20TestSupport
{
    public static IEnumerable<object[]> Assets()
    {
        foreach (var target in ConsumerTargetMatrix.Representative)
            foreach (var asynchronous in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { false } : new[] { false, true })
                foreach (var mysql in new[] { false, true }) yield return new object[] { target, asynchronous, mysql };
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public async Task PublicColumnsPreserveTypedGenerationMetadataAcrossAssets(string target, bool asynchronous, bool mysql)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var serving = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(budget.Token);
            var stream = peer.GetStream(); var channel = new PacketChannel(stream);
            await Handshake(channel, stream, "unrequested", budget.Token, mysql: mysql);
            var request = await channel.ReadPacketAsync(budget.Token); Assert.Equal(3, request[0]);
            var names = new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION", "RAW_TYPE_DECLARATION", "RAW_DATA_LENGTH", "RAW_CHAR_USED", "RAW_GENERATION_FLAGS", "RAW_GENERATION_EXPRESSION", "RAW_IDENTITY", "COLLATION_NAME", "COLUMN_COMMENT" };
            await channel.WritePacketAsync(new[] { (byte)names.Length }, budget.Token);
            foreach (var name in names)
            {
                using var column = new MemoryStream();
                foreach (var field in new[] { "def", "", "", "", name, name })
                { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
                column.Write(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(column.ToArray(), budget.Token);
            }
            await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
            string?[] values = { mysql ? "db" : null, mysql ? null : "owner", "Mixed_'\"_%", "Generated", "1", mysql ? null : "(\"A\"+1)", "YES", mysql ? "decimal" : "NUMBER", null, "18", "4", null,
                mysql ? "decimal(18,4) unsigned" : null, null, null, mysql ? "STORED GENERATED" : "YES", mysql ? "(`A`+1)" : null, null, null, "column comment" };
            using var row = new MemoryStream();
            foreach (var value in values)
            {
                if (value == null) { row.WriteByte(251); continue; }
                var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes);
            }
            await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
            await ObserveClose(stream, budget.Token);
        });
        var context = new AssemblyLoadContext("column-metadata-" + target, isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
            using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
            connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
            await connection.OpenAsync(budget.Token);
            using var table = asynchronous ? await connection.GetSchemaAsync("Columns", budget.Token) : connection.GetSchema("Columns");
            Assert.Equal(23, table.Columns.Count);
            foreach (var name in new[] { "IS_IDENTITY", "IS_COMPUTED", "IS_STORED", "HAS_DEFAULT", "HAS_ON_UPDATE" }) Assert.Equal(typeof(bool), table.Columns[name]!.DataType);
            var result = Assert.Single(table.Rows.Cast<DataRow>());
            Assert.Equal(mysql ? "decimal(18,4) unsigned" : "NUMBER(18,4)", result["TYPE_DECLARATION"]);
            Assert.Equal(true, result["IS_COMPUTED"]); Assert.Equal(mysql, result["IS_STORED"]);
            Assert.Equal(mysql ? "(`A`+1)" : "(\"A\"+1)", result["COMPUTED_EXPRESSION"]);
            Assert.Equal(mysql ? false : DBNull.Value, result["IS_IDENTITY"]);
            Assert.Equal(false, result["HAS_DEFAULT"]); Assert.Equal(DBNull.Value, result["DEFAULT_VALUE_SQL"]);
            Assert.Equal(DBNull.Value, result["COLLATION_NAME"]); Assert.Equal("column comment", result["COLUMN_COMMENT"]);
            Assert.Equal("Mixed_'\"_%", result["TABLE_NAME"]);
            connection.Close(); await serving.WaitAsync(budget.Token);
        }
        finally { budget.Cancel(); listener.Stop(); context.Unload(); }
    }
}
