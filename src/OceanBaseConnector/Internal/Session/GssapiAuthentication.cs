using System;
using OceanBaseConnector.Internal.Protocol;
#if NET8_0_OR_GREATER
using System.Net.Security;
using System.Security.Principal;
#endif

namespace OceanBaseConnector.Internal.Session
{
    internal interface IGssapiContext : IDisposable
    {
        bool IsAuthenticated { get; }
        bool IsMutuallyAuthenticated { get; }
        string Package { get; }
        byte[]? GetOutgoingBlob(byte[] token, out bool completed);
    }

    internal sealed class GssapiAuthentication : IDisposable
    {
        private readonly IGssapiContext _context;
        private bool _completed, _disposed;

        internal GssapiAuthentication(IGssapiContext context)
        {
            _context = context;
            try { InitialResponse = Advance(Array.Empty<byte>()) ?? Array.Empty<byte>(); }
            catch { Dispose(); throw; }
        }

        internal static GssapiAuthentication Create(byte[] seed, string configuredPrincipal, Func<string, IGssapiContext>? contextFactory = null)
        {
            var principal = GssapiChallenge.Parse(seed, configuredPrincipal);
            if (contextFactory is not null) return new GssapiAuthentication(contextFactory(principal));
#if NET8_0_OR_GREATER
            return new GssapiAuthentication(new SystemContext(principal));
#else
            throw new OceanBaseException("GSSAPI authentication requires a net8.0 or later connector asset.");
#endif
        }

        internal byte[] InitialResponse { get; }
        internal bool IsComplete => _completed;

        internal byte[]? Advance(byte[] token)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GssapiAuthentication));
            if (_completed) throw new ProtocolException("Expected the final GSSAPI authentication result.");
            var output = _context.GetOutgoingBlob(token, out _completed);
            if (_completed) ValidateSuccess();
            return output;
        }

        internal void ValidateSuccess()
        {
            if (!_completed) throw new ProtocolException("Database authentication ended before GSSAPI completed.");
            if (!_context.IsAuthenticated || !_context.IsMutuallyAuthenticated ||
                !string.Equals(_context.Package, "Kerberos", StringComparison.OrdinalIgnoreCase))
                throw new OceanBaseException("GSSAPI requires mutually authenticated Kerberos credentials.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _context.Dispose();
        }

#if NET8_0_OR_GREATER
        private sealed class SystemContext : IGssapiContext
        {
            private readonly NegotiateAuthentication _authentication;
            internal SystemContext(string principal)
            {
                try
                {
                    _authentication = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                    {
                        Package = "Kerberos", TargetName = principal, RequireMutualAuthentication = true,
                        AllowedImpersonationLevel = TokenImpersonationLevel.Identification,
                    });
                }
                catch (Exception error) when (IsSystemFailure(error))
                { throw new OceanBaseException("System GSSAPI authentication is unavailable."); }
            }
            public bool IsAuthenticated => _authentication.IsAuthenticated;
            public bool IsMutuallyAuthenticated => _authentication.IsMutuallyAuthenticated;
            public string Package => _authentication.Package;
            public byte[]? GetOutgoingBlob(byte[] token, out bool completed)
            {
                byte[]? output; NegotiateAuthenticationStatusCode status;
                try { output = _authentication.GetOutgoingBlob(token, out status); }
                catch (Exception error) when (IsSystemFailure(error))
                { throw new OceanBaseException("System GSSAPI authentication failed."); }
                if (status != NegotiateAuthenticationStatusCode.Completed && status != NegotiateAuthenticationStatusCode.ContinueNeeded)
                    throw new OceanBaseException("System GSSAPI authentication failed.");
                completed = status == NegotiateAuthenticationStatusCode.Completed;
                return output;
            }
            public void Dispose() => _authentication.Dispose();
            private static bool IsSystemFailure(Exception error) => error is System.Security.Authentication.AuthenticationException ||
                error is ArgumentException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception ||
                error is PlatformNotSupportedException || error is System.Security.SecurityException ||
                error is DllNotFoundException || error is EntryPointNotFoundException;
        }
#endif
    }
}
