"""Print strict-result digests from OceanBase 5d6cb5c charset source files.
Usage: python SingleByteReference.py ob_ctype_extra.cc ob_ctype_tis620_tab.h
Source links and strict placeholder policy are in the architecture document.
"""
import hashlib
import pathlib
import re
import sys

for path, name in [(sys.argv[1], 'to_uni_dec8_swedish_ci'),
                   (sys.argv[1], 'to_uni_dec8_bin'),
                   (sys.argv[2], 'cs_to_uni_tis620')]:
    source = pathlib.Path(path).read_text(encoding='utf-8')
    body = re.search(r'\b' + name + r'\[[^\]]*\]\s*=\s*\{([^}]+)', source).group(1)
    values = [int(x, 16) for x in re.findall(r'0x[0-9a-fA-F]+', body)]
    assert len(values) == 256 and values[:160] == list(range(160))
    # Zero outside NUL and FFFD are unassigned server table placeholders.
    strict = [v if (v != 0 or i == 0) and v != 0xfffd else 0xffff
              for i, v in enumerate(values)]
    digest = hashlib.sha256(b''.join(v.to_bytes(2, 'big') for v in strict)).hexdigest().upper()
    holes = ''.join(f'{i:02X}' for i, v in enumerate(strict) if v == 0xffff)
    print(f'{name}: SHA256={digest}; undefined={holes}')
