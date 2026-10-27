using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ProviderSurfaceTests
    {
        [Fact]
        public void FactoryCreatesUnifiedObjectsWithoutAdvertisingUnsupportedFeatures()
        {
            DbProviderFactories.RegisterFactory("OceanBaseConnector", OceanBaseProviderFactory.Instance);
            var factory = DbProviderFactories.GetFactory("OceanBaseConnector");
            using var connection = Assert.IsType<OceanBaseConnection>(factory.CreateConnection());
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.IsType<OceanBaseCommand>(factory.CreateCommand());
            Assert.IsType<OceanBaseParameter>(factory.CreateParameter());
            Assert.IsType<OceanBaseConnectionStringBuilder>(factory.CreateConnectionStringBuilder());
            Assert.True(factory.CanCreateBatch);
            Assert.True(connection.CanCreateBatch);
            using var batch = Assert.IsType<OceanBaseBatch>(factory.CreateBatch());
            Assert.IsType<OceanBaseBatchCommand>(factory.CreateBatchCommand());
            Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
            Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("other"));
            Assert.Throws<ArgumentNullException>(() => connection.EnlistTransaction(null));
        }

        [Fact]
        public async Task ClosedConnectionAndUnimplementedApisFailBeforeNetworkAndParametersKeepIdentity()
        {
            using var connection = new OceanBaseConnection("Server=localhost;User ID=u");
            using var command = Assert.IsType<OceanBaseCommand>(connection.CreateCommand());
            var parameter = command.CreateParameter();
            parameter.ParameterName = "x";
            parameter.Value = 42;
            command.Parameters.Add(parameter);
            command.CommandText = "select :x";
            Assert.Same(parameter, command.Parameters["x"]);
            Assert.Same(connection, command.Connection);
            Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => command.PrepareAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ChangeDatabaseAsync("other"));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.OpenAsync(canceled.Token));
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Fact]
        public void DataSourceFreezesConfigurationAndDisposalPreventsLaterOpens()
        {
            var builder = new OceanBaseDataSourceBuilder("Server=one;User ID=u;Password=secret");
            using var source = builder.Build();
            builder.ConnectionStringBuilder.Server = "two";
            using var other = builder.Build();
            using var connection = source.CreateConnection();
            Assert.Equal("one", connection.DataSource);
            Assert.DoesNotContain("secret", source.ConnectionString);
            Assert.DoesNotContain("secret", connection.ConnectionString);
            Assert.Equal("two", other.CreateConnection().DataSource);
            Assert.Throws<InvalidOperationException>(() => connection.ConnectionString = "Server=three;User ID=u");
            source.Dispose();
            source.Dispose();
            Assert.Throws<ObjectDisposedException>(() => source.CreateConnection());
            Assert.Throws<ObjectDisposedException>(() => connection.Open());
            Assert.Equal(ConnectionState.Closed, connection.State);
        }

        [Fact]
        public void ParameterDefaultsAndRangeValidationFollowAdoNet()
        {
            var parameter = new OceanBaseParameter();
            Assert.Equal(DbType.String, parameter.DbType);
            Assert.Equal(ParameterDirection.Input, parameter.Direction);
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.Size = -2);
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.Direction = (ParameterDirection)123);
            Assert.Throws<ArgumentOutOfRangeException>(() => parameter.DbType = (DbType)123);
            using var command = new OceanBaseCommand();
            var other = command.CreateParameter();
            Assert.Throws<ArgumentException>(() => command.Parameters.AddRange(new[] { parameter, other, parameter }));
            Assert.Empty(command.Parameters);
            command.Parameters.Add(parameter);
            command.Parameters[0] = other;
            using var second = new OceanBaseCommand();
            second.Parameters.Add(parameter);
            Assert.Same(parameter, second.Parameters[0]);
        }

        [Fact]
        public void ParameterCollectionRejectsDuplicatesAndCrossCommandOwnership()
        {
            using var first = new OceanBaseCommand();
            using var second = new OceanBaseCommand();
            var parameter = new OceanBaseParameter { ParameterName = "x", Value = 1 };
            first.Parameters.Add(parameter);
            Assert.Throws<ArgumentException>(() => first.Parameters.Add(parameter));
            Assert.Throws<ArgumentException>(() => second.Parameters.Add(parameter));
            first.Parameters.Remove(parameter);
            second.Parameters.Add(parameter);
            Assert.Same(parameter, second.Parameters[0]);
        }
    }
}
