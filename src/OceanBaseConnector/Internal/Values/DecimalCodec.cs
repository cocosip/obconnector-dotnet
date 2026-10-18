using System;
using System.Globalization;
using System.Numerics;

namespace OceanBaseConnector.Internal.Values
{
    internal static class DecimalCodec
    {
        internal static OceanBaseDecimal Parse(string text, int precision, int scale, bool unsigned)
        {
            // A supported 28-digit column never needs an unbounded numeric parser.
            if (text.Length == 0 || text.Length > 128) throw new FormatException("Invalid decimal value.");
            var exponentIndex = text.IndexOfAny(new[] { 'e', 'E' });
            var exponent = 0;
            if (exponentIndex >= 0)
            {
                if (!int.TryParse(text.Substring(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) || exponent < -128 || exponent > 128)
                    throw new FormatException("Invalid decimal exponent.");
                text = text.Substring(0, exponentIndex);
            }
            var negative = text.StartsWith("-", StringComparison.Ordinal);
            if (negative || text.StartsWith("+", StringComparison.Ordinal)) text = text.Substring(1);
            var coefficient = BigInteger.Zero;
            var fractional = 0;
            var point = false;
            var digits = 0;
            foreach (var character in text)
            {
                if (character == '.' && !point) { point = true; continue; }
                if (character < '0' || character > '9') throw new FormatException("Invalid decimal value.");
                coefficient = coefficient * 10 + character - '0';
                if (point) fractional++;
                digits++;
            }
            if (digits == 0) throw new FormatException("Invalid decimal value.");
            return Constrain(new OceanBaseDecimal(negative ? -coefficient : coefficient, fractional - exponent), precision, scale, unsigned);
        }

        internal static OceanBaseDecimal Constrain(OceanBaseDecimal value, int precision, int scale, bool unsigned = false)
        {
            var difference = (long)scale - value.Scale;
            if (difference < -256 || difference > 256) throw new OverflowException("The decimal scale exceeds the supported range.");
            var coefficient = value.Coefficient;
            if (difference >= 0) coefficient *= BigInteger.Pow(10, (int)difference);
            else
            {
                coefficient = BigInteger.DivRem(coefficient, BigInteger.Pow(10, (int)-difference), out var remainder);
                if (!remainder.IsZero) throw new OverflowException("The decimal scale would require rounding.");
            }
            if (unsigned && coefficient.Sign < 0 || BigInteger.Abs(coefficient) >= BigInteger.Pow(10, precision))
                throw new OverflowException("The value exceeds the declared decimal precision.");
            return new OceanBaseDecimal(coefficient, scale);
        }
    }
}
