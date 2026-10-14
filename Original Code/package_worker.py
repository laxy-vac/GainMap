"""Remove Microsoft runtime DLLs from a PyInstaller 6 one-file worker.

Windows provides UCRT/API sets; Microsoft VC++ v14 x64 must be installed
separately by the user. No runtime is downloaded, installed or copied.
NumPy's private MSVCP import is redirected to the installed system DLL.
Uses only Python's standard library; the original worker is never modified.
"""
import argparse
import base64
import csv
import hashlib
import io
from dataclasses import dataclass
from pathlib import Path
import re
import struct
import zlib

MAGIC = b"MEI\x0c\x0b\x0a\x0b\x0e"
COOKIE = struct.Struct("!8sIIII64s")
ENTRY = struct.Struct("!IIIIBc")


@dataclass
class Item:
    name: str
    kind: bytes
    compressed: int
    size: int
    payload: bytes

    def unpack(self):
        result = zlib.decompress(self.payload) if self.compressed else self.payload
        if len(result) != self.size:
            raise ValueError("Invalid archive item size: " + self.name)
        return result

    def replace(self, data):
        self.size = len(data)
        self.payload = zlib.compress(data, 9) if self.compressed else data


def read_archive(path):
    data = Path(path).read_bytes()
    location = data.rfind(MAGIC)
    if location < 0 or location + COOKIE.size != len(data):
        raise ValueError("Expected an unsigned PyInstaller archive ending at EOF")
    _, length, toc_offset, toc_size, version, library = COOKIE.unpack_from(data, location)
    start = len(data) - length
    if start < 0 or start + toc_offset + toc_size != location:
        raise ValueError("Invalid PyInstaller archive boundaries")
    position, end = start + toc_offset, location
    items = []
    while position < end:
        length, offset, packed_size, raw_size, compressed, kind = ENTRY.unpack_from(data, position)
        if length <= ENTRY.size or position + length > end or offset + packed_size > toc_offset:
            raise ValueError("Invalid PyInstaller archive entry")
        name = data[position + ENTRY.size:position + length].split(b"\0", 1)[0].decode("utf-8")
        items.append(Item(name, kind, compressed, raw_size,
                          data[start + offset:start + offset + packed_size]))
        position += length
    return data[:start], version, library, items


def write_archive(path, stub, version, library, items):
    body, toc = bytearray(), bytearray()
    for item in items:
        offset = len(body)
        body.extend(item.payload)
        name = item.name.encode("utf-8") + b"\0"
        length = (ENTRY.size + len(name) + 15) // 16 * 16
        toc.extend(ENTRY.pack(length, offset, len(item.payload), item.size, item.compressed, item.kind))
        toc.extend(name + b"\0" * (length - ENTRY.size - len(name)))
    cookie = COOKIE.pack(MAGIC, len(body) + len(toc) + COOKIE.size,
                         len(body), len(toc), version, library)
    Path(path).write_bytes(stub + body + toc + cookie)


def microsoft_dll(name):
    base = name.replace("\\", "/").rsplit("/", 1)[-1].lower()
    return (base == "ucrtbase.dll" or base.startswith("api-ms-win-") and base.endswith(".dll")
            or re.fullmatch(r"(?:msvcp|vcruntime|concrt)\d+(?:_[a-z0-9_]+)?(?:-[0-9a-f]+)?\.dll", base) is not None)


