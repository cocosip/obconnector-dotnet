"""Strict result digests from OceanBase 5d6cb5c ob_ctype_big5_tab.h.
Usage: python Big5Reference.py ob_ctype_big5_tab.h
"""
import hashlib
import pathlib
import re
import sys

source = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')


def table(name):
    body = re.search(r'\b' + name + r'\[\]\s*=\s*\{([^}]+)', source).group(1)
    body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
    return [int(x.strip(), 0) for x in body.split(',') if x.strip()]


decode = [0] * 65536
encode = [0] * 65536
for scalar in range(128):
    decode[scalar] = encode[scalar] = scalar
for name, start, end in [('tab_big5_uni0', 0xa140, 0xc7fc), ('tab_big5_uni1', 0xc940, 0xf9dc)]:
    values = table(name)
    assert len(values) == end - start + 1
    decode[start:end + 1] = values
for index, start, end in [(0, 0xa2, 0xf7), (1, 0x2c7, 0x451), (2, 0x2013, 0x22bf),
                          (3, 0x2460, 0x2642), (4, 0x3000, 0x3129), (5, 0x32a3, 0x32a3),
                          (6, 0x338e, 0x33d5), (7, 0x4e00, 0x9483), (8, 0x9577, 0x9fa4),
                          (9, 0xfa0c, 0xfa0d), (10, 0xfe30, 0xfffd)]:
    values = table('tab_uni_big5' + str(index))
    assert len(values) == end - start + 1
    encode[start:end + 1] = values

digest = hashlib.sha256()
for byte in range(256):
    scalar = byte if byte < 128 else 0xffffffff
    digest.update(bytes([byte]) + scalar.to_bytes(4, 'big'))
for lead in range(0x80, 0x100):
    for trail in range(256):
        scalar = decode[(lead << 8) + trail]
        if scalar in (0, 0xfffd):
            scalar = 0xffffffff
        digest.update(bytes([lead, trail]) + scalar.to_bytes(4, 'big'))
print('Decode SHA256:', digest.hexdigest().upper())

digest = hashlib.sha256()
for scalar in range(65536):
    code = encode[scalar] if scalar != 0xfffd and (scalar == 0 or encode[scalar]) else 0xffffffff
    digest.update(scalar.to_bytes(2, 'big') + code.to_bytes(4, 'big'))
print('Encode SHA256:', digest.hexdigest().upper())
