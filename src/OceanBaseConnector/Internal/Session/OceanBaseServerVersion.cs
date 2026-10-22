using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OceanBaseConnector.Internal.Session
{
    internal static class OceanBaseServerVersion
    {
        // A MySQL-compatible greeting prefix must never become the product version.
        private static readonly Regex Format = new Regex(
            @"\A(?:(?:[0-9]+\.[0-9]+\.[0-9]+-)?(OceanBase(?:_CE|_EE)?[ _-]+))?v?([0-9]+)\.([0-9]+)\.([0-9]+)(?:\.([0-9]+))?(?:[-+ ][\x20-\x7e]+)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static Version? Parse(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 128) return null;
            var marker = value.IndexOf("OceanBase", StringComparison.OrdinalIgnoreCase);
            var match = Format.Match(value);
            if (!match.Success || marker >= 0 && (!match.Groups[1].Success ||
                value.IndexOf("OceanBase", marker + "OceanBase".Length, StringComparison.OrdinalIgnoreCase) >= 0)) return null;
            var parts = new int[4];
            for (var i = 0; i < parts.Length; i++)
            {
                var text = match.Groups[i + 2].Value;
                if (i == 3 && text.Length == 0) continue;
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return null;
            }
            return parts[0] == 0 ? null : new Version(parts[0], parts[1], parts[2], parts[3]);
        }
    }
}
