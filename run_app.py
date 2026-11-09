"""PyInstaller giriş noktası. Dosya adında 'cli' geçen yürütülebilir dosya komut satırı modunda çalışır."""
import os
import sys
import multiprocessing

if __name__ == "__main__":
    multiprocessing.freeze_support()
    exe = os.path.basename(sys.executable if getattr(sys, "frozen", False) else sys.argv[0]).lower()
    if "cli" in exe:
        from meslypdf.cli import main
        sys.exit(main())
    from meslypdf.app import main
    sys.exit(main())
