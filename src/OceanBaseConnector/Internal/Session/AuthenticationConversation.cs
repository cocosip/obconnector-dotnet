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
        private bool _awaitingPublicKey;
        private byte[] _seed;

        public AuthenticationConversation(OceanBaseConnectionOptions options, string? pluginName, byte[] seed)
        {
            _options = options;
            _seed = (byte[])seed.Clone();
            PluginName = pluginName ?? InitialHandshakePacket.NativePasswordPluginName;
            InitialResponse = BuildResponse(seed);
        }

        public string PluginName { get; private set; }
        public byte[] InitialResponse { get; }

        public void ValidateSuccess()
        {
            if (_awaitingPublicKey) throw new ProtocolException("Authentication ended before the requested RSA public key arrived.");
        }

        /// <summary>Null means read the next server packet without writing, unlike an empty response.</summary>
        public byte[]? Continue(byte[] packet)
        {
            if (packet.Length != 0 && packet[0] == 0xFE && !_awaitingFinalResult)
            {
                if (_switched) throw new ProtocolException("Repeated authentication plugin switch.");
                _switched = true; _awaitingPublicKey = false;
                if (packet.Length == 1)
                {
                    PluginName = OldPassword.PluginName;
                    return BuildResponse(_seed);
                }
                var request = AuthSwitchRequest.Parse(packet);
                PluginName = request.PluginName;
                return BuildResponse(request.Seed);
            }
            if (_awaitingPublicKey)
            {
                if (packet.Length < 2 || packet.Length > 16386 || packet[0] != 1)
                    throw new ProtocolException("Expected an RSA authentication public key.");
                var length = packet.Length - 1;
                if (packet[packet.Length - 1] == 0) length--;
                if (length > 16384) throw new ProtocolException("RSA public key exceeds 16 KiB.");
                for (var i = 1; i <= length; i++)
                    if (packet[i] > 127) throw new ProtocolException("RSA public key PEM must be ASCII.");
                var response = RsaPassword.Encrypt(TerminatedPassword(), _seed, Encoding.ASCII.GetString(packet, 1, length));
                _awaitingPublicKey = false; _awaitingFinalResult = true;
                return response;
            }
            if (packet.Length == 0 || _awaitingFinalResult)
                throw new ProtocolException("Expected a final authentication OK or ERR packet.");

            if (PluginName != CachingSha2 || packet.Length != 2 || packet[0] != 1 ||
                (packet[1] != 3 && packet[1] != 4))
                throw new ProtocolException("Unexpected authentication continuation packet.");

            _awaitingFinalResult = true;
            return packet[1] == 3 ? null : FullSha2Password(2);
        }

        private byte[] BuildResponse(byte[] seed)
        {
            switch (PluginName)
            {
                case InitialHandshakePacket.NativePasswordPluginName:
                    return NativePassword.EncryptPassword(_options.Password, seed);
                case OldPassword.PluginName:
                    RequireVerifiedTls();
                    return OldPassword.Respond(_options.Password, seed);
                case InitialHandshakePacket.ClearPasswordPluginName:
                    RequireVerifiedTls();
                    return Encoding.UTF8.GetBytes(_options.Password + "\0");
                case Sha256:
                    SetSeed(seed);
                    return FullSha2Password(1);
                case CachingSha2:
                    SetSeed(seed);
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

        private void SetSeed(byte[] seed)
        {
            if (seed.Length != 20) throw new ProtocolException("SHA-2 authentication requires a 20-byte challenge.");
            _seed = (byte[])seed.Clone();
        }

        private byte[] FullSha2Password(byte keyRequest)
        {
            if (_options.Password.Length == 0) return new byte[] { 0 };
            if (_options.TlsMode == OceanBaseTlsMode.VerifyFull) return TerminatedPassword();
            if (_options.ServerRsaPublicKey.Length != 0)
                return RsaPassword.Encrypt(TerminatedPassword(), _seed, _options.ServerRsaPublicKey);
            if (_options.AllowPublicKeyRetrieval)
            {
                _ = EncodeSha2Password();
                _awaitingPublicKey = true;
                return new[] { keyRequest };
            }
            throw new OceanBaseException($"SHA-2 authentication at {_options.Endpoint} requires TLS Mode=VerifyFull or an explicitly configured RSA public key or public key retrieval.");
        }

        private byte[] TerminatedPassword()
        {
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
