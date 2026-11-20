# -*- coding: utf-8 -*-
"""Tiner Remote OCR - Kaggle worker.

Bat GPU T4 + Internet. Them secret COORD_URL / COORD_TOKEN (Add-ons > Secrets)
hoac sua truc tiep 2 bien duoi day.
"""
import os
import subprocess
import sys
import urllib.request

ENGINE = os.environ.get("ENGINE", "mineru4")
TIER = os.environ.get("TIER", "standard")
MINUTES = os.environ.get("MINUTES", "300")

URL = os.environ.get("COORD_URL", "")
TOKEN = os.environ.get("COORD_TOKEN", "")
if not URL or not TOKEN:
    try:
        from kaggle_secrets import UserSecretsClient
        _c = UserSecretsClient()
        URL = URL or _c.get_secret("COORD_URL")
        TOKEN = TOKEN or _c.get_secret("COORD_TOKEN")
    except Exception as e:
        print("Khong doc duoc Kaggle Secrets:", e)

WORK = os.environ.get("TINER_WORK", "/kaggle/working" if os.path.isdir("/kaggle/working") else os.getcwd())
os.chdir(WORK)

print(subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total",
                      "--format=csv,noheader"], capture_output=True, text=True).stdout)
urllib.request.urlretrieve(
    "https://raw.githubusercontent.com/chipmax/Tiner-batch-ocr/master/remote/worker.py",
    "worker.py")
subprocess.run([sys.executable, "-m", "pip", "install", "-q", "pymupdf", "requests"], check=True)
subprocess.run([sys.executable, "worker.py", "--install", "--engine", ENGINE], check=True)

if not URL or not TOKEN:
    raise SystemExit("Thieu COORD_URL / COORD_TOKEN. Them vao Kaggle Secrets.")

cmd = [sys.executable, "worker.py", "--coord", URL, "--token", TOKEN,
       "--engine", ENGINE, "--tier", TIER, "--device", "gpu:0",
       "--minutes", MINUTES]
print(" ".join(cmd), flush=True)
subprocess.run(cmd, check=False)
