#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Tiner Remote OCR - worker (chay tren Colab / Kaggle GPU).

Worker xin job tu coordinator (may local), tai PDF, chay engine OCR, roi tra
mot file zip gom main.md + assets ve coordinator. Chay nhieu session cung luc
duoc: moi job chi mot worker nhan.

Vi du:
  python worker.py --install --engine mineru4
  python worker.py --engine mineru4 --tier standard --minutes 300

Bien moi truong (hoac --coord/--token):
  COORD_URL    vd https://abc-xyz.trycloudflare.com
  COORD_TOKEN  token in ra o coordinator
"""
import argparse
import glob
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile

try:
    import requests
except Exception:
    requests = None

ENGINES = ["mock", "mineru4", "mineru3", "paddle", "paddle_structure",
           "docling", "marker", "dots", "unlimited"]

INSTALL = {
    "mock": ["pymupdf"],
    "mineru4": ['mineru==4.2.3', 'transformers>=5.23', 'pymupdf'],
    "mineru3": ['mineru[core]', 'pymupdf'],
    "paddle": ['paddleocr', 'paddlepaddle-gpu', 'pymupdf'],
    "paddle_structure": ['paddleocr', 'paddlepaddle-gpu', 'pymupdf'],
    "docling": ['docling', 'pymupdf'],
    "marker": ['marker-pdf', 'pymupdf'],
    "dots": ['transformers==4.57.1', 'einops', 'addict', 'easydict',
             'pymupdf', 'qwen_vl_utils'],
    "unlimited": ['transformers', 'accelerate', 'pymupdf'],
}

CACHE = os.path.join(tempfile.gettempdir(), "tiner_remote_cache")


# --------------------------------------------------------------------------
#  HTTP
# --------------------------------------------------------------------------
class Coord:
    def __init__(self, url, token):
        self.url = url.rstrip("/")
        self.token = token
        self.s = requests.Session() if requests else None

    def _req(self, method, path, data=None, headers=None, stream=False):
        h = {"X-Token": self.token}
        if headers:
            h.update(headers)
        url = self.url + path
        if self.s is not None:
            return self.s.request(method, url, data=data, headers=h,
                                  timeout=120, stream=stream)
        # fallback urllib
        import urllib.request
        req = urllib.request.Request(url, data=data, headers=h, method=method)
        return urllib.request.urlopen(req, timeout=120)

    @staticmethod
    def _body(r):
        c = getattr(r, "content", None)
        if c is None:
            c = r.read()
        return c

    def claim(self, worker):
        r = self._req("POST", "/claim",
                      data=json.dumps({"worker": worker}).encode())
        return json.loads(self._body(r).decode("utf-8"))

    def download(self, jid, dest):
        r = self._req("GET", "/pdf/" + jid, stream=True)
        if hasattr(r, "iter_content"):
            with open(dest, "wb") as f:
                for chunk in r.iter_content(1 << 20):
                    if chunk:
                        f.write(chunk)
        else:
            with open(dest, "wb") as f:
                shutil.copyfileobj(r, f)
        return dest

    def post_result(self, jid, body, status="done", engine="", seconds=0,
                    chars=0, error=""):
        headers = {
            "X-Status": status,
            "X-Engine": engine,
            "X-Seconds": "%.1f" % seconds,
            "X-Chars": str(chars),
            "X-Error": (error or "")[:500].replace("\n", " "),
            "Content-Type": "application/octet-stream",
        }
        self._req("POST", "/result/" + jid, data=body, headers=headers)
        return True


# --------------------------------------------------------------------------
#  Helpers
# --------------------------------------------------------------------------
def run(cmd, cwd=None, env=None, timeout=None, quiet=False):
    print("  + " + " ".join(cmd), flush=True)
    e = dict(os.environ)
    if env:
        e.update(env)
    try:
        p = subprocess.run(cmd, cwd=cwd, env=e, timeout=timeout,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                           text=True, encoding="utf-8", errors="replace")
    except subprocess.TimeoutExpired:
        raise RuntimeError("timeout sau %ss: %s" % (timeout, " ".join(cmd)))
    out = p.stdout or ""
    if out and not quiet:
        tail = out[-1500:]
        print("    " + tail.replace("\n", "\n    "), flush=True)
    if p.returncode != 0:
        raise RuntimeError("lenh loi (%d): %s\n%s" %
                           (p.returncode, " ".join(cmd), out[-1500:]))
    return out


def extract_pages(src, dst, page_from, page_to):
    """Cat [page_from, page_to] (1-based, bao gom) ra file moi."""
    import fitz
    with fitz.open(src) as doc:
        n = doc.page_count
        a = max(1, page_from or 1)
        b = min(n, page_to or n)
        if a == 1 and b >= n:
            return src
        out = fitz.open()
        out.insert_pdf(doc, from_page=a - 1, to_page=b - 1)
        out.save(dst)
        out.close()
        return dst


def find_main_md(outdir):
    mds = []
    for dp, _dn, fs in os.walk(outdir):
        for f in fs:
            if f.lower().endswith(".md"):
                p = os.path.join(dp, f)
                mds.append((os.path.getsize(p), p))
    if not mds:
        return None
    mds.sort(reverse=True)
    return mds[0][1]


LINK_RE = re.compile(r'(!?\[[^\]]*\]\()([^)]+)(\))')


def normalize_zip(outdir, md_path, zip_path):
    """Dong goi: main.md o goc + toan bo output vao assets/; sua link anh."""
    stage = tempfile.mkdtemp(prefix="tiner_stage_")
    md_rel_dir = os.path.relpath(os.path.dirname(md_path), outdir)
    prefix = "assets/" + ("" if md_rel_dir == "." else md_rel_dir + "/")
    md = open(md_path, encoding="utf-8", errors="replace").read()

    def sub(m):
        t = m.group(2).strip()
        if re.match(r'^[a-z]+://', t) or t.startswith("data:") or \
                t.startswith("#") or t.startswith("/"):
            return m.group(0)
        return m.group(1) + prefix + t.replace("\\", "/") + m.group(3)

    md = LINK_RE.sub(sub, md)
    with open(os.path.join(stage, "main.md"), "w", encoding="utf-8",
              newline="\n") as f:
        f.write(md)
    assets = os.path.join(stage, "assets")
    shutil.copytree(outdir, assets, dirs_exist_ok=True)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for dp, _dn, fs in os.walk(stage):
            for f in fs:
                p = os.path.join(dp, f)
                z.write(p, os.path.relpath(p, stage))
    shutil.rmtree(stage, ignore_errors=True)
    return zip_path


# --------------------------------------------------------------------------
#  Engines
# --------------------------------------------------------------------------
def eng_mock(pdf, outdir, args):
    import fitz
    with fitz.open(pdf) as doc:
        txt = "\n\n".join(p.get_text("text") for p in doc)
    if not txt.strip():
        txt = "<!-- mock: PDF khong co text layer (anh scan) -->\n"
    p = os.path.join(outdir, "main.md")
    open(p, "w", encoding="utf-8", newline="\n").write(txt)
    return p


def eng_mineru4(pdf, outdir, args):
    os.makedirs(outdir, exist_ok=True)
    out = os.path.join(outdir, "out.md")
    cmd = [shutil.which("mineru-kit") or "mineru-kit", "parse", pdf,
           "-o", out, "--tier", args.tier]
    if args.task_timeout:
        cmd += ["--task-timeout", str(args.task_timeout)]
    try:
        run(cmd, timeout=args.task_timeout or None)
    except FileNotFoundError:
        raise RuntimeError("Khong co mineru-kit. Chay: pip install mineru==4.2.3")
    if not os.path.isfile(out):
        # mot so ban ghi vao thu muc
        cand = find_main_md(outdir)
        if cand:
            return cand
        outdir2 = out + ".d"
        cand = find_main_md(outdir2) if os.path.isdir(outdir2) else None
        if cand:
            return cand
    return out


def eng_mineru3(pdf, outdir, args):
    os.makedirs(outdir, exist_ok=True)
    exe = shutil.which("mineru")
    if not exe:
        raise RuntimeError("Khong co mineru. Chay: pip install 'mineru[core]'")
    run([exe, "-p", pdf, "-o", outdir, "-m", "auto"],
        timeout=args.task_timeout or None)
    md = find_main_md(outdir)
    if not md:
        raise RuntimeError("mineru khong tao .md")
    return md


def eng_paddle(pdf, outdir, args):
    os.makedirs(outdir, exist_ok=True)
    exe = shutil.which("paddleocr")
    if not exe:
        raise RuntimeError("Khong co paddleocr. Chay: pip install paddleocr "
                           "paddlepaddle-gpu")
    run([exe, "doc_parser", "-i", pdf, "--save_path", outdir,
         "--device", args.device], timeout=args.task_timeout or None)
    md = find_main_md(outdir)
    if not md:
        raise RuntimeError("paddleocr doc_parser khong tao .md")
    return md


def eng_paddle_structure(pdf, outdir, args):
    os.makedirs(outdir, exist_ok=True)
    exe = shutil.which("paddleocr")
    if not exe:
        raise RuntimeError("Khong co paddleocr")
    run([exe, "pp_structurev3", "-i", pdf, "--save_path", outdir,
         "--device", args.device], timeout=args.task_timeout or None)
    md = find_main_md(outdir)
    if not md:
        raise RuntimeError("pp_structurev3 khong tao .md")
    return md


def eng_docling(pdf, outdir, args):
    os.makedirs(outdir, exist_ok=True)
    exe = shutil.which("docling")
    if not exe:
        raise RuntimeError("Khong co docling. Chay: pip install docling")
    run([exe, "--to", "md", "--output", outdir, pdf],
        timeout=args.task_timeout or None)
    md = find_main_md(outdir)
    if not md:
        raise RuntimeError("docling khong tao .md")
    return md


def eng_marker(pdf, outdir, args):
    os.makedirs(outdir, exist_ok=True)
    exe = shutil.which("marker_single")
    if not exe:
        raise RuntimeError("Khong co marker. Chay: pip install marker-pdf")
    run([exe, pdf, "--output_dir", outdir, "--output_format", "markdown"],
        timeout=args.task_timeout or None)
    md = find_main_md(outdir)
    if not md:
        raise RuntimeError("marker khong tao .md")
    return md


def eng_dots(pdf, outdir, args):
    repo = "/tmp/dotsmocr"
    if not os.path.isdir(repo):
        run(["git", "clone", "--depth", "1",
             "https://github.com/rednote-hilab/dots.mocr.git", repo])
        run([sys.executable, "-m", "pip", "install", "-q", "-e", repo])
    for dp, _dn, fs in os.walk(repo):
        for f in fs:
            if f.endswith(".py"):
                p = os.path.join(dp, f)
                s = open(p, encoding="utf-8", errors="ignore").read()
                if "flash_attention_2" in s:
                    open(p, "w", encoding="utf-8").write(
                        s.replace("flash_attention_2", "eager"))
    os.makedirs(outdir, exist_ok=True)
    run([sys.executable, "dots_mocr/parser.py", pdf,
         "--prompt", "prompt_ocr", "--use_hf", "true"], cwd=repo)
    md = find_main_md(repo)
    if not md:
        raise RuntimeError("dots.mocr khong tao .md")
    shutil.copy2(md, os.path.join(outdir, "main.md"))
    return os.path.join(outdir, "main.md")


def eng_unlimited(pdf, outdir, args):
    import torch
    import fitz
    from transformers import AutoModel, AutoTokenizer
    name = "baidu/Unlimited-OCR"
    tok = AutoTokenizer.from_pretrained(name, trust_remote_code=True)
    model = AutoModel.from_pretrained(
        name, trust_remote_code=True, use_safetensors=True,
        torch_dtype=torch.bfloat16).eval().cuda()
    os.makedirs(outdir, exist_ok=True)
    tmp = os.path.join(outdir, "pages")
    os.makedirs(tmp, exist_ok=True)
    with fitz.open(pdf) as doc:
        mat = fitz.Matrix(150 / 72, 150 / 72)
        paths = []
        for i, page in enumerate(doc):
            p = os.path.join(tmp, "page_%04d.png" % (i + 1))
            page.get_pixmap(matrix=mat).save(p)
            paths.append(p)
    model.infer_multi(tok, prompt="<image>Multi page parsing.",
                      image_files=paths, output_path=outdir,
                      image_size=1024, max_length=32768,
                      no_repeat_ngram_size=35, ngram_window=1024,
                      save_results=True)
    md = find_main_md(outdir)
    if not md:
        raise RuntimeError("Unlimited-OCR khong tao .md")
    return md


RUNNERS = {
    "mock": eng_mock, "mineru4": eng_mineru4, "mineru3": eng_mineru3,
    "paddle": eng_paddle, "paddle_structure": eng_paddle_structure,
    "docling": eng_docling, "marker": eng_marker, "dots": eng_dots,
    "unlimited": eng_unlimited,
}


# --------------------------------------------------------------------------
#  Install
# --------------------------------------------------------------------------
def install(engine):
    pkgs = INSTALL.get(engine, [])
    if not pkgs:
        return
    print("Cai dat cho engine '%s': %s" % (engine, " ".join(pkgs)), flush=True)
    run([sys.executable, "-m", "pip", "install", "-q", "-U"] + pkgs)
    if engine == "dots":
        repo = "/tmp/dotsmocr"
        if not os.path.isdir(repo):
            run(["git", "clone", "--depth", "1",
                 "https://github.com/rednote-hilab/dots.mocr.git", repo])
        run([sys.executable, "-m", "pip", "install", "-q", "-e", repo])


# --------------------------------------------------------------------------
#  Main loop
# --------------------------------------------------------------------------
def process(job, coord, args):
    jid = job["id"]
    t0 = time.time()
    os.makedirs(CACHE, exist_ok=True)
    cached = os.path.join(CACHE, (job.get("sha1") or jid)[:24] + ".pdf")
    if not os.path.isfile(cached):
        print("  tai %s (%s)..." % (job["relpath"],
                                   human(job.get("size") or 0)), flush=True)
        tmp = cached + ".part"
        coord.download(jid, tmp)
        os.replace(tmp, cached)
    work = tempfile.mkdtemp(prefix="tiner_job_")
    try:
        pdf = extract_pages(cached, os.path.join(work, "input.pdf"),
                            job.get("page_from") or 1, job.get("page_to") or 0)
        outdir = os.path.join(work, "out")
        os.makedirs(outdir, exist_ok=True)
        md = RUNNERS[args.engine](pdf, outdir, args)
        text = open(md, encoding="utf-8", errors="replace").read()
        zpath = os.path.join(work, "result.zip")
        normalize_zip(outdir, md, zpath)
        body = open(zpath, "rb").read()
        secs = time.time() - t0
        coord.post_result(jid, body, "done", args.engine, secs, len(text))
        print("  XONG %s | %d ky tu | %.0fs" %
              (job["relpath"], len(text), secs), flush=True)
    except Exception as e:
        secs = time.time() - t0
        msg = "%s: %s" % (type(e).__name__, e)
        print("  LOI %s -> %s" % (job["relpath"], msg), flush=True)
        try:
            coord.post_result(jid, b"", "error", args.engine, secs, 0, msg)
        except Exception as e2:
            print("  khong gui duoc loi: %s" % e2, flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)


def human(n):
    for u in ("B", "KB", "MB", "GB"):
        if n < 1024 or u == "GB":
            return "%.1f %s" % (n, u)
        n /= 1024.0


def main():
    ap = argparse.ArgumentParser(description="Tiner Remote OCR worker")
    ap.add_argument("--coord", default=os.environ.get("COORD_URL", ""))
    ap.add_argument("--token", default=os.environ.get("COORD_TOKEN", ""))
    ap.add_argument("--engine", default="mineru4", choices=ENGINES)
    ap.add_argument("--tier", default="standard",
                    help="mineru4: flash|basic|standard|advanced")
    ap.add_argument("--device", default="gpu:0", help="paddle device")
    ap.add_argument("--task-timeout", type=int, default=3600)
    ap.add_argument("--max-jobs", type=int, default=0, help="0 = khong gioi han")
    ap.add_argument("--minutes", type=float, default=0,
                    help="tu dung sau N phut (0 = khong gioi han)")
    ap.add_argument("--worker", default="")
    ap.add_argument("--install", action="store_true")
    args = ap.parse_args()

    if args.install:
        install(args.engine)
        if not args.coord:
            print("Da cai xong engine '%s'." % args.engine)
            return

    if not args.coord or not args.token:
        sys.exit("Thieu --coord/--token (hoac COORD_URL/COORD_TOKEN).")
    worker = args.worker or "%s-%s" % (args.engine,
                                       os.uname().nodename
                                       if hasattr(os, "uname") else os.environ.get("COMPUTERNAME", "remote"))
    coord = Coord(args.coord, args.token)
    print("Worker '%s' | engine=%s | coord=%s" %
          (worker, args.engine, args.coord), flush=True)
    start = time.time()
    n_done = n_err = 0
    while True:
        if args.minutes and (time.time() - start) / 60.0 > args.minutes:
            print("Het thoi gian (%.0f phut)." % args.minutes)
            break
        if args.max_jobs and (n_done + n_err) >= args.max_jobs:
            print("Dat gioi han %d job." % args.max_jobs)
            break
        try:
            resp = coord.claim(worker)
        except Exception as e:
            print("Khong ket noi duoc coordinator: %s (thu lai 15s)" % e)
            time.sleep(15)
            continue
        job = resp.get("job")
        counts = resp.get("counts") or {}
        if not job:
            if counts.get("pending", 0) == 0:
                print("Khong con job pending. Tong ket: done=%d err=%d" %
                      (n_done, n_err))
                break
            time.sleep(min(30, max(5, int(resp.get("wait") or 15))))
            continue
        print("\n[JOB %s] %s (trang %s-%s)" %
              (job["id"], job["relpath"], job["page_from"],
               job["page_to"] or "?"), flush=True)
        process(job, coord, args)
        n_done += 1
    print("Ket thuc: xu ly %d job." % (n_done + n_err))


if __name__ == "__main__":
    main()
