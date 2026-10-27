using System;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseProviderFactory : DbProviderFactory
    {
        public static readonly OceanBaseProviderFactory Instance = new OceanBaseProviderFactory();
        private OceanBaseProviderFactory() { }
        public override DbConnection CreateConnection() => new OceanBaseConnection();
        public override DbCommand CreateCommand() => new OceanBaseCommand();
        public override DbParameter CreateParameter() => new OceanBaseParameter();
        public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new OceanBaseConnectionStringBuilder();
#if NET8_0_OR_GREATER
        public override bool CanCreateBatch => true;
        public override DbBatch CreateBatch() => new OceanBaseBatch();
        public override DbBatchCommand CreateBatchCommand() => new OceanBaseBatchCommand();
        public override DbDataSource CreateDataSource(string connectionString) => new OceanBaseDataSourceBuilder(connectionString).Build();
#endif
    }
}
