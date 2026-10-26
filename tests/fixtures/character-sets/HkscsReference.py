"""Pinned OceanBase 5d6cb5c HKSCS/HKSCS31 digests and optional compact delta regeneration.
python HkscsReference.py ob_ctype_hkscs_tab.h ob_ctype_hkscs31_tab.h
Add --windows950 dump.bin --output HkscsMappings.cs to regenerate product numeric deltas.
ExportWindows950.ps1 creates the optional strict CP950 dump; neither tool is a runtime dependency.
"""
import argparse
import hashlib
import pathlib
import re
import struct

parser = argparse.ArgumentParser()
parser.add_argument('hkscs', type=pathlib.Path)
parser.add_argument('hkscs31', type=pathlib.Path)
parser.add_argument('--windows950', type=pathlib.Path)
parser.add_argument('--output', type=pathlib.Path)
args = parser.parse_args()


def mappings(path, name):
    source = path.read_text(encoding='utf-8')

    def table(identifier):
        body = re.search(r'\b' + identifier + r'\[\]\s*=\s*\{(.*?)\};', source, re.S).group(1)
        pairs = [tuple(int(v, 0) for v in match) for match in
                 re.findall(r'\{\s*(0x[0-9a-fA-F]+|\d+)\s*,\s*(0x[0-9a-fA-F]+|\d+)\s*\}', body)]
        assert len(pairs) == len(dict(pairs))
        return dict(pairs)

    decode = table(name + '_to_uni_map_array')
    encode = table('uni_to_' + name + '_map_array')
    assert len(decode) == 19910 and len(encode) == 19756
    assert all(decode[n] == encode[n] == n for n in range(128))
    return decode, encode


families = [mappings(args.hkscs, 'hkscs'), mappings(args.hkscs31, 'hkscs31')]
for name, (decode, encode) in zip(['HKSCS', 'HKSCS31'], families):
    digest = hashlib.sha256()
    for byte in range(256):
        scalar = byte if byte < 128 else 0xffffffff
        digest.update(bytes([byte]) + scalar.to_bytes(4, 'big'))
    for lead in range(128, 256):
        for trail in range(256):
            scalar = decode.get((lead << 8) + trail, 0)
            if scalar in (0, 0xfffd):
                scalar = 0xffffffff
            digest.update(bytes([lead, trail]) + scalar.to_bytes(4, 'big'))
    print(name, 'decode SHA256:', digest.hexdigest().upper())
    for label, start, end, width in [('BMP', 0, 65536, 2), ('Plane2', 0x20000, 0x30000, 4)]:
        digest = hashlib.sha256()
        for scalar in range(start, end):
            code = encode.get(scalar, 0xffffffff) if scalar != 0xfffd else 0xffffffff
            digest.update(scalar.to_bytes(width, 'big') + code.to_bytes(4, 'big'))
        print(name, label, 'encode SHA256:', digest.hexdigest().upper())

if args.output:
    if not args.windows950:
        parser.error('--output requires --windows950')
    windows = struct.unpack('<65536i', args.windows950.read_bytes())
    old, revised = families[0][0], families[1][0]
    undefined = [code for code, scalar in sorted(old.items()) if scalar == 0xfffd]
    bmp = [(code, scalar) for code, scalar in sorted(old.items())
           if code >= 256 and scalar != 0xfffd and windows[code] != scalar]
    supplementary = [(code, scalar - 0x20000) for code, scalar in sorted(revised.items()) if scalar != old[code]]
    assert len(undefined) == 62 and len(supplementary) == 1651
    assert all(0xe000 <= old[code] <= 0xf8ff and 0 <= low <= 65535 for code, low in supplementary)
    preferences = []
    for decode, encode in families:
        first = {}
        for code, scalar in sorted(decode.items()):
            if scalar != 0xfffd:
                first.setdefault(scalar, code)
        preferences.append(sorted(code for scalar, code in encode.items()
                                  if scalar != 0xfffd and scalar in first and first[scalar] != code))
        aliases = [(scalar, code) for scalar, code in encode.items()
                   if scalar != 0xfffd and decode[code] != scalar]
        assert aliases == [(0x2593, 0xf9fe)]
    assert preferences[0] == preferences[1] and len(preferences[0]) == 66

    def literal(name, values):
        text = '        internal const string ' + name + ' =\n'
        for index in range(0, len(values), 16):
            chunk = ''.join('\\u%04x' % value for value in values[index:index + 16])
            text += '            "' + chunk + ('";\n' if index + 16 >= len(values) else '" +\n')
        return text

    output = '// Generated numeric deltas from OceanBase 5d6cb5c; see HkscsReference.py.\n'
    output += 'namespace OceanBaseConnector.Internal.Protocol\n{\n    internal static class HkscsMappings\n    {\n'
    output += literal('UndefinedCodes', undefined) + '\n'
    output += literal('BmpCorrections', [value for pair in bmp for value in pair]) + '\n'
    output += literal('Plane2Corrections', [value for pair in supplementary for value in pair]) + '\n'
    output += literal('PreferredCodes', preferences[0])
    output += '    }\n}\n'
    args.output.write_text(output, encoding='utf-8', newline='\n')
    print('Generated BMP corrections:', len(bmp), '; Plane2 corrections:', len(supplementary))
