# Kaggle script kernel v8: dots.mocr truoc (CPU, luon chay duoc),
# Unlimited-OCR sau (chi khi GPU tu T4 tro len, vi code cung .cuda()).
import glob
import os
import subprocess
import time

RAW = ("https://raw.githubusercontent.com/chipmax/Tiner-batch-ocr"
       "/master/docs/colab/")
PDFS = ["samples-codeline-4pp.pdf", "samples-esh-3pp.pdf",
        "samples-kubota-scan.pdf"]
WORK = "/kaggle/working"


def sh(cmd, **kw):
    print("+ " + cmd, flush=True)
    r = subprocess.run(cmd, shell=True, **kw)
    print("  exit:", r.returncode, flush=True)
    return r


print("== GPU ==")
g = sh("nvidia-smi --query-gpu=name --format=csv,noheader")
IS_P100 = False

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

print("== tai model dots ==")
from huggingface_hub import snapshot_download
snapshot_download("rednote-hilab/dots.mocr",
                  local_dir="/tmp/models/DotsMOCR")

print("== dots.mocr (CPU, patch flash-attn) ==")
sh("grep -rl flash_attention_2 /tmp/dotsmocr --include=*.py | "
   "xargs sed -i 's/flash_attention_2/eager/g' || true")
os.makedirs(WORK + "/out_dots", exist_ok=True)
no_gpu = dict(os.environ)
no_gpu["CUDA_VISIBLE_DEVICES"] = ""
seen = set(glob.glob("/tmp/dotsmocr/**/*.md", recursive=True))
for f in PDFS:
    t = time.time()
    r = subprocess.run(
        ["python3", "dots_mocr/parser.py", f"{WORK}/pdfs/{f}",
         "--prompt", "prompt_ocr", "--use_hf", "true"],
        cwd="/tmp/dotsmocr", env=no_gpu)
    fresh = [m for m in glob.glob("/tmp/dotsmocr/**/*.md", recursive=True)
             if m not in seen]
    for m in fresh:
        seen.add(m)
        dst = f"{WORK}/out_dots/dots_{os.path.basename(f)}_{os.path.basename(m)}"
        open(dst, "w", encoding="utf-8").write(
            open(m, encoding="utf-8").read())
        print("   luu:", dst, flush=True)
    print(f, "XONG", round(time.time() - t), "s exit", r.returncode,
          flush=True)

print("== Unlimited-OCR (can GPU, bo qua neu P100) ==")
import fitz
gpu_name = ""
try:
    out = subprocess.run("nvidia-smi --query-gpu=name --format=csv,noheader",
                         shell=True, capture_output=True, text=True)
    gpu_name = out.stdout.strip()
except Exception:
    pass
print("GPU:", gpu_name, flush=True)
if "P100" in gpu_name or not gpu_name:
    print("BO QUA Unlimited (can CUDA that, P100 khong chay duoc)", flush=True)
else:
    import torch
    from transformers import AutoModel, AutoTokenizer
    tok = AutoTokenizer.from_pretrained(
        "/tmp/models/Unlimited-OCR" if os.path.exists("/tmp/models/Unlimited-OCR")
        else "baidu/Unlimited-OCR", trust_remote_code=True)
    if not os.path.exists("/tmp/models/Unlimited-OCR"):
        snapshot_download("baidu/Unlimited-OCR",
                          local_dir="/tmp/models/Unlimited-OCR")
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

print("== so sanh ==")
for md in sorted(glob.glob(WORK + "/out_dots/*.md") +
                 glob.glob(WORK + "/out_unlimited/*.md")):
    t = open(md, encoding="utf-8").read()
    print(md, len(t), "ky tu | bang:", t.count("<table") + t.count("| ---"),
          flush=True)
print("ALL DONE")
