#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Tiner Remote OCR - bo dieu phoi (chay tren may local).

May local giu file PDF va mo mot HTTP server nho. Colab/Kaggle ket noi qua
Cloudflare Tunnel, xin job, tai PDF, quet bang GPU roi tra ket qua ve day.
Khong can upload ca thu muc len cloud, khong can tai khoan moi.

Lenh:
  python coordinator.py plan   --src F:\\books --out F:\\books-ocr
  python coordinator.py serve  --port 8765 [--tunnel]
  python coordinator.py status
  python coordinator.py merge  --out F:\\books-ocr
  python coordinator.py reset
  python coordinator.py run    --src F:\\books --out F:\\books-ocr [--tunnel]

Chi can thu vien chuan + PyMuPDF (fitz) khi plan de dem so trang.
"""
import argparse
import hashlib
import io
import json
import os
import re
import secrets
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DB = os.path.join(HERE, "remote-state.db")
DEFAULT_CHUNK = 40          # so trang / job; 0 = khong chia
CLAIM_TIMEOUT = 30 * 60     # giay; job running qua lau se duoc thu hoi
MAX_ATTEMPTS = 3


# --------------------------------------------------------------------------
#  Database
# --------------------------------------------------------------------------
SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    id          TEXT PRIMARY KEY,
    relpath     TEXT NOT NULL,
    outname     TEXT NOT NULL,
    page_from   INTEGER NOT NULL DEFAULT 1,
    page_to     INTEGER NOT NULL DEFAULT 0,
    pages       INTEGER NOT NULL DEFAULT 0,
    size        INTEGER NOT NULL DEFAULT 0,
    sha1        TEXT NOT NULL DEFAULT '',
    status      TEXT NOT NULL DEFAULT 'pending',
    worker      TEXT,
    claimed_at  REAL,
    attempts    INTEGER NOT NULL DEFAULT 0,
    engine      TEXT,
    seconds     REAL,
    chars       INTEGER,
    result_path TEXT,
    error       TEXT,
    created_at  REAL,
    done_at     REAL
);
CREATE INDEX IF NOT EXISTS idx_jobs_status ON jobs(status);
CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
"""


class Store:
    def __init__(self, path):
        self.path = path
        self.lock = threading.Lock()
        self.conn = sqlite3.connect(path, check_same_thread=False, timeout=30)
        self.conn.row_factory = sqlite3.Row
        with self.lock:
            self.conn.executescript(SCHEMA)
            self.conn.commit()
            if not self.get_meta("token"):
                self.set_meta("token", secrets.token_hex(16))

    def get_meta(self, k, default=None):
        row = self.conn.execute("SELECT v FROM meta WHERE k=?", (k,)).fetchone()
        return row["v"] if row else default

    def set_meta(self, k, v):
        self.conn.execute(
            "INSERT INTO meta(k,v) VALUES(?,?) "
            "ON CONFLICT(k) DO UPDATE SET v=excluded.v", (k, v))
        self.conn.commit()

    def token(self):
        with self.lock:
            return self.get_meta("token")

    def add_job(self, job):
        with self.lock:
            self.conn.execute(
                "INSERT OR IGNORE INTO jobs(id,relpath,outname,page_from,page_to,"
                "pages,size,sha1,created_at) VALUES(?,?,?,?,?,?,?,?,?)",
                (job["id"], job["relpath"], job["outname"], job["page_from"],
                 job["page_to"], job["pages"], job["size"], job["sha1"],
                 time.time()))
            self.conn.commit()

    def claim(self, worker, now=None):
        now = now or time.time()
        with self.lock:
            self.conn.execute("BEGIN IMMEDIATE")
            try:
                row = self.conn.execute(
                    "SELECT * FROM jobs WHERE status='pending' "
                    "OR (status='running' AND claimed_at<?) "
                    "ORDER BY relpath, page_from LIMIT 1",
                    (now - CLAIM_TIMEOUT,)).fetchone()
                if row is None:
                    self.conn.commit()
                    return None
                self.conn.execute(
                    "UPDATE jobs SET status='running', worker=?, claimed_at=?, "
                    "attempts=attempts+1 WHERE id=?", (worker, now, row["id"]))
                self.conn.commit()
                d = dict(row)
                d["attempts"] = (d.get("attempts") or 0) + 1
                d["status"] = "running"
                return d
            except Exception:
                self.conn.rollback()
                raise

    def finish(self, jid, status, engine, seconds, chars, result_path, error):
        with self.lock:
            self.conn.execute(
                "UPDATE jobs SET status=?, engine=?, seconds=?, chars=?, "
                "result_path=?, error=?, done_at=?, claimed_at=NULL WHERE id=?",
                (status, engine, seconds, chars, result_path, error,
                 time.time(), jid))
            self.conn.commit()

    def get(self, jid):
        with self.lock:
            row = self.conn.execute("SELECT * FROM jobs WHERE id=?",
                                    (jid,)).fetchone()
            return dict(row) if row else None

    def rows(self, where="1=1", args=()):
        with self.lock:
            return [dict(r) for r in self.conn.execute(
                "SELECT * FROM jobs WHERE " + where + " ORDER BY relpath, page_from",
                args).fetchall()]

    def counts(self):
        with self.lock:
            return {r["status"]: r["n"] for r in self.conn.execute(
                "SELECT status, COUNT(*) n FROM jobs GROUP BY status").fetchall()}

    def requeue_failed(self):
        with self.lock:
            cur = self.conn.execute(
                "UPDATE jobs SET status='pending', claimed_at=NULL, worker=NULL, "
                "error=NULL WHERE status IN ('running','error') "
                "AND attempts<" + str(MAX_ATTEMPTS))
            self.conn.commit()
            return cur.rowcount


