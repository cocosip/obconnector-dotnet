#if NET8_0_OR_GREATER
using Microsoft.Extensions.Logging;
using OceanBaseConnector.Internal;

namespace OceanBaseConnector
{
    public sealed class OceanBaseDataSourceBuilder
    {
        private ILoggerFactory? _loggerFactory;
        private IOceanBaseAddressResolver? _addressResolver;
        public OceanBaseDataSourceBuilder(string connectionString = "") => ConnectionStringBuilder = new OceanBaseConnectionStringBuilder(connectionString);
        public OceanBaseConnectionStringBuilder ConnectionStringBuilder { get; }
        public OceanBaseDataSourceBuilder UseLoggerFactory(ILoggerFactory? loggerFactory) { _loggerFactory = loggerFactory; return this; }
        /// <summary>Use a caller-owned, concurrency-safe address source for each connection Open.</summary>
        public OceanBaseDataSourceBuilder UseAddressResolver(IOceanBaseAddressResolver? resolver) { _addressResolver = resolver; return this; }
        public OceanBaseDataSource Build() => new OceanBaseDataSource(ConnectionStringBuilder.Build(), ConnectionStringBuilder.ConnectionString, new ConnectionLog(_loggerFactory), _addressResolver);
    }
}
#endif
