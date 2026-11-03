"""Regenerate strict UJIS/EUCJPMS digests from mysql-8.0.36/strings sources.
Usage: python UjisReference.py ctype-ujis.cc [UjisMappings.cs]
       python UjisReference.py ctype-eucjpms.cc [EucJpMsMappings.cs ctype-ujis.cc]
Optional outputs contain only numeric Unicode assignments or corrections.
"""
import hashlib
import pathlib
import re
import sys

source = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')
microsoft = 'jisx0208_eucjpms_to_unicode' in source
suffix = 'eucjpms' if microsoft else 'eucjp'


def table(name, text=source):
    body = re.search(name + r'\[65536\]\s*=\s*\{([^}]+)', text).group(1)
    body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
    values = [int(x, 16) for x in re.findall(r'0x[0-9a-fA-F]+', body)]
    assert len(values) == 65536
    return values


primary = table('jisx0208_' + suffix + '_to_unicode')
secondary = table('jisx0212_' + suffix + '_to_unicode')
encode_primary = table('unicode_to_jisx0208_' + suffix)
encode_secondary = table('unicode_to_jisx0212_' + suffix)


def decode(wire):
    if len(wire) == 1:
        return wire[0] if wire[0] < 128 else 0xffffffff
    if len(wire) == 2:
        if wire[0] == 0x8e and 0xa1 <= wire[1] <= 0xdf:
            return 0xfec0 + wire[1]
        if all(0xa1 <= b <= 0xfe for b in wire):
            return primary[wire[0] << 8 | wire[1]] or 0xffffffff
    elif wire[0] == 0x8f and all(0xa1 <= b <= 0xfe for b in wire[1:]):
        return secondary[wire[1] << 8 | wire[2]] or 0xffffffff
    return 0xffffffff


digest = hashlib.sha256()
for byte in range(256):
    wire = bytes([byte])
    digest.update(wire + decode(wire).to_bytes(4, 'big'))
for lead in range(0x80, 256):
    for trail in range(256):
        wire = bytes([lead, trail])
        digest.update(wire + decode(wire).to_bytes(4, 'big'))
for second in range(256):
    for third in range(256):
        wire = bytes([0x8f, second, third])
        digest.update(wire + decode(wire).to_bytes(4, 'big'))
print('Decode SHA256:', digest.hexdigest().upper())

digest = hashlib.sha256()
for scalar in range(65536):
    code = scalar if scalar < 128 else encode_primary[scalar]
    if scalar >= 128 and not code:
        code = 0x8f0000 | encode_secondary[scalar] if encode_secondary[scalar] else (
            0x8e00 | scalar - 0xfec0 if 0xff61 <= scalar <= 0xff9f else 0xffffffff)
    if code != 0xffffffff and decode(code.to_bytes(1 if code < 256 else 2 if code < 65536 else 3, 'big')) != scalar:
        code = 0xffffffff
    digest.update(scalar.to_bytes(2, 'big') + code.to_bytes(4, 'big'))
print('Encode SHA256:', digest.hexdigest().upper())

if len(sys.argv) > 2 and microsoft:
    baseline = pathlib.Path(sys.argv[3]).read_text(encoding='utf-8')
    lines = ['// Numeric EUCJPMS deltas from MySQL 8.0.36; see UjisReference.py.',
             'namespace OceanBaseConnector.Internal.Protocol', '{', '    internal static class EucJpMsMappings', '    {']
    for name, mapping, base_name, expected in [
            ('Primary', primary, 'jisx0208_eucjp_to_unicode', 90),
            ('Secondary', secondary, 'jisx0212_eucjp_to_unicode', 108)]:
        original = table(base_name, baseline)
        corrections = [(a << 8 | b, mapping[a << 8 | b]) for a in range(0xa1, 0xff) for b in range(0xa1, 0xff)
                       if mapping[a << 8 | b] != original[a << 8 | b]]
        assert len(corrections) == expected
        values = [value for pair in corrections for value in pair]
        lines.append('        internal const string ' + name + ' =')
        chunks = [values[i:i + 16] for i in range(0, len(values), 16)]
        for i, chunk in enumerate(chunks):
            lines.append('            "' + ''.join('\\u%04x' % value for value in chunk) + ('";' if i == len(chunks) - 1 else '" +'))
    lines.extend(['    }', '}', ''])
    pathlib.Path(sys.argv[2]).write_text('\n'.join(lines), encoding='utf-8')
elif len(sys.argv) > 2:
    values = [secondary[lead << 8 | trail] for lead in range(0xa1, 0xf5) for trail in range(0xa1, 0xff)]
    first = next(i for i, value in enumerate(values) if value)
    last = max(i for i, value in enumerate(values) if value)
    assert first == 108 and sum(bool(value) for value in values) == 6067
    lines = ['// Numeric JIS X 0212 assignments from MySQL 8.0.36; see UjisReference.py.',
             'namespace OceanBaseConnector.Internal.Protocol', '{', '    internal static class UjisMappings', '    {',
             '        // Grid positions A2AF through EDE3, 94 columns per row; zero denotes undefined.',
             '        internal const int FirstPosition = 108;', '        internal const string Jis0212 =']
    chunks = [values[i:min(i + 16, last + 1)] for i in range(first, last + 1, 16)]
    for i, chunk in enumerate(chunks):
        lines.append('            "' + ''.join('\\u%04x' % value for value in chunk) + ('";' if i == len(chunks) - 1 else '" +'))
    lines.extend(['    }', '}', ''])
    pathlib.Path(sys.argv[2]).write_text('\n'.join(lines), encoding='utf-8')