# --------------------------------------------------------------------------
#  Plan
# --------------------------------------------------------------------------
def sha1_file(path, bufsize=1 << 20):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        while True:
            b = f.read(bufsize)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def page_count(path):
    try:
        import fitz  # PyMuPDF
        with fitz.open(path) as doc:
            return doc.page_count
    except Exception:
        return 0


def safe_outname(relpath):
    """<duong_dan_tuong_doi>/<stem>__remote.md, giu thu muc con."""
    rel = relpath.replace("\\", "/")
    d, name = os.path.split(rel)
    stem = os.path.splitext(name)[0]
    stem = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", stem).strip() or "doc"
    return (d + "/" if d else "") + stem + "__remote.md"


def cmd_plan(args):
    src = os.path.abspath(args.src)
    if not os.path.isdir(src):
        sys.exit("Khong thay thu muc nguon: " + src)
    st = Store(args.db)
    chunk = args.chunk
    n_pdf = n_job = n_skip = total_pages = 0
    t0 = time.time()
    for root, _dirs, files in os.walk(src):
        for name in sorted(files):
            if not name.lower().endswith(".pdf"):
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, src)
            try:
                size = os.path.getsize(full)
            except OSError:
                continue
            if size == 0:
                n_skip += 1
                continue
            n_pdf += 1
            pages = page_count(full)
            sha = sha1_file(full)
            outname = safe_outname(rel)
            base = hashlib.sha1(rel.encode("utf-8")).hexdigest()[:12]
            if chunk and pages > chunk:
                parts = []
                p = 1
                while p <= pages:
                    parts.append((p, min(p + chunk - 1, pages)))
                    p += chunk
            else:
                parts = [(1, pages)]
            for i, (pf, pt) in enumerate(parts):
                jid = base + "-%03d" % i if len(parts) > 1 else base
                st.add_job({
                    "id": jid, "relpath": rel, "outname": outname,
                    "page_from": pf, "page_to": pt, "pages": pages,
                    "size": size, "sha1": sha,
                })
                n_job += 1
                total_pages += (pt - pf + 1) if pages else 0
            if n_pdf % 200 == 0:
                print("  da quet %d PDF..." % n_pdf, flush=True)
    print("Xong plan trong %.1fs" % (time.time() - t0))
    print("  PDF       : %d" % n_pdf)
    print("  Job       : %d (chunk=%s)" % (n_job, chunk or "khong chia"))
    print("  Trang     : %d" % total_pages)
    if n_skip:
        print("  Bo qua    : %d file rong" % n_skip)
    print("  DB        : %s" % os.path.abspath(args.db))


# --------------------------------------------------------------------------
#  Merge
# --------------------------------------------------------------------------
LINK_RE = re.compile(r'(!?\[[^\]]*\]\()([^)]+)(\))')


def rewrite_links(md, prefix):
    """Doi link anh tuong doi -> prefix + duong dan (giu assets/...)."""
    def sub(m):
        target = m.group(2).strip()
        if re.match(r'^[a-z]+://', target) or target.startswith("data:") \
                or target.startswith("#"):
            return m.group(0)
        target = target.replace("\\", "/")
        return m.group(1) + prefix + target + m.group(3)
    return LINK_RE.sub(sub, md)


