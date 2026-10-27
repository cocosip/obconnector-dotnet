using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("4.4.1.0", false, false)]
        [InlineData("4.4.1.0", false, true)]
        [InlineData("4.4.1.0", true, false)]
        [InlineData("4.4.1.0", true, true)]
        [InlineData("4.3.5.0", false, false)]
        [InlineData("4.3.5.0", false, true)]
        [InlineData("4.3.5.0", true, false)]
        [InlineData("4.3.5.0", true, true)]
        public async Task ClobLocatorBindingsUseClientBytesOnlyForConvertingServerLayouts(string version, bool v2, bool array)
        {
            var samples = ClientCharacterSetFixture.Samples;
            var server = Server(async channel =>
            {
                foreach (var sample in samples)
                    await LobQueryReply(channel, 211, ClobEncodingLocator(sample.Text, v2));
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: version);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync();
                foreach (var sample in samples)
                {
                    using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                    using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                    var original = ClobEncodingLocator(sample.Text, v2);
                    var payload = v2 && version == "4.3.5.0" ? Encoding.UTF8.GetBytes(sample.Text) : Convert.FromHexString(sample.Hex);
                    var expected = ClobEncodingLocator(sample.Text, v2, payload);
                    var encoding = ClientCharacterSet.Parse(sample.Name);
                    for (var repeat = 0; repeat < 2; repeat++)
                    {
                        var actual = array ? ComplexValueCodec.EncodeParameter(new ComplexType(true, new ComplexType(211)),
                            new object?[] { lob }, OceanBaseCompatibilityMode.Oracle, clientEncoding: encoding, lobLocators: true)
                            : PreparedParameters.Encode(new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Clob, Value = lob } },
                                OceanBaseCompatibilityMode.Oracle, clientEncoding: encoding);
                        Assert.Equal((array ? new byte[] { 1, 0 } : new byte[] { 0, 1, 211, 0 }).Concat(WithLength(expected)), actual);
                        Assert.Equal(original, lob.BindingBytes());
                        Assert.Equal(Encoding.UTF8.GetByteCount(sample.Text), lob.ByteLength);
                    }
                    using var text = lob.OpenTextReader(); Assert.Equal(sample.Text, await text.ReadToEndAsync());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static byte[] ClobEncodingLocator(string text, bool v2, byte[]? payload = null)
        {
            payload ??= Encoding.UTF8.GetBytes(text);
            if (!v2) return InlineLob(payload);
            var bytes = new byte[36 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x18401);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x2102);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x8501);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(28), (ulong)payload.Length);
            payload.CopyTo(bytes, 36); return bytes;
        }

        [Fact]
        public async Task LobEncodingLeavesBlobExternalAndDefaultUtf8BytesOpaque()
        {
            var invalid = InlineLob(new byte[] { 0xff, 0xc3 }); var external = ExternalLob(90000);
            var server = Server(async channel =>
            {
                await LobQueryReply(channel, 210, invalid);
                await LobQueryReply(channel, 211, external);
                await LobQueryReply(channel, 211, invalid);
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync();
                foreach (var index in new[] { 0, 1, 2 })
                {
                    using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                    using var lob = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                    var original = lob.BindingBytes();
                    Assert.Same(original, lob.BindingBytes(ResultColumn.Utf8));
                    Assert.Same(original, lob.BindingBytes(ClientCharacterSet.Parse("utf8mb4")));
                    if (index < 2) Assert.Same(original, lob.BindingBytes(ClientCharacterSet.Parse("ascii")));
                    else Assert.Throws<DecoderFallbackException>(() => lob.BindingBytes(ClientCharacterSet.Parse("ascii")));
                    Assert.Equal(index == 1 ? external : invalid, lob.BindingBytes());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("trim", false, false)]
        [InlineData("trim", true, false)]
        [InlineData("trim", false, true)]
        [InlineData("trim", true, true)]
        [InlineData("write", false, false)]
        [InlineData("write", true, false)]
        [InlineData("write", false, true)]
        [InlineData("write", true, true)]
        [InlineData("free", false, false)]
        [InlineData("free", true, false)]
        [InlineData("free", false, true)]
        [InlineData("free", true, true)]
        public async Task ClobMutationEncodingFailurePreservesReadableHandleBeforeDispatch(string operation, bool asynchronous, bool v2)
        {
            const string text = "患者";
            var bytes = ClobEncodingLocator(text, v2);
            var server = Server(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "ascii", true, false, CancellationToken.None);
                channel.BeginCommand(); await LobQueryReply(channel, 211, bytes);
                var request = await ClientCharacterSetFixture.Read(channel, true, "ascii", false, CancellationToken.None);
                if (request[0] == 3 && Encoding.ASCII.GetString(request, 1, request.Length - 1) == ClientCharsetStateSql + " FROM DUAL")
                {
                    await ClientCharacterSetFixture.State(channel, "ascii", true, false, CancellationToken.None);
                    request = await ClientCharacterSetFixture.Read(channel, true, "ascii", false, CancellationToken.None);
                }
                Assert.Equal(3, request[0]); Assert.Equal("after", Encoding.ASCII.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(Ok(1));
            }, lobLocators: true, serverVersion: "4.4.1.0");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Character Set=ascii");
            try
            {
                await connection.OpenAsync();
                using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                using var fetched = Assert.IsType<OceanBaseLob>(await source.ExecuteScalarAsync());
                using var lob = new OceanBaseLob(fetched.Context, fetched.BindingBytes(), true, ownsTemporary: true);
                await Assert.ThrowsAsync<EncoderFallbackException>(async () =>
                {
                    if (operation == "trim") { if (asynchronous) await lob.TruncateAsync(0); else lob.Truncate(0); }
                    else if (operation == "write") { if (asynchronous) await lob.WriteTextAsync(0, "OK"); else lob.WriteText(0, "OK"); }
                    else { if (asynchronous) await lob.FreeTemporaryAsync(); else lob.FreeTemporary(); }
                });
                Assert.Equal(bytes, lob.BindingBytes());
                Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = lob } },
                    OceanBaseCompatibilityMode.Oracle, clientEncoding: ClientCharacterSet.Parse("ascii")));
                using (var reader = lob.OpenTextReader()) Assert.Equal(text, await reader.ReadToEndAsync());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
