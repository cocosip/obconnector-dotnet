using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task PrepareCursorColumns(PacketChannel channel, bool deprecated, params byte[] types)
        {
            await StatementCommand(channel, 22, deprecated);
            await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, (byte)types.Length, 0, 0, 0, 0, 0, 0 });
            for (var i = 0; i < types.Length; i++) await channel.WritePacketAsync(Column("c" + i, types[i]));
            if (!deprecated) await channel.WritePacketAsync(Eof());
        }

        private static async Task CursorColumnResult(PacketChannel channel, bool deprecated, byte[][] rows, params byte[] types)
        {
            await channel.WritePacketAsync(new byte[] { (byte)types.Length });
            for (var i = 0; i < types.Length; i++) await channel.WritePacketAsync(Column("c" + i, types[i]));
            if (!deprecated) await channel.WritePacketAsync(Eof());
            foreach (var row in rows) await channel.WritePacketAsync(row);
            await channel.WritePacketAsync(CursorTerminator(2, deprecated));
        }

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(true, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, true, true, false)]
        [InlineData(false, false, false, true)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(true, true, false, true)]
        public Task CursorColumnWaitsForParentAndSurvivesCommandDisposal(bool asynchronous, bool sequential, bool deprecated, bool keyInfo)
            => ReadCursorColumnAndRelease(asynchronous, sequential, deprecated, keyInfo);

        private static async Task ReadCursorColumnAndRelease(bool asynchronous, bool sequential, bool deprecated, bool keyInfo, int commandFetchSize = 0, bool automaticPreparation = false)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, deprecated, 163);
                var execution = await StatementCommand(channel, 23, deprecated); Assert.Equal(0, execution[5]);
                await CursorColumnResult(channel, deprecated, new[] { new byte[] { 0, 0, 91, 0, 0, 0 } }, 163);
                Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28, deprecated));
                await CursorMetadata(channel, 2, deprecated, omitTerminator: deprecated);
                await channel.WritePacketAsync(CursorRow(7));
                await channel.WritePacketAsync(CursorTerminator(130, deprecated));
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
                channel.BeginCommand(); await Query(channel, "after", deprecated); await channel.WritePacketAsync(Ok(1));
            }, deprecated, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "SELECT CURSOR(SELECT 7 FROM DUAL) FROM DUAL";
                ((OceanBaseCommand)command).FetchSize = commandFetchSize;
                if (!automaticPreparation) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                var behavior = (sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) | (keyInfo ? CommandBehavior.KeyInfo : CommandBehavior.Default);
                using var parent = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                Assert.Equal(typeof(OceanBaseRefCursor), parent.GetFieldType(0));
                Assert.Equal("REF CURSOR", parent.GetDataTypeName(0));
                Assert.True(asynchronous ? await parent.ReadAsync() : parent.Read());
                using var cursor = asynchronous ? await parent.GetFieldValueAsync<OceanBaseRefCursor>(0) : parent.GetFieldValue<OceanBaseRefCursor>(0);
                Assert.Same(cursor, parent.GetValue(0)); Assert.Same(cursor, parent.GetProviderSpecificValue(0));
                Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader());
                Assert.False(asynchronous ? await parent.ReadAsync() : parent.Read());
                command.Dispose();
                parent.Close();
                using var child = asynchronous ? await cursor.GetDataReaderAsync(2) : cursor.GetDataReader(2);
                Assert.True(asynchronous ? await child.ReadAsync() : child.Read()); Assert.Equal(7L, child.GetInt64(0));
                Assert.False(asynchronous ? await child.ReadAsync() : child.Read());
                Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader());
                cursor.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorColumnSkippedNullAndAliasesDoNotPublish(bool asynchronous, bool sequential)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163, 163, 8);
                await StatementCommand(channel, 23);
                var row = new byte[18]; row[2] = row[6] = 91; BitConverter.GetBytes(7L).CopyTo(row, 10);
                var nullRow = new byte[14]; nullRow[1] = 4; nullRow[2] = 92; BitConverter.GetBytes(8L).CopyTo(nullRow, 6);
                await CursorColumnResult(channel, false, new[] { row, nullRow }, 163, 163, 8);
                foreach (var id in new byte[] { 91, 92, 42 })
                    Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT cursors";
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(reader.IsDBNull(0)); Assert.Equal(7L, reader.GetInt64(2));
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.True(reader.IsDBNull(0)); Assert.Equal(8L, reader.GetInt64(2));
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorColumnNestedDescendantRetainsDisposedAncestors(bool asynchronous, bool sequential)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163); await StatementCommand(channel, 23);
                await CursorColumnResult(channel, false, new[] { new byte[] { 0, 0, 91, 0, 0, 0 } }, 163);
                Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28));
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("c0", 163)); await channel.WritePacketAsync(Column("c1", 163));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 92, 0, 0, 0, 93, 0, 0, 0 });
                await channel.WritePacketAsync(CursorTerminator(130, false));
                Assert.Equal(new byte[] { 25, 93, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 28, 92, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28));
                await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(9));
                await channel.WritePacketAsync(CursorTerminator(130, false));
                foreach (var id in new byte[] { 92, 91, 42 })
                    Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT nested_cursor";
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                using var parent = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                Assert.True(asynchronous ? await parent.ReadAsync() : parent.Read());
                using var cursor = parent.GetFieldValue<OceanBaseRefCursor>(0);
                Assert.False(asynchronous ? await parent.ReadAsync() : parent.Read()); parent.Close(); command.Dispose();
                using var child = asynchronous ? await cursor.GetDataReaderAsync(2, behavior) : cursor.GetDataReader(2, behavior);
                Assert.True(asynchronous ? await child.ReadAsync() : child.Read());
                using var descendant = asynchronous ? await child.GetFieldValueAsync<OceanBaseRefCursor>(0) : child.GetFieldValue<OceanBaseRefCursor>(0);
                Assert.False(asynchronous ? await child.ReadAsync() : child.Read());
                child.Close();
                if (asynchronous) await cursor.DisposeAsync(); else cursor.Dispose();
                using var leaf = asynchronous ? await descendant.GetDataReaderAsync(2, behavior) : descendant.GetDataReader(2, behavior);
                Assert.True(asynchronous ? await leaf.ReadAsync() : leaf.Read()); Assert.Equal(9L, leaf.GetInt64(0));
                Assert.False(asynchronous ? await leaf.ReadAsync() : leaf.Read()); descendant.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorColumnResourceCeilingIncludesDescendantExecutions(bool sequential)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163); await StatementCommand(channel, 23);
                await CursorColumnResult(channel, false, new[] { new byte[] { 0, 0, 91, 0, 0, 0 } }, 163);
                await StatementCommand(channel, 28);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("c", 163));
                await channel.WritePacketAsync(Eof());
                for (var i = 100u; i < 4196; i++)
                {
                    var row = new byte[6]; BitConverter.GetBytes(i).CopyTo(row, 2); await channel.WritePacketAsync(row);
                }
                await channel.WritePacketAsync(CursorTerminator(130, false));
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT many_cursors";
                await command.PrepareAsync(); using var parent = await command.ExecuteReaderAsync();
                Assert.True(await parent.ReadAsync()); using var cursor = parent.GetFieldValue<OceanBaseRefCursor>(0);
                Assert.False(await parent.ReadAsync()); parent.Close();
                using var child = await cursor.GetDataReaderAsync(4096, sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { while (await child.ReadAsync()) { } });
                Assert.IsType<ProtocolException>(error.InnerException);
                if (!sequential) Assert.Contains("4096", error.InnerException!.Message);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorColumnPublishesOnlyAccessedSibling(bool asynchronous, bool sequential)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163, 163, 163); await StatementCommand(channel, 23);
                await CursorColumnResult(channel, false, new[] { new byte[] { 0, 0, 91, 0, 0, 0, 91, 0, 0, 0, 92, 0, 0, 0 } }, 163, 163, 163);
                Assert.Equal(new byte[] { 25, 92, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28));
                await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(7));
                await channel.WritePacketAsync(CursorTerminator(130, false));
                foreach (var id in new byte[] { 91, 42 }) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT aliases";
                await command.PrepareAsync(); using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync());
                using var cursor = asynchronous ? await reader.GetFieldValueAsync<OceanBaseRefCursor>(0) : reader.GetFieldValue<OceanBaseRefCursor>(0);
                Assert.Same(cursor, reader.GetProviderSpecificValue(1));
                Assert.False(await reader.ReadAsync()); reader.Close(); command.Dispose();
                using var child = asynchronous ? await cursor.GetDataReaderAsync(2) : cursor.GetDataReader(2);
                Assert.True(await child.ReadAsync()); Assert.Equal(7L, child.GetInt64(0)); Assert.False(await child.ReadAsync());
                cursor.Dispose(); using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        public async Task CursorColumnInvalidIdentityOrUnownedMetadataRetiresSession(bool asynchronous, bool sequential, bool unannounced)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, unannounced ? (byte)8 : (byte)163);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("c", 163));
                if (!unannounced)
                {
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new byte[] { 0, 0, 0, 0, 0, 0 });
                }
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT invalid_cursor";
                await command.PrepareAsync(); var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                    while (asynchronous ? await reader.ReadAsync() : reader.Read()) { }
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorColumnSchemaOnlyDoesNotExecuteOrAllocateCursor(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163);
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT schema_cursor";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.SchemaOnly);
                Assert.Equal(typeof(OceanBaseRefCursor), reader.GetFieldType(0)); Assert.False(reader.HasRows); Assert.False(reader.Read());
                reader.Close(); command.Dispose(); using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorColumnAfterProcedureOutputIsNotPublishedByOutputParameters(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4));
                await channel.WritePacketAsync(Column("v", 8)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("v", 8)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(CursorRow(7)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Ok(status: 10));
                await CursorColumnResult(channel, false, new[] { new byte[] { 0, 0, 92, 0, 0, 0 } }, 163);
                foreach (var id in new byte[] { 92, 42 }) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "BEGIN p(:v); END;";
                var output = new OceanBaseParameter { ParameterName = "v", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.Int64 };
                command.Parameters.Add(output);
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Equal(7L, output.Value); command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch { server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); throw; }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        [InlineData(false, 5)]
        [InlineData(true, 5)]
        [InlineData(false, 6)]
        [InlineData(true, 6)]
        [InlineData(false, 7)]
        [InlineData(true, 7)]
        [InlineData(false, 8)]
        [InlineData(true, 8)]
        [InlineData(false, 9)]
        [InlineData(true, 9)]
        public async Task CursorColumnFailedTypedAccessDoesNotPublish(bool sequential, int accessor)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163); await StatementCommand(channel, 23);
                await CursorColumnResult(channel, false, new[] { new byte[] { 0, 0, 91, 0, 0, 0 } }, 163);
                foreach (var id in new byte[] { 91, 42 }) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT wrong_type";
                await command.PrepareAsync(); using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync());
                if (accessor == 3) await Assert.ThrowsAsync<InvalidCastException>(() => reader.GetFieldValueAsync<int>(0));
                else Assert.Throws<InvalidCastException>(() =>
                {
                    switch (accessor)
                    {
                        case 0: reader.GetInt32(0); break;
                        case 1: reader.GetString(0); break;
                        case 2: reader.GetFieldValue<int>(0); break;
                        case 4: reader.GetBytes(0, 0, null, 0, 0); break;
                        case 5: reader.GetChars(0, 0, null, 0, 0); break;
                        case 6: reader.GetStream(0); break;
                        case 7: reader.GetTextReader(0); break;
                        case 8: reader.GetValues(new string[1]); break;
                        case 9: reader.GetProviderSpecificValues(new string[1]); break;
                    }
                });
                Assert.False(await reader.ReadAsync()); reader.Close(); command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorColumnKeyInfoRewriteKeepsFinalParentStatement(bool asynchronous, bool schemaOnly)
        {
            var prefix = SchemaColumn("payload", 163, 4, originalName: "payload");
            var key = SchemaColumn("__ob_key_0", 8, 20, flags: 1, originalName: "id");
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 42, prefix);
                await KeyDirectory(channel, false, new string?[] { "租户库", "ledger", "PK", "P", "id", "1", "NO", "YES" });
                await StatementCommand(channel, 22); await KeyPrepare(channel, 43, prefix, key);
                if (!schemaOnly)
                {
                    await StatementCommand(channel, 23); await channel.WritePacketAsync(new byte[] { 2 });
                    await channel.WritePacketAsync(prefix); await channel.WritePacketAsync(key); await channel.WritePacketAsync(Eof());
                    var row = new byte[14]; row[2] = 91; BitConverter.GetBytes(7L).CopyTo(row, 6);
                    await channel.WritePacketAsync(row); await channel.WritePacketAsync(Eof());
                    Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28));
                    await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(9)); await channel.WritePacketAsync(CursorTerminator(130, false));
                    foreach (var id in new byte[] { 91, 43 }) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                else Assert.Equal(new byte[] { 25, 43, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select l.payload from \"租户库\".\"ledger\" l";
                var behavior = CommandBehavior.KeyInfo | CommandBehavior.SequentialAccess | (schemaOnly ? CommandBehavior.SchemaOnly : CommandBehavior.Default);
                using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                if (!schemaOnly)
                {
                    Assert.True(await reader.ReadAsync()); using var cursor = reader.GetFieldValue<OceanBaseRefCursor>(0);
                    Assert.False(await reader.ReadAsync()); reader.Close();
                    using var child = await cursor.GetDataReaderAsync(2); Assert.True(await child.ReadAsync()); Assert.Equal(9L, child.GetInt64(0)); Assert.False(await child.ReadAsync());
                    cursor.Dispose();
                }
                else Assert.False(await reader.ReadAsync());
                reader.Close(); command.Dispose(); using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorColumnOldLeaseCannotAffectReopenedConnection(bool asynchronous, bool sequential)
        {
            var oldServer = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163); await StatementCommand(channel, 23);
                await CursorColumnResult(channel, false, new[] { new byte[] { 0, 0, 91, 0, 0, 0 } }, 163);
                await AssertOracleExecuteTransportClosed(channel);
            });
            var newServer = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(oldServer.Port) + ";Pooling=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT old_cursor";
                await command.PrepareAsync();
                var behavior = CommandBehavior.CloseConnection | (sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                Assert.True(await reader.ReadAsync()); using var cursor = reader.GetFieldValue<OceanBaseRefCursor>(0);
                Assert.False(await reader.ReadAsync());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await oldServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                connection.ConnectionString = Configuration(newServer.Port);
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader()); cursor.Dispose();
                reader.Close(); command.Dispose(); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); oldServer.Stop(); newServer.Stop(); }
            await newServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false, false, false)]
        [InlineData(true, true, true, false, false)]
        [InlineData(false, true, true, false, true)]
        [InlineData(true, false, false, false, true)]
        [InlineData(false, false, true, true, false)]
        [InlineData(true, true, false, true, false)]
        [InlineData(false, true, false, true, true)]
        [InlineData(true, false, true, true, true)]
        public Task CursorColumnPositiveFetchHintDoesNotPageParent(bool asynchronous, bool sequential, bool deprecated, bool keyInfo, bool automaticPreparation)
            => ReadCursorColumnAndRelease(asynchronous, sequential, deprecated, keyInfo, commandFetchSize: 2, automaticPreparation: automaticPreparation);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorColumnOffsetFetchIsRejectedBeforeTargetExecution(bool asynchronous, bool keyInfo)
        {
            var server = Server(async channel =>
            {
                await PrepareCursorColumns(channel, false, 163);
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT CURSOR(SELECT 7 FROM DUAL) FROM DUAL";
                ((OceanBaseCommand)command).FetchSize = 2; ((OceanBaseCommand)command).UseCursorOffset = true;
                await command.PrepareAsync(); var behavior = keyInfo ? CommandBehavior.KeyInfo : CommandBehavior.Default;
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync(behavior));
                else Assert.Throws<NotSupportedException>(() => command.ExecuteReader(behavior));
                Assert.Equal(ConnectionState.Open, connection.State); command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
