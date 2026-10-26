import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.Charset;
import java.nio.charset.CodingErrorAction;
import java.security.MessageDigest;
import java.util.HexFormat;

// Independent fixtures; run: java tests/fixtures/character-sets/Gb180302022Reference.java
// Recorded with Temurin OpenJDK 21.0.12.1+1, GB18030 default revision2022.
class Gb180302022Reference {
    public static void main(String[] args) throws Exception {
        var charset = Charset.forName("GB18030");
        if (!charset.decode(ByteBuffer.wrap(new byte[] {(byte)0xfe, 0x59})).toString().equals("\u9fb4"))
            throw new IllegalStateException("This JDK is not using GB18030-2022");
        var encoder = charset.newEncoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT);
        var decoder = charset.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT);
        var hash = MessageDigest.getInstance("SHA-256");
        for (int scalar = 0; scalar <= 0xffff; scalar++) {
            if (scalar >= 0xd800 && scalar <= 0xdfff) continue;
            var encoded = encoder.encode(CharBuffer.wrap(new char[] {(char)scalar}));
            hash.update(new byte[] {(byte)(scalar >> 8), (byte)scalar, (byte)encoded.remaining()}); hash.update(encoded);
        }
        System.out.println("Runtime=" + Runtime.version());
        System.out.println("BMP encoding SHA256=" + HexFormat.of().formatHex(hash.digest()));
        for (int n = 0; n < 23940 + 39420; n++) {
            byte[] bytes;
            if (n < 23940) {
                int trail = n % 190 + 0x40; if (trail >= 0x7f) trail++;
                bytes = new byte[] {(byte)(n / 190 + 0x81), (byte)trail};
            } else {
                int x = n - 23940; bytes = new byte[4];
                bytes[3] = (byte)(x % 10 + 0x30); x /= 10;
                bytes[2] = (byte)(x % 126 + 0x81); x /= 126;
                bytes[1] = (byte)(x % 10 + 0x30); bytes[0] = (byte)(x / 10 + 0x81);
            }
            int scalar = -1;
            try { scalar = Character.codePointAt(decoder.decode(ByteBuffer.wrap(bytes)), 0); }
            catch (CharacterCodingException error) { }
            hash.update(bytes);
            hash.update(new byte[] {(byte)(scalar >> 24), (byte)(scalar >> 16), (byte)(scalar >> 8), (byte)scalar});
        }
        System.out.println("Two-byte/BMP four-byte decoding SHA256=" + HexFormat.of().formatHex(hash.digest()));
    }
}
