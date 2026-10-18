using System;
using System.Globalization;
using System.Numerics;

namespace OceanBaseConnector.Internal.Values
{
    internal static class DecimalCodec
    {
        internal static OceanBaseDecimal Parse(string text)
        {
            // Bound untrusted payloads before constructing a coefficient or expanding an exponent.
            if (text.Length == 0 || text.Length > 512) throw new FormatException("Invalid decimal value.");
            var exponentIndex = text.IndexOfAny(new[] { 'e', 'E' });
            var exponent = 0;
            if (exponentIndex >= 0)
            {
                if (!int.TryParse(text.Substring(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) || exponent < -256 || exponent > 256)
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
            return new OceanBaseDecimal(negative ? -coefficient : coefficient, fractional - exponent);
        }

        internal static void Validate(OceanBaseDecimal value, OceanBaseCompatibilityMode mode, bool unsigned = false)
        {
            if (value.Scale < -256 || value.Scale > 256) throw new OverflowException("The decimal scale exceeds the supported range.");
            if (unsigned && value.Coefficient.Sign < 0) throw new OverflowException("A negative value cannot be unsigned.");
            var digits = BigInteger.Abs(value.Coefficient).ToString(CultureInfo.InvariantCulture);
            if (mode == OceanBaseCompatibilityMode.MySql)
            {
                if (value.Scale > 30 || digits.Length + (value.Coefficient.IsZero ? 0 : Math.Max(0, -value.Scale)) > 65)
                    throw new OverflowException("The value exceeds the MySQL decimal range.");
            }
            else if (!value.Coefficient.IsZero)
            {
                var integerDigits = (long)digits.Length - value.Scale;
                if (digits.TrimEnd('0').Length > 38 || integerDigits < -129 || integerDigits > 126)
                    throw new OverflowException("The value exceeds the Oracle NUMBER range.");
            }
        }

        internal static string Format(OceanBaseDecimal value)
        {
            var digits = BigInteger.Abs(value.Coefficient).ToString(CultureInfo.InvariantCulture);
            var sign = value.Coefficient.Sign < 0 ? "-" : "";
            if (value.Scale <= 0) return sign + digits + (value.Coefficient.IsZero ? "" : new string('0', -value.Scale));
            digits = digits.PadLeft(value.Scale + 1, '0');
            return sign + digits.Insert(digits.Length - value.Scale, ".");
        }

        internal static OceanBaseDecimal Constrain(OceanBaseDecimal value, int precision, int scale, bool unsigned = false)
        {
            if (precision < 0 || precision > 65 || scale < -256 || scale > 256)
                throw new OverflowException("The decimal metadata exceeds the supported range.");
            var difference = (long)scale - value.Scale;
            if (difference < -256 || difference > 256) throw new OverflowException("The decimal scale exceeds the supported range.");
            var coefficient = value.Coefficient;
            if (difference >= 0) coefficient *= BigInteger.Pow(10, (int)difference);
            else
            {
                coefficient = BigInteger.DivRem(coefficient, BigInteger.Pow(10, (int)-difference), out var remainder);
                if (!remainder.IsZero) throw new OverflowException("The decimal scale would require rounding.");
            }
            if (unsigned && coefficient.Sign < 0 || precision > 0 && BigInteger.Abs(coefficient) >= BigInteger.Pow(10, precision))
                throw new OverflowException("The value exceeds the declared decimal precision.");
            return new OceanBaseDecimal(coefficient, scale);
        }
    }
}
