"""Regenerate strict CP932 digests from mysql-8.0.36/strings/ctype-cp932.cc.
Usage: python Cp932Reference.py ctype-cp932.cc
"""
import hashlib
import pathlib
import re
import sys

source = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')


def table(name):
    body = re.search(name + r'\[65536\]\s*=\s*\{([^}]+)', source).group(1)
    body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
    values = [int(x, 16) for x in re.findall(r'0x[0-9a-fA-F]+', body)]
    assert len(values) == 65536
    return values


decode = table('cp932_to_unicode')
encode = table('unicode_to_cp932')
digest = hashlib.sha256()
for byte in range(256):
    scalar = decode[byte] if byte < 128 or 0xa1 <= byte <= 0xdf else 0xffffffff
    digest.update(bytes([byte]) + scalar.to_bytes(4, 'big'))
for lead in list(range(0x80, 0xa1)) + list(range(0xe0, 0x100)):
    for trail in range(256):
        scalar = decode[(lead << 8) + trail] or 0xffffffff
        digest.update(bytes([lead, trail]) + scalar.to_bytes(4, 'big'))
print('Decode SHA256:', digest.hexdigest().upper())

digest = hashlib.sha256()
for scalar in range(65536):
    code = scalar if scalar < 128 else encode[scalar]
    if scalar >= 128 and (code == 0 or decode[code] != scalar):
        code = 0xffffffff
    digest.update(scalar.to_bytes(2, 'big') + code.to_bytes(4, 'big'))
print('Encode SHA256:', digest.hexdigest().upper())