def cmd_merge(args):
    st = Store(args.db)
    outroot = os.path.abspath(args.out)
    os.makedirs(outroot, exist_ok=True)
    rows = st.rows("status='done' AND result_path IS NOT NULL")
    groups = {}
    for r in rows:
        groups.setdefault(r["relpath"], []).append(r)
    n_doc = n_missing = 0
    for rel, jobs in sorted(groups.items()):
        jobs.sort(key=lambda r: r["page_from"])
        expected = jobs[0]["pages"]
        got = sum(1 for r in jobs if r["result_path"])
        if expected and args.strict and got == 0:
            n_missing += 1
            continue
        outname = jobs[0]["outname"].replace("\\", "/")
        out_md = os.path.join(outroot, outname)
        docdir = os.path.dirname(out_md)
        os.makedirs(docdir, exist_ok=True)
        stem = os.path.splitext(os.path.basename(out_md))[0]
        asset_root = os.path.join(docdir, stem + "_assets")
        parts = []
        for j in jobs:
            rp = j["result_path"]
            if not rp or not os.path.isfile(rp):
                continue
            chunk_dir = os.path.join(asset_root, j["id"])
            os.makedirs(chunk_dir, exist_ok=True)
            md = ""
            try:
                with zipfile.ZipFile(rp) as z:
                    z.extractall(chunk_dir)
                    main = os.path.join(chunk_dir, "main.md")
                    if os.path.isfile(main):
                        md = open(main, encoding="utf-8", errors="replace").read()
                    else:
                        mds = [os.path.join(dp, f) for dp, _, fs in
                               os.walk(chunk_dir) for f in fs
                               if f.endswith(".md")]
                        mds.sort(key=lambda p: os.path.getsize(p), reverse=True)
                        if mds:
                            md = open(mds[0], encoding="utf-8",
                                      errors="replace").read()
            except zipfile.BadZipFile:
                md = open(rp, encoding="utf-8", errors="replace").read()
            if not md:
                continue
            prefix = (stem + "_assets/" + j["id"] + "/assets/")
            md = rewrite_links(md, prefix)
            if len(jobs) > 1:
                parts.append("\n\n<!-- %s trang %d-%d -->\n\n" %
                             (os.path.basename(rel), j["page_from"], j["page_to"]))
            parts.append(md)
        if not parts:
            n_missing += 1
            continue
        with open(out_md, "w", encoding="utf-8", newline="\n") as f:
            f.write("".join(parts))
        n_doc += 1
        if n_doc % 100 == 0:
            print("  da ghi %d tai lieu..." % n_doc, flush=True)
    print("Merge xong: %d tai lieu -> %s" % (n_doc, outroot))
    if n_missing:
        print("  Thieu ket qua: %d (chay 'status' de xem)" % n_missing)


