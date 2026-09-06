#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PaddleOCR PP-StructureV3 wrapper: nhanh + gon.

CLI `paddlex` mac dinh goi save_all() -> moi trang sinh .md + .json + .docx
+ .tex + .html/.xlsx + 4 PNG visualization (600+ file cho sach 70 trang).

Wrapper nay:
  - Tat module thua: seal / formula / chart / orientation / unwarping.
  - Chi goi save_to_markdown() theo tung trang (khong docx/tex/json/PNG).
  - Gop cac trang thanh 1 file <ten pdf>.md duy nhat, anh crop gom ve imgs/.
"""

import argparse
import shutil
import sys
import tempfile
from pathlib import Path


def parse_args():
    ap = argparse.ArgumentParser("paddle_run: PP-StructureV3 chi luu markdown gop")
    ap.add_argument("--input", required=True, help="Duong dan file PDF")
    ap.add_argument("--save_path", required=True, help="Thu muc luu ket qua")
    ap.add_argument("--device", default="gpu", help="gpu | cpu")
    return ap.parse_args()


def main():
    args = parse_args()
    pdf = Path(args.input)
    outdir = Path(args.save_path)
    outdir.mkdir(parents=True, exist_ok=True)
    if not pdf.exists():
        print(f"PADDLE LOI: khong thay file {pdf}", flush=True)
        return 2

    from paddlex import create_pipeline

    pipeline = create_pipeline(pipeline="PP-StructureV3", device=args.device)

    tmp = Path(tempfile.mkdtemp(prefix="paddle_run_"))
    pages = []
    try:
        results = pipeline.predict(
            input=str(pdf),
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
            use_seal_recognition=False,
            use_formula_recognition=False,
            use_chart_recognition=False,
        )
        for i, res in enumerate(results):
            if isinstance(res, dict) and res.get("error"):
                print(f"PADDLE page {i + 1}: loi: {res.get('error')}", flush=True)
                continue
            page_dir = tmp / f"p{i:04d}"
            page_dir.mkdir(parents=True, exist_ok=True)
            page_md = page_dir / "page.md"
            try:
                res.save_to_markdown(save_path=str(page_md))
            except Exception as ex:
                print(f"PADDLE page {i + 1}: loi luu markdown: {ex}", flush=True)
                continue
            if not page_md.exists():
                print(f"PADDLE page {i + 1}: khong co markdown", flush=True)
                continue
            text = page_md.read_text(encoding="utf-8")
            # Gom anh crop cua trang ve outdir/imgs, doi ten theo trang
            # de khong de nhau giua cac trang (ten goc theo toa do).
            img_dir = page_dir / "imgs"
            if img_dir.is_dir():
                dest_img = outdir / "imgs"
                dest_img.mkdir(parents=True, exist_ok=True)
                for img in sorted(img_dir.iterdir()):
                    if not img.is_file():
                        continue
                    new_name = f"p{i:04d}_{img.name}"
                    try:
                        shutil.copy2(str(img), str(dest_img / new_name))
                        text = text.replace(f"imgs/{img.name}", f"imgs/{new_name}")
                    except Exception:
                        pass
            pages.append((i, text))
            print(f"PADDLE page {i + 1} xong ({len(text)} ky tu)", flush=True)

        if not pages:
            print("PADDLE LOI: khong co trang nao co ket qua", flush=True)
            return 1

        pages.sort(key=lambda t: t[0])
        merged = []
        for i, text in pages:
            merged.append(f"\n\n<!-- trang {i + 1} -->\n\n")
            merged.append(text.strip())
        final_md = outdir / f"{pdf.stem}.md"
        final_md.write_text("".join(merged).strip() + "\n", encoding="utf-8")
        print(
            f"PADDLE DONE pages={len(pages)} chars={final_md.stat().st_size} "
            f"file={final_md.name}",
            flush=True,
        )
        return 0
    finally:
        shutil.rmtree(str(tmp), ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
