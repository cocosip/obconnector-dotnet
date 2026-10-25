using System;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryDdlDoesNotAttachOldLayoutToNewResults(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                channel.BeginCommand(); await Query(channel, "ALTER TYPE Owner.Thing COMPILE"); await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 22); await Prepared(channel, 94, 0);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var old = await connection.GetComplexTypeAsync("Owner", "Thing");
                var snapshot = old.CreateValue(new byte[] { 1 });
                using (var ddl = connection.CreateCommand())
                {
                    ddl.CommandText = "ALTER TYPE Owner.Thing COMPILE"; Assert.Equal(1, await ddl.ExecuteNonQueryAsync());
                }
                using var query = connection.CreateCommand(); query.CommandText = "SELECT value FROM t";
                if (asynchronous) await query.PrepareAsync(); else query.Prepare();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
                });
                Assert.IsType<NotSupportedException>(error.InnerException);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(new byte[] { 1 }, snapshot.GetValues()[0]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("CREATE OR REPLACE TYPE Owner.Thing AS TABLE OF NUMBER")]
        [InlineData("SELECT q'[; DROP TYPE Owner.Thing]' FROM dual; /* second */ ALTER TYPE Owner.Thing COMPILE")]
        [InlineData("-- ddl\nDROP TYPE Owner.Thing FORCE")]
        [InlineData("RENAME Thing TO OtherThing")]
        [InlineData("TRUNCATE TABLE uses_thing")]
        public async Task ComplexDictionaryDdlInvalidatesNestedAndAnonymousDescriptors(string sql)
        {
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 85, "Owner", "Parent", true, new[] { "Owner", "Parent", "OBJECT", "1" });
                await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 86, "Owner", "Parent", false, new[] { "CHILD", "1", "Owner", "Thing", "COLLECTION" });
                await StatementCommand(channel, 25); await ComplexScalarType(channel);
                channel.BeginCommand(); await Query(channel, sql); await channel.WritePacketAsync(Ok(1));
            }, multiStatements: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); var parent = await connection.GetComplexTypeAsync("Owner", "Parent");
                var child = parent.Members[0].ComplexType!; var item = child.CreateValue(new byte[] { 1 });
                var snapshot = parent.CreateValue(item); var array = connection.CreateArrayType(parent);
                using var ddl = connection.CreateCommand(); ddl.CommandText = sql; Assert.Equal(1, await ddl.ExecuteNonQueryAsync());
                Assert.Throws<InvalidOperationException>(() => child.CreateValue(new byte[] { 2 }));
                Assert.Throws<InvalidOperationException>(() => parent.CreateValue(item));
                Assert.Throws<InvalidOperationException>(() => array.CreateValue(snapshot));
                Assert.Throws<InvalidOperationException>(() => connection.CreateArrayType(child));
                Assert.Same(item, snapshot.GetValues()[0]); Assert.Equal(new byte[] { 1 }, item.GetValues()[0]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ComplexDictionaryBatchDdlRejectsStaleSuffixBeforeSendingIt(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                channel.BeginCommand(); await Query(channel, "DROP TYPE Owner.Thing"); await channel.WritePacketAsync(Ok(1));
                channel.BeginCommand(); await Query(channel, "SELECT 1 FROM dual"); await channel.WritePacketAsync(Ok(2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var batch = connection.CreateBatch(); var first = BatchItem(batch, "DROP TYPE Owner.Thing");
                var suffix = BatchItem(batch, "SELECT :value FROM dual");
                suffix.Parameters.Add(new OceanBaseParameter { ParameterName = "value", Value = type.CreateValue(new byte[] { 1 }) });
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Equal(1, first.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                using var probe = connection.CreateCommand(); probe.CommandText = "SELECT 1 FROM dual";
                Assert.Equal(2, await probe.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
