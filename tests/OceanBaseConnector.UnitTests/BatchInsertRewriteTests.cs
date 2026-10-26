using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("INSERT INTO t(v) VALUES (?)", "INSERT INTO t(v) VALUES (?),(?)")]
        [InlineData("/*before*/ insert into `db`.`t` (`v`) values (coalesce(?, '?'), 'it''s'); -- end", "/*before*/ insert into `db`.`t` (`v`) values (coalesce(?, '?'), 'it''s'),(coalesce(?, '?'), 'it''s') -- end")]
        [InlineData("insert into t values (1, /*?*/ ?) #end", "insert into t values (1, /*?*/ ?),(1, /*?*/ ?) #end")]
        public void BatchInsertRewriteRecognizesOnlyOneSafeTuple(string sql, string expected)
        {
            var shape = RewriteShape(sql);
            Assert.NotNull(shape);
            Assert.Equal(expected, shape.GetType().GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shape, new object[] { 2 }));
        }

        [Theory]
        [InlineData("insert ignore into t values (?)")]
        [InlineData("insert into t values (?) on duplicate key update v=?")]
        [InlineData("insert into t values (?) returning v")]
        [InlineData("insert into t values (?),(?)")]
        [InlineData("insert into t select ?")]
        [InlineData("insert into t set v=?")]
        [InlineData("insert into t values (?); select 1")]
        [InlineData("insert into t values (/*! ? */ 1)")]
        [InlineData("insert into t values (/*M! ? */ 1)")]
        [InlineData("insert into t values ('a\\'?, ?)")]
        [InlineData("insert into t values ('unterminated)")]
        [InlineData("insert into t values (? /*unterminated)")]
        [InlineData("insert into t values (?\0)")]
        [InlineData("insert into t values (1) /*\0*/")]
        [InlineData("insert into ? values (?)")]
        [InlineData("insert into t values (?) ;;")]
        public void BatchInsertRewriteFallsBackForUnsafeShapes(string sql) => Assert.Null(RewriteShape(sql));

        private static object? RewriteShape(string sql)
        {
            var type = typeof(OceanBaseBatch).Assembly.GetType("OceanBaseConnector.Internal.Sql.MySqlInsertRewrite");
            Assert.NotNull(type);
            return type.GetMethod("TryParse", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { sql, CancellationToken.None });
        }

        [Fact]
        public void BatchInsertRewriteIsExplicitAndDefaultsOff()
        {
            var option = typeof(OceanBaseBatch).GetProperty("RewriteBatchedInserts", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(option); Assert.Equal(typeof(bool), option.PropertyType);
            using var batch = new OceanBaseBatch();
            Assert.Equal(false, option.GetValue(batch));
            option.SetValue(batch, true); Assert.Equal(true, option.GetValue(batch));
            option.SetValue(batch, false); Assert.Equal(false, option.GetValue(batch));
            batch.Dispose();
            var error = Assert.Throws<TargetInvocationException>(() => option.SetValue(batch, true));
            Assert.IsType<ObjectDisposedException>(error.InnerException);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteBindsRowsInOnePreparedRequestAndRetainsCounts(bool asynchronous)
        {
            const string sql = "INSERT INTO t(v) VALUES (?)";
            var server = MySqlProcedureServer(async channel =>
            {
                var request = await StatementCommand(channel, 22);
                Assert.Equal(22, request[0]); Assert.Equal("INSERT INTO t(v) VALUES (?),(?)", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await Prepared(channel, 181, 2);
                request = await StatementCommand(channel, 23);
                Assert.Equal(PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = 7 }, new OceanBaseParameter { Value = 8 } }), request.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(2, 2));
                await StatementCommand(channel, 25);
                channel.BeginCommand();
                await Query(channel, "after rewrite"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                var one = BatchItem(batch, sql); one.Parameters.Add(new OceanBaseParameter { Value = 7 });
                var two = BatchItem(batch, sql); two.Parameters.Add(new OceanBaseParameter { Value = 8 });
                Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(1, one.RecordsAffected); Assert.Equal(1, two.RecordsAffected);
                using var after = connection.CreateCommand(); after.CommandText = "after rewrite";
                Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