# --------------------------------------------------------------------------
#  HTTP server
# --------------------------------------------------------------------------
class Handler(BaseHTTPRequestHandler):
    server_version = "TinerRemote/1.0"
    protocol_version = "HTTP/1.1"

    # -- helpers --------------------------------------------------------
    @property
    def store(self):
        return self.server.store

    def _auth(self):
        tok = self.headers.get("X-Token")
        if not tok:
            q = parse_qs(urlparse(self.path).query)
            tok = (q.get("token") or [""])[0]
        return tok and secrets.compare_digest(tok, self.store.token())

    def _json(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def log_message(self, fmt, *a):
        sys.stdout.write("[%s] %s\n" % (time.strftime("%H:%M:%S"), fmt % a))
        sys.stdout.flush()

    # -- routes ---------------------------------------------------------
    def do_GET(self):
        p = urlparse(self.path)
        if p.path == "/health":
            return self._json(200, {"ok": True, "counts": self.store.counts()})
        if not self._auth():
            return self._json(401, {"error": "unauthorized"})
        if p.path == "/claim":
            return self._json(405, {"error": "use POST"})
        if p.path == "/stats":
            return self._json(200, {"counts": self.store.counts()})
        if p.path.startswith("/pdf/"):
            return self._serve_pdf(p.path[len("/pdf/"):])
        if p.path.startswith("/result/"):
            jid = p.path[len("/result/"):]
            return self._serve_result(jid)
        return self._json(404, {"error": "not found"})

    def do_POST(self):
        p = urlparse(self.path)
        if not self._auth():
            return self._json(401, {"error": "unauthorized"})
        if p.path == "/claim":
            return self._claim()
        if p.path.startswith("/result/"):
            return self._result(p.path[len("/result/"):])
        return self._json(404, {"error": "not found"})

    def _claim(self):
        try:
            body = json.loads(self._read_body() or b"{}")
        except Exception:
            body = {}
        worker = (body.get("worker") or "remote")[:120]
        job = self.store.claim(worker)
        if not job:
            return self._json(200, {"job": None, "wait": 15,
                                    "counts": self.store.counts()})
        full = os.path.join(self.server.src, job["relpath"])
        if not os.path.isfile(full):
            self.store.finish(job["id"], "error", job.get("engine"), 0, 0, None,
                              "missing source file")
            return self._json(200, {"job": None, "wait": 1})
        out = {
            "id": job["id"],
            "relpath": job["relpath"],
            "page_from": job["page_from"],
            "page_to": job["page_to"],
            "pages": job["pages"],
            "size": job["size"],
            "sha1": job["sha1"],
        }
        return self._json(200, {"job": out, "counts": self.store.counts()})

    def _serve_pdf(self, jid):
        job = self.store.get(jid)
        if not job:
            return self._json(404, {"error": "no job"})
        path = os.path.join(self.server.src, job["relpath"])
        if not os.path.isfile(path):
            return self._json(404, {"error": "no file"})
        size = os.path.getsize(path)
        start, end = 0, size - 1
        rng = self.headers.get("Range")
        if rng:
            m = re.match(r"bytes=(\d*)-(\d*)", rng)
            if m:
                if m.group(1):
                    start = int(m.group(1))
                if m.group(2):
                    end = int(m.group(2))
                if start > end or start >= size:
                    self.send_response(416)
                    self.send_header("Content-Range", "bytes */%d" % size)
                    self.end_headers()
                    return
        length = end - start + 1
        self.send_response(206 if rng else 200)
        self.send_header("Content-Type", "application/pdf")
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        if rng:
            self.send_header("Content-Range", "bytes %d-%d/%d" %
                             (start, end, size))
        self.end_headers()
        with open(path, "rb") as f:
            f.seek(start)
            left = length
            while left > 0:
                b = f.read(min(1 << 20, left))
                if not b:
                    break
                self.wfile.write(b)
                left -= len(b)

    def _serve_result(self, jid):
        job = self.store.get(jid)
        if not job or not job.get("result_path") or \
                not os.path.isfile(job["result_path"]):
            return self._json(404, {"error": "no result"})
        data = open(job["result_path"], "rb").read()
        self.send_response(200)
        self.send_header("Content-Type", "application/zip")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _result(self, jid):
        job = self.store.get(jid)
        if not job:
            return self._json(404, {"error": "no job"})
        body = self._read_body()
        status = (self.headers.get("X-Status") or "done").lower()
        engine = self.headers.get("X-Engine") or ""
        try:
            seconds = float(self.headers.get("X-Seconds") or 0)
        except ValueError:
            seconds = 0.0
        try:
            chars = int(self.headers.get("X-Chars") or 0)
        except ValueError:
            chars = 0
        error = self.headers.get("X-Error") or ""
        results_dir = os.path.join(self.server.out, "_remote_results")
        os.makedirs(results_dir, exist_ok=True)
        path = None
        if status == "done" and body:
            rp = os.path.join(results_dir, jid + ".zip")
            tmp = rp + ".part"
            with open(tmp, "wb") as f:
                f.write(body)
            os.replace(tmp, rp)
            path = rp
        if status != "done":
            status = "error"
        self.store.finish(jid, status, engine, seconds, chars, path, error)
        if status == "error" and (job["attempts"] or 0) >= MAX_ATTEMPTS:
            pass
        return self._json(200, {"ok": True, "status": status,
                                "counts": self.store.counts()})


def make_server(src, out, db, port, bind="0.0.0.0"):
    st = Store(db)
    srv = ThreadingHTTPServer((bind, port), Handler)
    srv.daemon_threads = True
    srv.store = st
    srv.src = os.path.abspath(src)
    srv.out = os.path.abspath(out) if out else os.path.dirname(st.path)
    return srv


# --------------------------------------------------------------------------
#  Tunnel
# --------------------------------------------------------------------------
CLOUDFLARED_URL = ("https://github.com/cloudflare/cloudflared/releases/latest/"
                   "download/cloudflared-windows-amd64.exe")


def find_or_get_cloudflared():
    exe = shutil.which("cloudflared")
    if exe:
        return exe
    local = os.path.join(HERE, "bin", "cloudflared.exe")
    if os.path.isfile(local):
        return local
    if os.name != "nt":
        return None
    os.makedirs(os.path.dirname(local), exist_ok=True)
    print("Dang tai cloudflared (~35 MB)...", flush=True)
    try:
        import urllib.request
        urllib.request.urlretrieve(CLOUDFLARED_URL, local)
        return local
    except Exception as e:
        print("Khong tai duoc cloudflared: %s" % e)
        print("Tai tay tai: " + CLOUDFLARED_URL)
        return None


def start_tunnel(port):
    exe = find_or_get_cloudflared()
    if not exe:
        return None, None
    proc = subprocess.Popen(
        [exe, "tunnel", "--url", "http://127.0.0.1:%d" % port, "--no-autoupdate"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        encoding="utf-8", errors="replace")
    url = None
    t0 = time.time()
    # cloudflared in URL ra stderr (gop vao stdout)
    while time.time() - t0 < 45:
        line = proc.stdout.readline()
        if not line:
            if proc.poll() is not None:
                break
            time.sleep(0.2)
            continue
        sys.stdout.write("  [cloudflared] " + line)
        m = re.search(r"https://[a-z0-9-]+\.trycloudflare\.com", line)
        if m:
            url = m.group(0)
            break
    if not url:
        print("Khong lay duoc URL tunnel. Thu chay: cloudflared tunnel --url "
              "http://127.0.0.1:%d" % port)
    return url, proc


def cmd_serve(args):
    st = Store(args.db)
    srv = make_server(args.src, args.out, args.db, args.port, args.bind)
    url = "http://127.0.0.1:%d" % args.port
    tunnel_proc = None
    if args.tunnel:
        print("Dang mo Cloudflare Tunnel...", flush=True)
        turl, tunnel_proc = start_tunnel(args.port)
        if turl:
            url = turl
    tok = st.token()
    cfg = {"url": url, "token": tok, "db": os.path.abspath(args.db),
           "src": os.path.abspath(args.src), "out": os.path.abspath(args.out),
           "started": time.strftime("%Y-%m-%d %H:%M:%S")}
    with open(os.path.join(HERE, "connect.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    print("\n" + "=" * 64)
    print("  COORDINATOR DANG CHAY")
    print("  URL   : %s" % url)
    print("  TOKEN : %s" % tok)
    print("  (da luu vao remote/connect.json)")
    print("=" * 64)
    print("  Dan URL + TOKEN vao notebook Colab/Kaggle roi chay worker.")
    print("  Ctrl+C de dung.\n")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\nDang dung...")
    finally:
        try:
            srv.shutdown()
        except Exception:
            pass
        if tunnel_proc:
            try:
                tunnel_proc.terminate()
            except Exception:
                pass


def cmd_status(args):
    st = Store(args.db)
    c = st.counts()
    total = sum(c.values())
    done = c.get("done", 0)
    print("Tong job : %d" % total)
    for k in ("pending", "running", "done", "error"):
        print("  %-8s: %d" % (k, c.get(k, 0)))
    if total:
        print("Tien do  : %.1f%%" % (100.0 * done / total))
    if c.get("error"):
        print("\nJob loi (toi da %d lan thu):" % MAX_ATTEMPTS)
        for r in st.rows("status='error'")[:20]:
            print("  %s  %s  %s" % (r["relpath"], r["id"],
                                    (r["error"] or "")[:80]))


def cmd_reset(args):
    st = Store(args.db)
    n = st.requeue_failed()
    print("Da dua %d job ve pending." % n)


def cmd_run(args):
    cmd_plan(args)
    args.tunnel = True
    cmd_serve(args)


# --------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(description="Tiner Remote OCR coordinator")
    ap.add_argument("--db", default=DEFAULT_DB, help="file SQLite trang thai")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("plan", help="quet thu muc, tao danh sach job")
    p.add_argument("--src", required=True)
    p.add_argument("--out", default="")
    p.add_argument("--chunk", type=int, default=DEFAULT_CHUNK,
                   help="so trang moi job (0 = khong chia)")

    s = sub.add_parser("serve", help="mo HTTP server cho worker")
    s.add_argument("--src", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--port", type=int, default=8765)
    s.add_argument("--bind", default="0.0.0.0")
    s.add_argument("--tunnel", action="store_true", help="mo Cloudflare Tunnel")

    sub.add_parser("status", help="xem tien do")
    sub.add_parser("reset", help="dua job loi ve pending")

    m = sub.add_parser("merge", help="ghep ket qua thanh .md")
    m.add_argument("--out", required=True)
    m.add_argument("--strict", action="store_true")

    r = sub.add_parser("run", help="plan + serve + tunnel")
    r.add_argument("--src", required=True)
    r.add_argument("--out", required=True)
    r.add_argument("--port", type=int, default=8765)
    r.add_argument("--bind", default="0.0.0.0")
    r.add_argument("--chunk", type=int, default=DEFAULT_CHUNK)

    args = ap.parse_args()
    {"plan": cmd_plan, "serve": cmd_serve, "status": cmd_status,
     "reset": cmd_reset, "merge": cmd_merge, "run": cmd_run}[args.cmd](args)


if __name__ == "__main__":
    main()
