"""tesseract.exe'nin bağımlı olduğu DLL'leri (yinelemeli) bulup hedef klasöre kopyalar."""
import os
import shutil
import sys

import pefile

src, dst = sys.argv[1], sys.argv[2]
local = {f.lower(): f for f in os.listdir(src) if f.lower().endswith(".dll")}
need, todo = set(), ["tesseract.exe"]
while todo:
    name = todo.pop()
    path = os.path.join(src, local.get(name.lower(), name))
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                                           pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"]])
    for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
        for entry in getattr(pe, attr, []):
            dll = entry.dll.decode().lower()
            if dll in local and dll not in need:
                need.add(dll)
                todo.append(dll)
total = 0
for dll in sorted(need):
    shutil.copy2(os.path.join(src, local[dll]), dst)
    total += os.path.getsize(os.path.join(src, local[dll]))
print(f"{len(need)} / {len(local)} DLL kopyalandı ({total / 2**20:.0f} MB)")
