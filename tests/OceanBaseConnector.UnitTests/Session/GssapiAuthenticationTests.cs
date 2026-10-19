using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class GssapiAuthenticationTests
    {
        [Fact]
        public void GssapiKeepsRawTokensAndRequiresMutualCompletionBeforeDatabaseOk()
        {
            var context = new ScriptedContext();
            using (var auth = new GssapiAuthentication(context))
            {
                Assert.Equal(new byte[] { 96, 2, 1, 0 }, auth.InitialResponse);
                Assert.Throws<ProtocolException>(auth.ValidateSuccess);
                Assert.Equal(new byte[] { 111, 1, 0 }, auth.Advance(new byte[] { 111, 2, 0, 255 }));
                auth.ValidateSuccess();
                Assert.Throws<ProtocolException>(() => auth.Advance(new byte[] { 1 }));
                Assert.Empty(context.Inputs[0]);
                Assert.Equal(new byte[] { 111, 2, 0, 255 }, context.Inputs[1]);
            }
            Assert.True(context.Disposed);
        }

        [Theory]
        [InlineData(false, true, "Kerberos")]
        [InlineData(true, false, "Kerberos")]
        [InlineData(true, true, "NTLM")]
        public void GssapiRejectsIncompleteIdentityGuarantees(bool authenticated, bool mutual, string package)
        {
            var context = new ScriptedContext { Authenticated = authenticated, Mutual = mutual, NegotiatedPackage = package };
            using var auth = new GssapiAuthentication(context);
            Assert.Throws<OceanBaseException>(() => auth.Advance(new byte[] { 111 }));
        }

        [Fact]
        public void GssapiDisposesItsContextWhenInitialTokenFails()
        {
            var context = new ScriptedContext { Fail = true };
            Assert.Throws<OceanBaseException>(() => new GssapiAuthentication(context));
            Assert.True(context.Disposed);
        }

        [Fact]
        public void GssapiCanCompleteWithoutSendingAnotherToken()
        {
            var context = new ScriptedContext { NoFinalToken = true };
            var auth = new GssapiAuthentication(context);
            Assert.Null(auth.Advance(new byte[] { 111 }));
            auth.ValidateSuccess();
            auth.Dispose(); auth.Dispose();
            Assert.Throws<ObjectDisposedException>(() => auth.Advance(new byte[] { 1 }));
        }

        // Deterministic platform boundary, not a Kerberos/KDC implementation or interoperability fixture.
        private sealed class ScriptedContext : IGssapiContext
        {
            public readonly List<byte[]> Inputs = new List<byte[]>();
            public bool Authenticated = true, Mutual = true, Fail, NoFinalToken, Disposed;
            public string NegotiatedPackage = "Kerberos";
            public bool IsAuthenticated => Authenticated;
            public bool IsMutuallyAuthenticated => Mutual;
            public string Package => NegotiatedPackage;
            public byte[]? GetOutgoingBlob(byte[] token, out bool completed)
            {
                if (Fail) throw new OceanBaseException("System authentication failed.");
                Inputs.Add((byte[])token.Clone()); completed = Inputs.Count == 2;
                return completed ? NoFinalToken ? null : new byte[] { 111, 1, 0 } : new byte[] { 96, 2, 1, 0 };
            }
            public void Dispose() { Disposed = true; }
        }
    }
}