def import_names(data):
    """Yield (DLL import name, its file offset) for normal and delay PE imports."""
    if data[:2] != b"MZ":
        raise ValueError("Expected a PE file")
    pe = struct.unpack_from("<I", data, 0x3c)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Invalid PE signature")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    if magic != 0x20b:
        raise ValueError("Only x64 PE files are supported")
    directories = optional + 112
    header_size = struct.unpack_from("<I", data, optional + 60)[0]
    sections = optional + optional_size

    def locate(rva):
        if rva < header_size:
            return rva
        for index in range(count):
            off = sections + index * 40
            virtual_size, virtual, raw_size, raw = struct.unpack_from("<IIII", data, off + 8)
            if virtual <= rva < virtual + max(virtual_size, raw_size):
                result = raw + rva - virtual
                if result >= len(data):
                    break
                return result
        raise ValueError("Invalid PE RVA")

    for index, descriptor_size, name_index in ((1, 20, 3), (13, 32, 1)):
        rva, size = struct.unpack_from("<II", data, directories + index * 8)
        if not rva or not size:
            continue
        position = locate(rva)
        for off in range(position, position + size, descriptor_size):
            fields = struct.unpack_from("<" + "I" * (descriptor_size // 4), data, off)
            if not any(fields):
                break
            if index == 13 and not fields[0] & 1:
                raise ValueError("Unsupported delay import using a virtual address")
            name_offset = locate(fields[name_index])
            end = data.find(b"\0", name_offset, name_offset + 512)
            if end < 0:
                raise ValueError("Unterminated DLL import")
            yield data[name_offset:end].decode("ascii"), name_offset


def package(source, destination):
    if Path(source).resolve() == Path(destination).resolve():
        raise ValueError("Input and output workers must differ")
    stub, version, library, items = read_archive(source)
    if version != 312:
        raise ValueError("This build pipeline is validated for Python 3.12")
    removed = [item.name for item in items if microsoft_dll(item.name)]
    aliases = {}
    for name in removed:
        base = name.replace("\\", "/").rsplit("/", 1)[-1].lower()
        canonical = re.sub(r"-[0-9a-f]{8,}(?=\.dll$)", "", base)
        if canonical != base:
            aliases[base] = canonical
    retained, changes = [], []
    for item in items:
        if item.name in removed:
            continue
        if item.name.lower().endswith((".dll", ".pyd", ".exe")):
            raw = bytearray(item.unpack())
            patched = []
            for name, offset in import_names(raw):
                replacement = aliases.get(name.lower())
                if replacement:
                    old = name.encode("ascii")
                    new = replacement.encode("ascii")
                    if len(new) > len(old):
                        raise ValueError("DLL import replacement cannot grow")
                    raw[offset:offset + len(old)] = new + b"\0" * (len(old) - len(new))
                    patched.append((name, replacement))
            if patched:
                item.replace(raw)
                changes.append((item.name, patched))
        elif ".load-order-" in item.name:
            raw = item.unpack()
            lines = raw.decode("utf-8").splitlines(keepends=True)
            filtered = "".join(line for line in lines if not microsoft_dll(line.strip()))
            if filtered.encode("utf-8") != raw:
                item.replace(filtered.encode("utf-8"))
                changes.append((item.name, "removed Microsoft preloads"))
        retained.append(item)
    removed_paths = {name.replace("\\", "/") for name in removed}
    modified_paths = {name.replace("\\", "/") for name, _ in changes}
    by_path = {item.name.replace("\\", "/"): item for item in retained}
    for item in retained:
        if not item.name.replace("\\", "/").endswith(".dist-info/RECORD"):
            continue
        rows, changed = [], False
        for row in csv.reader(io.StringIO(item.unpack().decode("utf-8"))):
            if row[0] in removed_paths:
                changed = True
                continue
            if row[0] in modified_paths and row[0] in by_path:
                raw = by_path[row[0]].unpack()
                digest = base64.urlsafe_b64encode(hashlib.sha256(raw).digest()).rstrip(b"=").decode("ascii")
                row[1:] = ["sha256=" + digest, str(len(raw))]
                changed = True
            rows.append(row)
        if changed:
            stream = io.StringIO(newline="")
            csv.writer(stream, lineterminator="\n").writerows(rows)
            item.replace(stream.getvalue().encode("utf-8"))
            changes.append((item.name, "updated modified and removed file records"))
    if not removed:
        raise ValueError("No Microsoft DLLs found; check the build inputs")
    if aliases and not changes:
        raise ValueError("Private runtime DLLs were removed without updating their imports")
    Path(destination).parent.mkdir(parents=True, exist_ok=True)
    write_archive(destination, stub, version, library, retained)
    # Re-read the completed file, verify data integrity and all redirected imports.
    _, _, _, actual = read_archive(destination)
    for item in actual:
        if microsoft_dll(item.name):
            raise ValueError("Microsoft DLL remained in the packaged worker")
        raw = item.unpack()
        if item.name.lower().endswith((".dll", ".pyd", ".exe")):
            if any(name.lower() in aliases for name, _ in import_names(raw)):
                raise ValueError("A private Microsoft DLL import remained")
    return {"removed": removed, "modified": changes, "retained_entries": len(actual)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    import json
    print(json.dumps(package(args.source, args.destination), indent=2, ensure_ascii=False))
