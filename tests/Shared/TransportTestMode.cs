namespace OceanBaseConnector.Tests;

internal static class TransportTestMode
{
#if SECURITY_ACCEPTANCE
    internal const bool Encrypted = true;
#else
    internal const bool Encrypted = false;
#endif
}
