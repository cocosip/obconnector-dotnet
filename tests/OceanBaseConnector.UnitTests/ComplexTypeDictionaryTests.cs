using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static readonly string[] ComplexHeaderNames = { "TYPE_SCHEMA", "TYPE_NAME", "TYPE_KIND", "MEMBER_COUNT" };
        private static readonly string[] ComplexMemberNames = { "MEMBER_NAME", "MEMBER_POSITION", "MEMBER_SCHEMA", "MEMBER_TYPE", "MEMBER_KIND" };

        private static async Task ComplexDictionaryReply(PacketChannel channel, byte id, string schema, string name, bool header, params string?[][] rows)
        {
            var request = await StatementCommand(channel, 22);
            var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
            Assert.Contains(header ? "ALL_TYPES" : "ALL_TYPE_ATTRS", sql);
            Assert.DoesNotContain(schema, sql); Assert.DoesNotContain(name, sql);
            Assert.Contains("OWNER = ?", sql); Assert.Contains("TYPE_NAME = ?", sql);
            await Prepared(channel, id, 2);
            var execute = await StatementCommand(channel, 23);
            Assert.Equal(PreparedParameters.Execute(id, PreparedParameters.Encode(new[] {
                new OceanBaseParameter { Value = schema }, new OceanBaseParameter { Value = name }
            }, OceanBaseCompatibilityMode.Oracle)), execute);
            await ComplexDictionaryRows(channel, header ? ComplexHeaderNames : ComplexMemberNames, rows);
        }

        private static Task ComplexDictionaryRows(PacketChannel channel, string[] names, params string?[][] rows)
            => ComplexDictionaryRows(channel, names, rows, terminate: true);

        private static async Task ComplexDictionaryRows(PacketChannel channel, string[] names, string?[][] rows, bool terminate)
        {
            await channel.WritePacketAsync(new[] { (byte)names.Length });
            foreach (var name in names) await channel.WritePacketAsync(Column(name, 253));
            await channel.WritePacketAsync(Eof());
            foreach (var values in rows)
            {
                using var stream = new MemoryStream(); stream.WriteByte(0);
                var bitmap = new byte[(names.Length + 9) / 8];
                for (var i = 0; i < values.Length; i++) if (values[i] == null) bitmap[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                stream.Write(bitmap);
                foreach (var value in values)
                    if (value != null) { var bytes = Encoding.UTF8.GetBytes(value); Assert.True(bytes.Length < 251); stream.WriteByte((byte)bytes.Length); stream.Write(bytes); }
                await channel.WritePacketAsync(stream.ToArray());
            }
            if (terminate) await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryLoadsNestedExactTypesCachesAndSnapshotsValues(bool asynchronous)
        {
            const string schema = "Owner_%'目标"; const string name = "Mixed_Type";
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 61, schema, name, true, new[] { schema, name, "OBJECT", "3" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 62, schema, name, false,
                    new[] { "ID", "1", null, "NUMBER", "NUMBER" },
                    new[] { "BYTES", "2", null, "RAW", "RAW" },
                    new[] { "ITEMS", "3", "ChildOwner", "List_Type", "COLLECTION" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 63, "ChildOwner", "List_Type", true, new[] { "ChildOwner", "List_Type", "COLLECTION", null });
                await StatementCommand(channel, 25);
                // Collection queries use ALL_COLL_TYPES rather than ALL_TYPE_ATTRS.
                var prepare = await StatementCommand(channel, 22);
                Assert.Contains("ALL_COLL_TYPES", Encoding.UTF8.GetString(prepare)); await Prepared(channel, 64, 2);
                await StatementCommand(channel, 23);
                await ComplexDictionaryRows(channel, ComplexMemberNames, new[] { null, "1", null, "VARCHAR2", "VARCHAR2" });
                await StatementCommand(channel, 25);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var type = asynchronous ? await connection.GetComplexTypeAsync(schema, name) : connection.GetComplexType(schema, name);
                Assert.Equal(schema, type.SchemaName); Assert.Equal(name, type.TypeName); Assert.False(type.IsCollection);
                Assert.Equal(new[] { "ID", "BYTES", "ITEMS" }, type.Members.Select(x => x.Name));
                Assert.Same(type, connection.GetComplexType(schema, name)); Assert.Same(type, await connection.GetComplexTypeAsync(schema, name));
                var collection = type.Members[2].ComplexType!; Assert.True(collection.IsCollection); Assert.Single(collection.Members);
                Assert.Same(collection, connection.GetComplexType("ChildOwner", "List_Type"));
                var bytes = new byte[] { 1, 2 }; var items = collection.CreateValue("a", null, "患者😀");
                object?[] input = { 17, bytes, items }; var value = type.CreateValue(input);
                input[0] = 99; bytes[0] = 9; Assert.Same(type, value.Type);
                var first = value.GetValues(); Assert.Equal(17, first[0]); Assert.Equal(new byte[] { 1, 2 }, first[1]);
                ((byte[])first[1]!)[0] = 8; first[0] = -1;
                Assert.Equal(17, value.GetValues()[0]); Assert.Equal(new byte[] { 1, 2 }, value.GetValues()[1]); Assert.Same(items, first[2]);
                Assert.Throws<ArgumentException>(() => type.CreateValue(1, bytes));
                Assert.Throws<ArgumentException>(() => type.CreateValue(1.5, bytes, items));
                Assert.Throws<ArgumentException>(() => type.CreateValue(1, bytes, new object?[] { "a" }));
                Assert.Throws<ArgumentException>(() => type.CreateValue(1, bytes, value));
                Assert.Throws<NotSupportedException>(() => ((IList<OceanBaseComplexMember>)type.Members)[0] = type.Members[1]);
                Assert.ThrowsAny<ArgumentException>(() => collection.CreateValue("\ud800"));
                connection.ClearComplexTypeCache(); Assert.Throws<InvalidOperationException>(() => type.CreateValue(1, bytes, items));
                Assert.Throws<InvalidOperationException>(() => collection.CreateValue("old"));
                Assert.Equal(17, value.GetValues()[0]);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "missing")]
        [InlineData(true, "missing")]
        [InlineData(false, "owner")]
        [InlineData(true, "count")]
        [InlineData(false, "position")]
        [InlineData(true, "unsupported")]
        [InlineData(false, "cycle")]
        public async Task ComplexDictionaryRejectsIncompleteOrUnsupportedLayouts(bool asynchronous, string kind)
        {
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 65, "Owner", "Thing", true, kind == "missing" ? Array.Empty<string?[]>() :
                    new[] { new[] { kind == "owner" ? "Other" : "Owner", "Thing", "OBJECT", kind == "count" ? "1.5" : "1" } });
                await StatementCommand(channel, 25);
                if (kind != "missing" && kind != "owner" && kind != "count")
                {
                    await ComplexDictionaryReply(channel, 66, "Owner", "Thing", false,
                        new[] { "A", kind == "position" ? "2" : "1", kind == "cycle" ? "Owner" : null,
                            kind == "cycle" ? "Thing" : "CURSOR", kind == "cycle" ? "OBJECT" : "CURSOR" });
                    await StatementCommand(channel, 25);
                }
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                await Assert.ThrowsAnyAsync<NotSupportedException>(async () => { if (asynchronous) await connection.GetComplexTypeAsync("Owner", "Thing"); else connection.GetComplexType("Owner", "Thing"); });
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryGuardsHaveNoNetworkIo(bool mysql)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                Assert.Throws<ArgumentException>(() => connection.GetComplexType("", "T"));
                Assert.Throws<ArgumentException>(() => connection.GetComplexType("O", "\0"));
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetComplexTypeAsync("O", "T", cancellationToken: canceled.Token)); Assert.Equal(canceled.Token, error.CancellationToken);
                if (mysql)
                {
                    Assert.Throws<NotSupportedException>(() => connection.CreateArrayType("RAW"));
                    Assert.Throws<NotSupportedException>(() => connection.CreateArrayType(ComplexResultType(false)));
                    Assert.Throws<NotSupportedException>(() => connection.GetComplexType("O", "T"));
                    await Assert.ThrowsAsync<NotSupportedException>(() => connection.GetComplexTypeAsync("O", "T"));
                }
                else
                {
                    using (var lease = connection.GetOpenSession().Acquire())
                    {
                        Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType("RAW"));
                        Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType(ComplexResultType(false)));
                        Assert.Throws<InvalidOperationException>(() => connection.GetComplexType("O", "T"));
                        Assert.Throws<InvalidOperationException>(() => connection.ClearComplexTypeCache()); lease.CompleteIfActive();
                    }
                    using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
                    Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType("RAW"));
                    Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType(ComplexResultType(false)));
                    Assert.Throws<InvalidOperationException>(() => connection.GetComplexType("O", "T"));
                }
                Assert.Empty(transport.Written);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task ComplexScalarType(PacketChannel channel, string schema = "Owner", string name = "Thing", string scalar = "RAW", bool collection = true)
        {
            await ComplexDictionaryReply(channel, 81, schema, name, true, new[] { schema, name, collection ? "COLLECTION" : "OBJECT", collection ? null : "1" });
            await StatementCommand(channel, 25);
            var request = await StatementCommand(channel, 22);
            Assert.Contains(collection ? "ALL_COLL_TYPES" : "ALL_TYPE_ATTRS", Encoding.UTF8.GetString(request));
            await Prepared(channel, 82, 2); await StatementCommand(channel, 23);
            await ComplexDictionaryRows(channel, ComplexMemberNames, new[] { collection ? null : "A", "1", null, scalar, scalar });
            await StatementCommand(channel, 25);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryRejectsOversizedRawBeforeCopyingIt(bool asynchronous)
        {
            var server = Server(async channel => await ComplexScalarType(channel));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var type = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.GetComplexType("Owner", "Thing");
                var bytes = new byte[70 * 1024 * 1024]; var before = GC.GetAllocatedBytesForCurrentThread();
                Assert.Throws<ArgumentException>(() => type.CreateValue(bytes));
                Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "Oversized RAW must be rejected before cloning its body.");
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryRespectsExplicitTransactionsAndInvalidatesOldTypes(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                channel.BeginCommand(); await TransactionBegin(channel);
                await ComplexScalarType(channel);
                channel.BeginCommand(); await TransactionEnd(channel, false);
                await ComplexScalarType(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var initial = connection.GetComplexType("Owner", "Thing");
                using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                Assert.Throws<InvalidOperationException>(() => initial.CreateValue(new byte[] { 1 }));
                Assert.Throws<InvalidOperationException>(() => connection.GetComplexType("Owner", "Thing"));
                Assert.Throws<InvalidOperationException>(() => connection.ClearComplexTypeCache());
                var current = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing", transaction) : connection.GetComplexType("Owner", "Thing", transaction);
                Assert.Same(current, connection.GetComplexType("Owner", "Thing", transaction));
                var value = current.CreateValue(new byte[] { 1 }); Assert.Single(value.GetValues());
                await transaction.RollbackAsync();
                Assert.Throws<InvalidOperationException>(() => current.CreateValue(new byte[] { 1 }));
                var after = await connection.GetComplexTypeAsync("Owner", "Thing"); Assert.NotSame(initial, after); Assert.NotSame(current, after);
                Assert.Throws<InvalidOperationException>(() => initial.CreateValue(new byte[] { 1 }));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ComplexDictionaryCacheSeparatesCaseAndSchemaAndExplicitRefresh()
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, "Owner", "Thing"); await ComplexScalarType(channel, "owner", "Thing");
                await ComplexScalarType(channel, "Owner", "thing"); await ComplexScalarType(channel, "Owner", "Thing", "NUMBER");
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var first = await connection.GetComplexTypeAsync("Owner", "Thing");
                var second = await connection.GetComplexTypeAsync("owner", "Thing"); var third = await connection.GetComplexTypeAsync("Owner", "thing");
                Assert.NotSame(first, second); Assert.NotSame(first, third); Assert.Same(first, await connection.GetComplexTypeAsync("Owner", "Thing"));
                connection.ClearComplexTypeCache(); var refreshed = await connection.GetComplexTypeAsync("Owner", "Thing");
                Assert.Equal("NUMBER", refreshed.Members[0].TypeName); Assert.Single(refreshed.CreateValue(17).GetValues());
                foreach (var old in new[] { first, second, third }) Assert.Throws<InvalidOperationException>(() => old.CreateValue());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryCloseAndReopenInvalidatesTypeWithoutBreakingNewSession(bool asynchronous)
        {
            var server = Server(async channel => await ComplexScalarType(channel));
            var next = Server(async channel => { await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing"); var value = type.CreateValue(new byte[] { 1 });
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                Assert.Throws<InvalidOperationException>(() => type.CreateValue());
                connection.ConnectionString = Configuration(next.Port); await connection.OpenAsync(); Assert.Throws<InvalidOperationException>(() => type.CreateValue());
                Assert.Equal(new byte[] { 1 }, value.GetValues()[0]);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); next.Stop(); }
            await Task.WhenAll(server.Completion, next.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ComplexDictionarySentQueryTimeoutOrCancellationBreaksOriginalSession(bool asynchronous, bool cancel)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 83, 2); await StatementCommand(channel, 23); received.SetResult();
                try { Assert.Equal(0, await channelStreamRead(channel)); disconnected.SetResult(); }
                catch (IOException) { disconnected.SetResult(); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=1");
            using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await connection.OpenAsync();
                var operation = asynchronous ? connection.GetComplexTypeAsync("Owner", "Thing", cancellationToken: caller.Token) : Task.Run(() => connection.GetComplexType("Owner", "Thing"));
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(operation.IsCompleted);
                Assert.Throws<InvalidOperationException>(() => connection.ClearComplexTypeCache());
                if (cancel) caller.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => operation); Assert.True(error.IsExecutionOutcomeUnknown);
                if (cancel)
                {
                    Exception? cause = error;
                    while (cause != null && !(cause is OperationCanceledException)) cause = cause.InnerException;
                    Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(cause).CancellationToken);
                }
                await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ComplexDictionaryAsyncQueriesWaitAndHoldExclusiveReservation()
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 84, 2); await StatementCommand(channel, 23);
                received.SetResult(); await resume.Task;
                await ComplexDictionaryRows(channel, ComplexHeaderNames, new[] { "Owner", "Empty_Object", "OBJECT", "0" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 85, "Owner", "Empty_Object", false);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var pending = connection.GetComplexTypeAsync("Owner", "Empty_Object");
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted);
                using var command = connection.CreateCommand(); command.CommandText = "interleaved";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                resume.SetResult(); var type = await pending; Assert.Empty(type.Members); Assert.Empty(type.CreateValue().GetValues());
            }
            finally { resume.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ComplexDictionaryWarmedChildCannotBypassSessionMetadataBudget()
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                await ComplexDictionaryReply(channel, 86, "Owner", "Root_Type", true, new[] { "Owner", "Root_Type", "OBJECT", "65535" });
                await StatementCommand(channel, 25);
                channel.BeginCommand();
                byte[] request;
                try { request = await ReadClientCommand(channel, false); }
                catch (Exception error) when (error is IOException || error is ProtocolException) { return; }
                // The unfixed loader continues querying after its warmed child has exhausted the budget.
                Assert.Equal(22, request[0]); await Prepared(channel, 87, 2); await StatementCommand(channel, 23);
                var rows = Enumerable.Range(1, 65535).Select(i => new string?[] { "A" + i, i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Owner", "Thing", "COLLECTION" }).ToArray();
                await ComplexDictionaryRows(channel, ComplexMemberNames, rows); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var child = await connection.GetComplexTypeAsync("Owner", "Thing");
                await Assert.ThrowsAsync<NotSupportedException>(() => connection.GetComplexTypeAsync("Owner", "Root_Type"));
                Assert.Same(child, await connection.GetComplexTypeAsync("Owner", "Thing"));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("name")]
        [InlineData("kind")]
        [InlineData("negative-count")]
        [InlineData("large-count")]
        [InlineData("duplicate-name")]
        [InlineData("null-name")]
        [InlineData("fractional-position")]
        [InlineData("position-overflow")]
        [InlineData("missing-member")]
        [InlineData("unknown-schema")]
        [InlineData("member-name-nul")]
        [InlineData("invalid-kind")]
        public async Task ComplexDictionaryRejectsInconsistentIdentityAndMemberMetadata(string kind)
        {
            var headerOnly = kind == "name" || kind == "kind" || kind == "negative-count" || kind == "large-count";
            var server = Server(async channel =>
            {
                var count = kind == "negative-count" ? "-1" : kind == "large-count" ? "65536" : kind == "duplicate-name" ? "2" : "1";
                await ComplexDictionaryReply(channel, 91, "Owner", "Thing", true, new[] { "Owner", kind == "name" ? "Other" : "Thing", kind == "kind" ? "OPAQUE" : "OBJECT", count });
                await StatementCommand(channel, 25);
                if (!headerOnly)
                {
                    var row = new string?[] { kind == "null-name" ? null : kind == "member-name-nul" ? "A\0" : "A",
                        kind == "fractional-position" ? "1.5" : kind == "position-overflow" ? "999999999999999999999999999999999" : "1",
                        kind == "unknown-schema" ? "OtherOwner" : null, "NUMBER", kind == "invalid-kind" ? "VARCHAR2" : "NUMBER" };
                    var rows = kind == "missing-member" ? Array.Empty<string?[]>() : kind == "duplicate-name" ? new[] { row, new[] { "A", "2", null, "NUMBER", "NUMBER" } } : new[] { row };
                    await ComplexDictionaryReply(channel, 92, "Owner", "Thing", false, rows); await StatementCommand(channel, 25);
                }
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var error = await Record.ExceptionAsync(() => connection.GetComplexTypeAsync("Owner", "Thing"));
                Assert.True(error is NotSupportedException || error is ArgumentException, error?.ToString() ?? "Expected dictionary rejection.");
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryFailedRootDoesNotPublishSuccessfullyLoadedChildren(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 93, "Owner", "Root_Type", true, new[] { "Owner", "Root_Type", "OBJECT", "2" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 94, "Owner", "Root_Type", false,
                    new[] { "GOOD", "1", "Owner", "Good_Type", "COLLECTION" }, new[] { "BAD", "2", "Owner", "Bad_Type", "OBJECT" });
                await StatementCommand(channel, 25);
                await ComplexScalarType(channel, name: "Good_Type");
                await ComplexScalarType(channel, name: "Bad_Type", scalar: "CURSOR", collection: false);
                await ComplexScalarType(channel, name: "Good_Type");
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await connection.GetComplexTypeAsync("Owner", "Root_Type"); else connection.GetComplexType("Owner", "Root_Type"); });
                var good = await connection.GetComplexTypeAsync("Owner", "Good_Type"); Assert.True(good.IsCollection);
                Assert.Single(good.CreateValue(new byte[] { 1 }).GetValues());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("NUMBER", 1)]
        [InlineData("VARCHAR2", 2)]
        [InlineData("CHAR", 2)]
        [InlineData("DATE", 3)]
        [InlineData("TIMESTAMP", 4)]
        public async Task ComplexDictionaryConstructsOnlyPreciselyTypedScalarMembers(string scalar, int valueKind)
        {
            var server = Server(async channel => await ComplexScalarType(channel, scalar: scalar));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                object value = valueKind == 1 ? new OceanBaseDecimal(System.Numerics.BigInteger.Parse("12345678901234567890123456789012345678", System.Globalization.CultureInfo.InvariantCulture), 0) : valueKind == 2 ? "患者😀"
                    : valueKind == 3 ? new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Unspecified) : new OceanBaseTimestamp(new DateTime(2026, 10, 19, 12, 34, 56), 123456789, 9);
                Assert.Equal(value, type.CreateValue(value).GetValues()[0]); Assert.Null(type.CreateValue(DBNull.Value).GetValues()[0]);
                Assert.Empty(type.CreateValue().GetValues());
                Assert.Throws<ArgumentException>(() => type.CreateValue(new object()));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("BLOB", OceanBaseDbType.Blob)]
        [InlineData("CLOB", OceanBaseDbType.Clob)]
        public async Task ComplexDictionaryLoadsLobMembersButDoesNotCreateLobParameters(string scalar, OceanBaseDbType dbType)
        {
            var server = Server(async channel => await ComplexScalarType(channel, scalar: scalar, collection: false));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                Assert.Equal(scalar, type.Members[0].TypeName);
                Assert.Throws<NotSupportedException>(() => type.CreateValue(new object()));
                Assert.Equal(dbType, new OceanBaseParameter { OceanBaseDbType = dbType }.OceanBaseDbType);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(32)]
        [InlineData(33)]
        public async Task ComplexDictionaryBoundsNestedLayoutsAndPublicValueMemberTotals(int levels)
        {
            var server = Server(async channel =>
            {
                for (var i = 0; i < 32; i++)
                {
                    var id = (byte)(100 + 2 * i); var name = "Node" + i;
                    await ComplexDictionaryReply(channel, id, "Owner", name, true, new[] { "Owner", name, "COLLECTION", null });
                    await StatementCommand(channel, 25);
                    var request = await StatementCommand(channel, 22); Assert.Contains("ALL_COLL_TYPES", Encoding.UTF8.GetString(request));
                    await Prepared(channel, (byte)(id + 1), 2); await StatementCommand(channel, 23);
                    var leaf = i == levels - 1;
                    await ComplexDictionaryRows(channel, ComplexMemberNames, new[] { null, "1", leaf ? null : "Owner", leaf ? "VARCHAR2" : "Node" + (i + 1), leaf ? "VARCHAR2" : "COLLECTION" });
                    await StatementCommand(channel, 25);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                if (levels == 33) await Assert.ThrowsAsync<NotSupportedException>(() => connection.GetComplexTypeAsync("Owner", "Node0"));
                else
                {
                    var root = await connection.GetComplexTypeAsync("Owner", "Node0"); var types = new List<OceanBaseComplexType> { root };
                    while (types[types.Count - 1].Members[0].ComplexType is OceanBaseComplexType child) types.Add(child);
                    Assert.Equal(32, types.Count); var value = types[31].CreateValue("leaf");
                    for (var i = 30; i >= 0; i--) value = types[i].CreateValue(value);
                    Assert.Same(root, value.Type);
                    Assert.Throws<NotSupportedException>(() => connection.CreateArrayType(root));
                    var arrayType = connection.CreateArrayType(types[1]);
                    Assert.Equal(32, arrayType.NestingDepth); Assert.Single(arrayType.CreateValue(value.GetValues()[0]).GetValues());
                    var many = types[31].CreateValue(new object?[1024]);
                    Assert.Throws<ArgumentException>(() => types[30].CreateValue(Enumerable.Repeat<object?>(many, 64).ToArray()));
                    var bounded = types[31].CreateValue(new object?[1022]); Assert.Equal(64, types[30].CreateValue(Enumerable.Repeat<object?>(bounded, 64).ToArray()).GetValues().Length);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryRejectsWrongColumnShapesWithoutReusingUnreadResponse(bool members)
        {
            var server = Server(async channel =>
            {
                if (members)
                {
                    await ComplexDictionaryReply(channel, 171, "Owner", "Thing", true, new[] { "Owner", "Thing", "OBJECT", "1" });
                    await StatementCommand(channel, 25);
                }
                await StatementCommand(channel, 22); await Prepared(channel, 172, 2); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("WRONG_COLUMN", 253));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 10 }.Concat(Encoding.UTF8.GetBytes("unexpected")).ToArray());
                // The reader prefetches one row; leave its final EOF pending to prove the session is discarded.
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await Assert.ThrowsAsync<NotSupportedException>(() => connection.GetComplexTypeAsync("Owner", "Thing"));
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
