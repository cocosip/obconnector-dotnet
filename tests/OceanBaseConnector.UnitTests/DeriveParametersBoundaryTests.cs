using System;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("FLOAT", "float(10,2)")]
        [InlineData("DOUBLE", "double(12,3)")]
        [InlineData("FLOAT", "float(24)")]
        [InlineData("DOUBLE", "double(53)")]
        [InlineData("YEAR", "year(2)")]
        [InlineData("YEAR", "year(4,0)")]
        public async Task DeriveParametersCannotDropUnsupportedMySqlDeclarationAttributes(string type, string declaration)
        {
            using var routines = DerivationRoutines(true); using var arguments = DerivationArguments(true);
            arguments.Rows[0]["DATA_TYPE"] = type; arguments.Rows[0]["TYPE_DECLARATION"] = declaration;
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, true, false, new object[] { "db", "proc" });
                await ServeDerivationTable(channel, 72, arguments, true, true, new object[] { "PROCEDURE", "db", "proc" });
                channel.BeginCommand(); Assert.Equal(new byte[] { 25, 72, 0, 0, 0 }, await channel.ReadPacketAsync());
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "db.proc" };
                var original = new OceanBaseParameter { ParameterName = "original", Value = 42 }; command.Parameters.Add(original);
                await Assert.ThrowsAsync<NotSupportedException>(() => command.DeriveParametersAsync());
                Assert.Same(original, Assert.Single(command.Parameters.Cast<OceanBaseParameter>())); Assert.Same(command.Parameters, original.Owner); Assert.Equal(42, original.Value);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "owner-package")]
        [InlineData(true, "owner-package")]
        [InlineData(false, "current-package")]
        [InlineData(true, "current-package")]
        [InlineData(false, "function")]
        [InlineData(true, "function")]
        public async Task DeriveParametersSelectsPackageOverloadsAndOrdersFunctionReturns(bool asynchronous, string shape)
        {
            var packaged = shape != "function";
            using var routines = DerivationRoutines(false, packaged ? "PROCEDURE" : "FUNCTION");
            using var arguments = DerivationArguments(false);
            if (packaged)
            {
                var routine = routines.Rows[0]; routine["PACKAGE_NAME"] = "PKG"; routine["ROUTINE_TYPE"] = DBNull.Value; routine["OVERLOAD"] = "1";
                var second = routines.NewRow(); second.ItemArray = routine.ItemArray; second["OVERLOAD"] = "2"; second["SUBPROGRAM_ID"] = 2; routines.Rows.Add(second);
                var argument = arguments.Rows[0]; argument["PACKAGE_NAME"] = "PKG"; argument["OVERLOAD"] = "2"; argument["SUBPROGRAM_ID"] = 2;
            }
            else
            {
                var returned = arguments.NewRow(); returned.ItemArray = arguments.Rows[0].ItemArray;
                returned["PARAMETER_NAME"] = DBNull.Value; returned["ORDINAL_POSITION"] = 0; returned["PARAMETER_MODE"] = "RETURN";
                arguments.Rows.Add(returned);
            }
            var server = Server(async channel =>
            {
                await ServeDerivationTable(channel, 71, routines, false, false, shape == "owner-package" ? new object[] { "OWNER", "PROC", "PKG" }
                    : shape == "current-package" ? new object[] { "PKG", "PKG", "PROC" } : new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 72, arguments, false, true, packaged ? new object[] { 2, "2", "OWNER", "PROC", "PKG" } : new object[] { 1, "OWNER", "PROC" }, package: packaged ? "PKG" : null);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = new OceanBaseCommand
                {
                    Connection = connection,
                    CommandType = CommandType.StoredProcedure,
                    CommandText = shape == "owner-package" ? "owner.pkg.proc" : shape == "current-package" ? "pkg.proc" : "owner.proc"
                };
                if (asynchronous) await command.DeriveParametersAsync(packaged ? "2" : null); else command.DeriveParameters(packaged ? "2" : null);
                Assert.Equal(packaged ? new[] { "amount" } : new[] { "RETURN_VALUE", "amount" }, command.Parameters.Cast<OceanBaseParameter>().Select(p => p.ParameterName));
                Assert.Equal(packaged ? ParameterDirection.InputOutput : ParameterDirection.ReturnValue, command.Parameters[0].Direction);
                Assert.All(command.Parameters.Cast<OceanBaseParameter>(), p => { Assert.Equal(-2, p.DecimalScale); Assert.Equal(DBNull.Value, p.Value); });
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task DeriveParametersPreservesDirectoryPermissionErrorsAndExistingValues(bool asynchronous, bool argumentsDenied)
        {
            using var routines = DerivationRoutines(false); using var arguments = DerivationArguments(false);
            var server = Server(async channel =>
            {
                async Task<bool> Denied()
                {
                    await channel.WritePacketAsync(new byte[] { 255, 118, 4, 35, 52, 50, 48, 48, 48 }.Concat(Encoding.UTF8.GetBytes("directory denied")).ToArray());
                    return false;
                }
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" }, argumentsDenied ? null : Denied);
                if (argumentsDenied) await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" }, Denied);
                // Failure cleanup releases its prepared statement before closing the socket.
                channel.BeginCommand(); Assert.Equal(new byte[] { 25, argumentsDenied ? (byte)72 : (byte)71, 0, 0, 0 }, await channel.ReadPacketAsync());
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc" };
                var original = new OceanBaseParameter { ParameterName = "original", Value = 42 }; command.Parameters.Add(original);
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.DeriveParametersAsync()) : Assert.Throws<OceanBaseException>(() => command.DeriveParameters());
                Assert.Equal(1142, error.ServerErrorCode); Assert.Equal("42000", error.SqlState); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Same(original, Assert.Single(command.Parameters.Cast<OceanBaseParameter>())); Assert.Equal(42, original.Value); Assert.Same(command.Parameters, original.Owner);
                Assert.Equal(ConnectionState.Open, connection.State); Assert.NotEmpty(connection.GetSchema("DataTypes").Rows);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeriveParametersUsesTheBoundLocalTransactionWithoutCommittingIt(bool asynchronous)
        {
            using var routines = DerivationRoutines(false); using var arguments = DerivationArguments(false);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await ServeDerivationTable(channel, 71, routines, false, false, new object[] { "OWNER", "OWNER", "PROC" });
                await ServeDerivationTable(channel, 72, arguments, false, true, new object[] { 1, "OWNER", "PROC" });
                channel.BeginCommand(); Assert.Equal(new byte[] { 25, 72, 0, 0, 0 }, await ReadClientCommand(channel));
                channel.BeginCommand(); await TransactionEnd(channel, false);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                using var command = new OceanBaseCommand { Connection = connection, CommandType = CommandType.StoredProcedure, CommandText = "owner.proc" };
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.DeriveParametersAsync());
                command.Transaction = transaction;
                if (asynchronous) await command.DeriveParametersAsync(); else command.DeriveParameters();
                Assert.Same(connection, transaction.Connection); Assert.Same(transaction, command.Transaction);
                Assert.Equal("amount", command.Parameters[0].ParameterName);
                if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public sealed class DerivedParameterPublicationTests
    {
        [Fact]
        public async Task ConcurrentAddCannotDisappearDuringDerivedParameterPublication()
        {
            for (var iteration = 0; iteration < 128; iteration++)
            {
                var collection = new OceanBaseParameterCollection(); var original = new OceanBaseParameter(); collection.Add(original);
                var fresh = new OceanBaseParameter(); var added = new OceanBaseParameter(); var version = collection.Version;
                using var start = new ManualResetEventSlim();
                var addition = Task.Run(() => { start.Wait(); collection.Add(added); });
                var publication = Task.Run(() => { start.Wait(); return Record.Exception(() => collection.ReplaceDerived(new[] { fresh }, version)); });
                start.Set(); await Task.WhenAll(addition, publication);
                var error = await publication; var retained = error == null ? fresh : original;
                if (error != null) Assert.IsType<InvalidOperationException>(error);
                Assert.Equal(new[] { retained, added }, collection.Cast<OceanBaseParameter>());
                Assert.Same(collection, retained.Owner); Assert.Same(collection, added.Owner); Assert.Null((error == null ? original : fresh).Owner);
            }
        }
    }
}
