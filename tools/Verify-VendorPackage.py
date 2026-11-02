"""Statically verify the manufacturer package audited on 2026-11-02.

No installer/DLL execution, hardware access, network access or binary output.
The fixed stream offset is valid ONLY for the pinned ZIP/Setup.exe below.
Usage: python tools/Verify-VendorPackage.py path/to/1102Setup20160723.zip
"""
import hashlib
import json
import pathlib
import struct
import sys
import zipfile
import zlib

PACKAGE_SHA256 = "d9af1c928f7cae1e2f447f5973dd8707a1327d13b9b2ae8a811d2c00c72d7e27"
DLL_SHA256 = "93ca581f4de649c54957cbd84b25a067abaa89a078f765b4c7b6958f895c9cc1"
DLL_STREAM_OFFSET = 0x85A970


def verify(path):
    if path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("Package exceeds the audited size bound")
    package_hash = hashlib.sha256(path.read_bytes()).hexdigest()
    if package_hash != PACKAGE_SHA256:
        raise ValueError("Unknown package hash; do not reuse the audited stream offset")
    with zipfile.ZipFile(path) as archive:
        info = archive.getinfo("Setup.exe")
        if info.file_size != 9023195:
            raise ValueError("Unexpected installer size")
        setup = archive.read(info)
    decoder = zlib.decompressobj(-15)
    dll = decoder.decompress(setup[DLL_STREAM_OFFSET:], 1024 * 1024)
    if not decoder.eof or len(dll) != 49152:
        raise ValueError("Incomplete or unexpected DLL stream")
    dll_hash = hashlib.sha256(dll).hexdigest()
    if dll_hash != DLL_SHA256:
        raise ValueError("Vendor DLL differs from the reconstructed ABI baseline")
    pe = struct.unpack_from("<I", dll, 0x3C)[0]
    if dll[:2] != b"MZ" or dll[pe:pe + 4] != b"PE\0\0":
        raise ValueError("DLL is not PE")
    machine = struct.unpack_from("<H", dll, pe + 4)[0]
    if machine != 0x14C:
        raise ValueError("DLL is not i386")
    return {
        "packageSha256": package_hash,
        "installerZipTimestamp": info.date_time,
        "dllStreamOffset": hex(DLL_STREAM_OFFSET),
        "dllBytes": len(dll),
        "dllSha256": dll_hash,
        "machine": "i386",
        "matchesDocumentedAbiBinary": True,
        "executedVendorCode": False,
    }


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: Verify-VendorPackage.py path/to/1102Setup20160723.zip")
    try:
        print(json.dumps(verify(pathlib.Path(sys.argv[1])), indent=2))
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, zlib.error) as error:
        raise SystemExit(str(error)) from error
