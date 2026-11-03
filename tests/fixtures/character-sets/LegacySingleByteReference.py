"""Print strict Unicode table digests from MySQL mysql-8.0.36 charset XML files.
Usage: python LegacySingleByteReference.py cp850.xml hp8.xml macroman.xml swe7.xml
Zero outside NUL marks an undefined slot; results must reject it rather than replace it.
"""
import hashlib
import pathlib
import sys
import xml.etree.ElementTree as xml

for argument in sys.argv[1:]:
    path = pathlib.Path(argument)
    values = [int(value, 16) for value in xml.parse(path).findtext('.//unicode/map').split()]
    assert len(values) == 256
    strict = [value if (value != 0 or index == 0) and value != 0xfffd else 0xffff
              for index, value in enumerate(values)]
    digest = hashlib.sha256(b''.join(value.to_bytes(2, 'big') for value in strict)).hexdigest().upper()
    holes = ''.join(f'{index:02X}' for index, value in enumerate(strict) if value == 0xffff)
    print(f'{path.stem}: SHA256={digest}; undefined={holes}')
