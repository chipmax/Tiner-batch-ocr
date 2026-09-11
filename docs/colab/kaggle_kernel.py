# Kaggle script kernel: test Unlimited-OCR + dots.mocr (GPU T4 free).
# Input PDFs: lay tu GitHub raw (repo cong khai). Output: /kaggle/working.
import os
import subprocess
import sys
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

print("== cai dat ==")
sh("pip install -q transformers==4.57.1 einops addict easydict "
   "pymupdf==1.27.2.2 qwen_vl_utils matplotlib psutil")
sh("git clone --depth 1 https://github.com/rednote-hilab/dots.mocr.git "
   "/tmp/dotsmocr && pip install -q -e /tmp/dotsmocr")

print("== tai PDF mau ==")
os.makedirs(WORK + "/pdfs", exist_ok=True)
for f in PDFS:
    sh(f"curl -sL -o {WORK}/pdfs/{f} {RAW}{f}")
    print("  ", f, os.path.getsize(f"{WORK}/pdfs/{f}"), "bytes", flush=True)

print("== tai model ==")
from huggingface_hub import snapshot_download
snapshot_download("baidu/Unlimited-OCR", local_dir="/tmp/models/Unlimited-OCR")
snapshot_download("rednote-hilab/dots.mocr", local_dir="/tmp/models/DotsMOCR")

print("== Unlimited-OCR ==")
import fitz
import torch
from transformers import AutoModel, AutoTokenizer

tok = AutoTokenizer.from_pretrained("/tmp/models/Unlimited-OCR",
                                    trust_remote_code=True)
model = AutoModel.from_pretrained(
    "/tmp/models/Unlimited-OCR", trust_remote_code=True,
    use_safetensors=True, torch_dtype=torch.bfloat16).eval().cuda()


def pdf_to_images(pdf, dpi=150):
    doc = fitz.open(pdf)
    tmp = f"/tmp/ocr_{os.path.basename(pdf)}"
    os.makedirs(tmp, exist_ok=True)
    mat = fitz.Matrix(dpi / 72, dpi / 72)
    paths = []
    for i, page in enumerate(doc):
        p = os.path.join(tmp, f"page_{i + 1:04d}.png")
        page.get_pixmap(matrix=mat).save(p)
        paths.append(p)
    doc.close()
    return paths


os.makedirs(WORK + "/out_unlimited", exist_ok=True)
for f in PDFS:
    t = time.time()
    model.infer_multi(
        tok, prompt="<image>Multi page parsing.",
        image_files=pdf_to_images(f"{WORK}/pdfs/{f}"),
        output_path=WORK + "/out_unlimited",
        image_size=1024, max_length=32768,
        no_repeat_ngram_size=35, ngram_window=1024, save_results=True)
    print(f, "XONG", round(time.time() - t), "s", flush=True)
del model
torch.cuda.empty_cache()

print("== dots.mocr (transformers, patch flash-attn cho T4) ==")
sh("grep -rl flash_attention_2 /tmp/dotsmocr --include=*.py | "
   "xargs sed -i 's/flash_attention_2/eager/g' || true")
for f in PDFS:
    t = time.time()
    sh(f"cd /tmp/dotsmocr && python3 dots_mocr/parser.py {WORK}/pdfs/{f} "
       f"--prompt prompt_ocr --use_hf true")
    print(f, "XONG", round(time.time() - t), "s", flush=True)

print("== so sanh ==")
import glob
for md in sorted(glob.glob(WORK + "/out_unlimited/*.md") +
                 glob.glob("/tmp/dotsmocr/*.md")):
    t = open(md, encoding="utf-8").read()
    print(md, len(t), "ky tu | bang:", t.count("<table") + t.count("| ---"),
          flush=True)
print("ALL DONE")
