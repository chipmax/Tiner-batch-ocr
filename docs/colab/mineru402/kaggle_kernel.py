# Kernel probe: MinerU 4.0.2 tren 3 PDF mau (do toc do + chat luong).
import os
import subprocess
import time

RAW = ("https://raw.githubusercontent.com/chipmax/Tiner-batch-ocr"
       "/master/docs/colab/")
PDFS = ["samples-codeline-4pp.pdf", "samples-esh-3pp.pdf",
        "samples-kubota-scan.pdf"]
WORK = "/kaggle/working"


def sh(cmd):
    print("+ " + cmd, flush=True)
    r = subprocess.run(cmd, shell=True)
    print("  exit:", r.returncode, flush=True)
    return r.returncode


print("== GPU ==")
sh("nvidia-smi --query-gpu=name,memory.total --format=csv")
print("== cai mineru 4.0.2 ==")
sh("pip install -q mineru==4.0.2")
sh("mineru-kit models show || true")
print("== tai PDF ==")
os.makedirs(WORK + "/pdfs", exist_ok=True)
for f in PDFS:
    sh(f"curl -sL -o {WORK}/pdfs/{f} {RAW}{f}")
print("== parse tung file ==")
os.makedirs(WORK + "/out_mineru402", exist_ok=True)
for f in PDFS:
    t = time.time()
    sh(f"cd {WORK} && mineru parse {WORK}/pdfs/{f} --pages all "
       f"-o {WORK}/out_mineru402/{f}.md")
    print(f, "XONG", round(time.time() - t), "s", flush=True)
print("== so sanh ==")
import glob
for md in sorted(glob.glob(WORK + "/out_mineru402/*.md")):
    t = open(md, encoding="utf-8", errors="ignore").read()
    print(md, len(t), "ky tu", flush=True)
print("ALL DONE")
