namespace OceanBaseConnector
{
    /// <summary>Initial endpoint selection for each new physical session.</summary>
    public enum OceanBaseLoadBalance
    {
        /// <summary>Try addresses in their supplied order (default).</summary>
        Sequential = 0,
        /// <summary>Rotate the first address per connection or shared data source.</summary>
        RoundRobin = 1,
    }

    /// <summary>
    /// TLS behavior for a connection. The default is <see cref="VerifyFull"/>,
    /// which encrypts and validates the certificate chain plus the endpoint
    /// (or the explicit TLS Server Name) against the OS trust store.
    /// </summary>
    public enum OceanBaseTlsMode
    {
        /// <summary>Encrypt and verify the certificate chain and host name (default).</summary>
        VerifyFull = 0,

        /// <summary>Encrypt without identity verification; must be chosen explicitly.</summary>
        Required = 1,

        /// <summary>Cleartext connection; must be chosen explicitly.</summary>
        Disabled = 2,
    }

    /// <summary>
    /// OceanBase compatibility mode. Auto is a connection configuration option;
    /// authenticated sessions report MySql or Oracle.
    /// </summary>
    public enum OceanBaseCompatibilityMode
    {
        /// <summary>Automatically detect the tenant mode (connection configuration default only).</summary>
        Auto = 0,

        /// <summary>MySQL compatibility mode.</summary>
        MySql = 1,

        /// <summary>Oracle compatibility mode.</summary>
        Oracle = 2,
    }
}
