namespace OceanBaseConnector
{
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
    /// OceanBase compatibility mode of a session. The effective mode is
    /// determined by the server during authentication; this value is only the
    /// client-side expectation used to reject a mismatched session.
    /// </summary>
    public enum OceanBaseCompatibilityMode
    {
        /// <summary>MySQL compatibility mode.</summary>
        MySql = 0,

        /// <summary>Oracle compatibility mode.</summary>
        Oracle = 1,
    }
}
