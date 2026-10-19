using System;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    /// <summary>Authentication decisions only; constructed after the selected TLS upgrade succeeds.</summary>
    internal sealed class AuthenticationConversation
    {
        internal const string CachingSha2 = "caching_sha2_password";
        internal const string Sha256 = "sha256_password";
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly OceanBaseConnectionOptions _options;
        private bool _switched;
        private bool _awaitingFinalResult;

        public AuthenticationConversation(OceanBaseConnectionOptions options, string? pluginName, byte[] seed)
        {
            _options = options;
            PluginName = pluginName ?? InitialHandshakePacket.NativePasswordPluginName;
            InitialResponse = BuildResponse(seed);
        }

        public string PluginName { get; private set; }
        public byte[] InitialResponse { get; }

        /// <summary>Null means read the next server packet without writing, unlike an empty response.</summary>
        public byte[]? Continue(byte[] packet)
        {
            if (packet.Length == 0 || _awaitingFinalResult)
                throw new ProtocolException("Expected a final authentication OK or ERR packet.");

            if (packet[0] == 0xFE)
            {
                if (_switched) throw new ProtocolException("Repeated authentication plugin switch.");
                _switched = true;
                var request = AuthSwitchRequest.Parse(packet);
                PluginName = request.PluginName;
                return BuildResponse(request.Seed);
            }

            if (PluginName != CachingSha2 || packet.Length != 2 || packet[0] != 1 ||
                (packet[1] != 3 && packet[1] != 4))
                throw new ProtocolException("Unexpected authentication continuation packet.");

            _awaitingFinalResult = true;
            return packet[1] == 3 ? null : ClearSha2Password();
        }

        private byte[] BuildResponse(byte[] seed)
        {
            switch (PluginName)
            {
                case InitialHandshakePacket.NativePasswordPluginName:
                    return NativePassword.EncryptPassword(_options.Password, seed);
                case InitialHandshakePacket.ClearPasswordPluginName:
                    RequireVerifiedTls();
                    return Encoding.UTF8.GetBytes(_options.Password + "\0");
                case Sha256:
                    return ClearSha2Password();
                case CachingSha2:
                    if (seed.Length != 20) throw new ProtocolException("SHA-2 authentication requires a 20-byte challenge.");
                    var password = EncodeSha2Password();
                    if (password.Length == 0) return Array.Empty<byte>();
                    using (var hash = SHA256.Create())
                    {
                        var stage1 = hash.ComputeHash(password);
                        var stage2 = hash.ComputeHash(stage1);
                        var input = new byte[stage2.Length + seed.Length];
                        Buffer.BlockCopy(stage2, 0, input, 0, stage2.Length);
                        Buffer.BlockCopy(seed, 0, input, stage2.Length, seed.Length);
                        var digest = hash.ComputeHash(input);
                        for (var i = 0; i < stage1.Length; i++) stage1[i] ^= digest[i];
                        return stage1;
                    }
                default:
                    throw new OceanBaseException($"Authentication plugin requested by {_options.Endpoint} is not supported.");
            }
        }

        private byte[] ClearSha2Password()
        {
            RequireVerifiedTls();
            var password = EncodeSha2Password();
            var response = new byte[password.Length + 1];
            Buffer.BlockCopy(password, 0, response, 0, password.Length);
            return response;
        }

        private byte[] EncodeSha2Password()
        {
            if (_options.Password.IndexOf('\0') >= 0)
                throw new OceanBaseException("SHA-2 authentication passwords cannot contain NUL characters.");
            try { return StrictUtf8.GetBytes(_options.Password); }
            catch (EncoderFallbackException)
            {
                throw new OceanBaseException("SHA-2 authentication requires a valid Unicode password.");
            }
        }

        private void RequireVerifiedTls()
        {
            if (_options.TlsMode != OceanBaseTlsMode.VerifyFull)
                throw new OceanBaseException($"Password authentication at {_options.Endpoint} requires TLS Mode=VerifyFull; refusing to send the password over a weaker channel.");
        }
    }
}
