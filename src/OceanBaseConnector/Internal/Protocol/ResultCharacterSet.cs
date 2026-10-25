using System;
using System.Globalization;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class ResultCharacterSet
    {
        // MySQL latin1 uses Windows-1252, including its C1 mappings, rather than ISO-8859-1.
        private static readonly Encoding Latin1 = CodePage(1252);
        private static readonly Encoding Gbk = new ChineseEncoding(936);
        private static readonly Encoding Gb18030 = new ChineseEncoding(54936);
        private static readonly Encoding Ascii = Encoding.GetEncoding(20127, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private static readonly Encoding Utf16Le = new UnicodeEncoding(false, false, true);

        private static Encoding CodePage(int number) => CodePagesEncodingProvider.Instance.GetEncoding(number,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? throw new NotSupportedException("The required character encoding is unavailable.");

        // OceanBase IDs follow v4.3.5_CE ob_charset.h; legacy UTF8MB3 IDs follow MySQL. Holes are not encodings.
        internal static bool IsUtf8(ushort collation) => IsUtf8Mb3(collation) || collation == 45 || collation == 46 ||
            collation >= 224 && collation <= 247 || collation >= 255 && collation <= 323 &&
            collation != 272 && collation != 276 && collation != 295 && collation != 299 && collation != 301 && collation != 302 ||
            collation == 325 || collation == 340 || collation == 355;

        private static bool IsUtf8Mb3(ushort collation) => collation == 33 || collation == 83 || collation >= 192 && collation <= 215;

        internal static Encoding Resolve(ushort collation, out uint maximumBytesPerCharacter)
        {
            maximumBytesPerCharacter = 4;
            if (IsUtf8(collation))
            {
                if (IsUtf8Mb3(collation)) maximumBytesPerCharacter = 3;
                return ResultColumn.Utf8;
            }
            if (collation >= 101 && collation <= 124) return NationalCharacterSet.Utf16;
            switch (collation)
            {
                case 11:
                case 65:
                case 331:
                case 346:
                case 361:
                    maximumBytesPerCharacter = 1; return Ascii;
                case 5:
                case 8:
                case 15:
                case 31:
                case 47:
                case 48:
                case 49:
                case 94:
                case 329:
                case 344:
                case 359:
                    maximumBytesPerCharacter = 1; return Latin1;
                case 28:
                case 87:
                case 326:
                case 341:
                case 356:
                    maximumBytesPerCharacter = 2; return Gbk;
                case 248: case 249: case 251: case 328: case 343: case 358: return Gb18030;
                case 54: case 55: case 327: case 342: case 357: return NationalCharacterSet.Utf16;
                case 56: case 62: case 333: case 348: case 363: return Utf16Le;
                default: throw new NotSupportedException("Unsupported result collation ID " + collation.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
    }
}
