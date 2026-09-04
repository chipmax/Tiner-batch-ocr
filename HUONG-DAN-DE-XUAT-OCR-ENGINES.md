# DE XUAT: TICH HOP THEM CAC OCR ENGINE KHAC VAO MINERU25TOOL
(Chi la de bao cao/de xuat — CHUA implement. Ngay tao: 2026-09-01)

## 1. MUC TIEU

Them lua chon Engine o man hinh chinh de co the chay OCR bang engine khac
ngoai MinerU (chon khi bat dau, giong voi cach chon engine MinerU hien tai).
Cac engine moi se dung chung: resume, quality gate [KTRA], batch-log,
batch-report.html, timeout, retry, chia CPU.

## 2. KIEN TRUC DE XUAT

- Doi BuildArgs/RunOneAsync (dang hardcode cho MinerU) thanh interface:

      interface IParseEngine
      {
          string Name { get; }                          // hien thi tren UI
          Task PrepareAsync();                          // kiem tra exe/pip/API key, log canh bao
          string BuildArgs(FileInfo f, string outdir);  // cau lenh chay 1 file
          string OutputMdPath(FileInfo f, string outdir);// de quality gate + resume
      }

- EngineBox them nhom moi (Separator item):
    + MinerU (hien tai): hybrid/vlm/pipeline/http-client
    + Local: PaddleOCR, Docling, Tesseract, GOT-OCR2.0
    + Cloud: Mistral OCR, Azure Document Intelligence
- Moi engine xuat ket qua ve CUNG layout: <outdir>\<ten file>\<ten file>.md
  de resume + quality gate hoat dong khong doi.
- Cloud engine khong can GPU; local engine nhe co the chay SONG SONG voi
  batch MinerU (vi VRAM con duong) — sau nay co the lam che do "engine so sanh"
  chay 2 engine tren file KHA NGHI de chon ket qua tot hon.

## 3. CAC ENGINE DE XUAT (xep theo do uu tien voi corpus hien tai)

### 3.1 PaddleOCR 3.x (PP-OCRv5/v6 + PP-StructureV3) — KHUYEN DUNG SO 1 — DA TRIEN KHAI (2026-09-01)
- License: Apache-2.0. VRAM: ~1-2GB (vua 4GB, co the chay GPU khac thoi diem voi MinerU).
- Diem manh: OCR scan rat manh (PP-OCRv6 +11% acc), bang biểu tot (PP-StructureV3),
  nhanh, chay duoc CPU neu GPU ban.
- Cach chay: `pip install paddleocr paddlepaddle-gpu`; CLI `paddleocr ocr -i file.pdf`
  hoac Python API; PP-StructureV3 xuat markdown + JSON.
- Diem yeu: khong bang VLM, doan chua am thanh/kho se thua MinerU VLM.

### 3.2 Docling (IBM) — KHUYEN DUNG SO 2
- License: MIT (thuan hoa thuong mai nhat). VRAM: ~2-3GB, chay tot ca CPU.
- Diem manh: PDF->markdown chat luong cao, bang bieu manh (TableFormer),
  doc duoc DOCX/PPTX/XLSX, co layout model rieng.
- Cach chay: `pip install docling`; CLI `docling file.pdf` -> md/json.

### 3.3 Mistral OCR API — khuyen dung neu dong y cloud
- Tra phi re (~1000 trang/$), xuat markdown gom anh, bang, cong thuc.
- Khong can cai gi local: tool goi REST truc tiep tu C# (de tich hop nhat).
- Luu y: GUIA/ho so ky thuat phai duoc phep gui len cloud.

### 3.4 Tesseract 5 — du phong mien phi
- License Apache-2.0, CPU only, rat nhe. cai UB Mannheim build + pytesseract.
- Diem yeu: khong hieu layout hien dai, md xau; chi dung de kiem cham
  hoan file scan den.

### 3.5 GOT-OCR2.0 — nho, thu vi
- Model 580M (~1.2GB VRAM), chay duoc 4GB de dang. `pip install got-ocr`.
- Chat luong md trung binh; co the chen giua MinerU va Tesseract.

### 3.6 olmOCR / DeepSeek-OCR / marker / Surya — de phat trien sau
- olmOCR-2 (7B) va DeepSeek-OCR (3B): chat luong VLM cao nhung VRAM 6GB+,
  phai doi GPU manh hon hoac chay remote.
- marker & Surya: tot nhung license GPL — han che phan phien.

### 3.7 Azure Document Intelligence / Google Document AI
- Chat luong bang bieu hang dau, nhung gia cao hon Mistral; tich hop REST
  giong phan 3.3.

## 4. LO TRINH DE XUAT (khi duoc phep lam)

- Dot 1: IParseEngine + PaddleOCR — HOAN THANH. Luu y ky thuat: paddle va torch KHONG chung process duoc (xung dot cudnn 9.x) -> paddle phai o venv rieng (paddle-env); CLI dung paddlex.exe --pipeline PP-StructureV3; sau moi file tool tu xoa PNG visualization.
- Dot 2: Docling + cloud engine (REST) + lua chon API key trong UI.
- Dot 3: Che do "so sanh 2 engine cho file KHA NGHI" va bao cao chenh lech.

## 5. GHI CHU KY THUAT

- Khong them NuGet: goi CLI bang Process nhu MinerU hien tai; cloud dung
  HttpClient co san.
- Luu API key vao file `engine-keys.txt` canh exe (khong hardcode).
- Moi engine can 1 dong log [ENGINE <ten>] de phan biet trong batch-log.

## 6. REPOS GITHUB HO TRO (da tham khao)

- PaddlePaddle/PaddleOCR — repo chinh (CLI, PP-StructureV3, PP-OCRv5/v6)
- PaddlePaddle/PaddleX — pipeline engine dang sau paddleocr 3.x (docs PP-StructureV3)
- RapidAI/RapidOCR — phuong an ONNX khong can paddle framework (du phong nhe)
- hiroi-sora/PaddleOCR-json + Umi-OCR — wrapper offline xuat JSON cho Windows
- StephenKaylonChan/paddleocr-guide — vi du Python PDF->Markdown voi PP-StructureV3
