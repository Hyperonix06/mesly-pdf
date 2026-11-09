"""PyInstaller giriş noktası. Dosya adında 'cli' geçen yürütülebilir dosya komut satırı modunda çalışır."""
import os
import sys
import multiprocessing

def _utf8_console():
    """Windows konsolu (cp1252/cp857) Türkçe karakterlerde çökmesin."""
    for stream in (sys.stdout, sys.stderr):
        try:
            if stream is not None:
                stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


if __name__ == "__main__":
    multiprocessing.freeze_support()
    _utf8_console()
    exe = os.path.basename(sys.executable if getattr(sys, "frozen", False) else sys.argv[0]).lower()
    if "cli" in exe:
        from meslypdf.cli import main
        sys.exit(main())
    from meslypdf.app import main
    sys.exit(main())
