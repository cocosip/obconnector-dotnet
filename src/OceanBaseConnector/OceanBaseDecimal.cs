using System;
using System.Globalization;
using System.Numerics;

namespace OceanBaseConnector
{
    /// <summary>An exact decimal coefficient and scale; the value is Coefficient * 10^-Scale.</summary>
    public readonly struct OceanBaseDecimal
    {
        public OceanBaseDecimal(BigInteger coefficient, int scale) { Coefficient = coefficient; Scale = scale; }
        public OceanBaseDecimal(decimal value)
        {
            var bits = decimal.GetBits(value);
            var magnitude = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
            Coefficient = bits[3] < 0 ? -magnitude : magnitude;
            Scale = (bits[3] >> 16) & 255;
        }
        public BigInteger Coefficient { get; }
        public int Scale { get; }

        /// <summary>Converts exactly, throwing OverflowException instead of rounding.</summary>
        public decimal ToDecimal()
        {
            var magnitude = BigInteger.Abs(Coefficient);
            var scale = Scale;
            var maximum = (BigInteger.One << 96) - 1;
            if (magnitude.IsZero) return new decimal(0, 0, 0, false, (byte)Math.Min(28, Math.Max(0, scale)));
            if (scale < 0)
            {
                if (scale < -28) throw new OverflowException("The value cannot be represented as decimal.");
                magnitude *= BigInteger.Pow(10, -scale);
                scale = 0;
            }
            while (scale > 0 && (scale > 28 || magnitude > maximum))
            {
                magnitude = BigInteger.DivRem(magnitude, 10, out var remainder);
                if (!remainder.IsZero) throw new OverflowException("The value cannot be represented exactly as decimal.");
                scale--;
            }
            if (magnitude > maximum) throw new OverflowException("The value cannot be represented as decimal.");
            return new decimal(unchecked((int)(uint)(magnitude & uint.MaxValue)),
                unchecked((int)(uint)((magnitude >> 32) & uint.MaxValue)),
                unchecked((int)(uint)(magnitude >> 64)), Coefficient.Sign < 0, (byte)scale);
        }

        public override string ToString()
        {
            var digits = BigInteger.Abs(Coefficient).ToString(CultureInfo.InvariantCulture);
            var sign = Coefficient.Sign < 0 ? "-" : "";
            // Large exponents stay bounded without allocating a string proportional to Scale.
            if (Scale < 0 || Scale > 28) return Coefficient.ToString(CultureInfo.InvariantCulture) + "E" + (-(long)Scale).ToString(CultureInfo.InvariantCulture);
            if (Scale == 0) return sign + digits;
            digits = digits.PadLeft(Scale + 1, '0');
            return sign + digits.Insert(digits.Length - Scale, ".");
        }
    }
}
