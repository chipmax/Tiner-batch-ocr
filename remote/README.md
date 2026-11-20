# Tiner Remote OCR — quét bằng GPU Colab / Kaggle

Máy local (T400 4GB) **không cần quét**. Nó chỉ giữ PDF và mở một server nhỏ;
Colab/Kaggle tải từng job về, quét bằng GPU rồi trả markdown về. Không upload
25 GB lên cloud, không cần tài khoản lưu trữ mới.

```
 F:\books (PDF)                Internet                 Colab / Kaggle (GPU T4)
      │                            │                            │
      ▼                            │                            │
 coordinator.py  ── Cloudflare ────┼── 1. POST /claim ─────────►│
 (SQLite + HTTP)     Tunnel         │◄─ job (tên, trang) ────────│
      │                            │                            │
      │◄──────── 2. GET /pdf/<id> ─┼──── tải PDF ──────────────│
      │                            │                            │
      │◄──────── 3. POST /result ──┼──── main.md + assets ──────│
      ▼                            │
 F:\books-ocr\*.md  ◄── merge ─────┘
```

## 1. Chuẩn bị trên máy local

Cần Python 3.10+ và PyMuPDF:

```powershell
pip install pymupdf
```

Tạo danh sách job (đếm trang, chia mỗi job 40 trang):

```powershell
cd remote
python coordinator.py plan --src "F:\books" --out "F:\books-ocr"
```

## 2. Mở coordinator + tunnel

```powershell
python coordinator.py serve --src "F:\books" --out "F:\books-ocr" --tunnel
```

Lần đầu sẽ tự tải `cloudflared.exe` (~35 MB). Khi chạy, màn hình in ra:

```
URL   : https://xxxx-yyyy.trycloudflare.com
TOKEN : 1a2b3c...
```

Hai giá trị này cũng được lưu vào `remote/connect.json`.

> Nếu không muốn dùng tunnel (cùng LAN), bỏ `--tunnel` và dùng
> `http://<IP-máy-local>:8765`.

## 3. Chạy worker trên Colab

Mở `docs/colab/Tiner_Remote_Colab.ipynb` trên Colab, chọn GPU T4, dán URL +
TOKEN vào ô, chạy 2 cell. Worker sẽ tự xin job đến khi hết hoặc hết giờ.

## 4. Chạy worker trên Kaggle

1. Upload `docs/colab/remote_worker/` thành một Kaggle Kernel
   (`kaggle kernels push -p docs/colab/remote_worker`), hoặc tạo notebook mới
   và dán nội dung `kaggle_kernel.py`.
2. Bật **GPU T4** và **Internet**.
3. Vào **Add-ons → Secrets**, thêm `COORD_URL` và `COORD_TOKEN`.
4. Run All.

Có thể chạy **nhiều session cùng lúc** (1 Colab + 2 Kaggle); mỗi job chỉ một
worker nhận, không trùng.

## 5. Ghép kết quả

Khi worker trả về, chạy:

```powershell
python coordinator.py status
python coordinator.py merge --out "F:\books-ocr"
```

Kết quả: `F:\books-ocr\<thư mục con>\<tên>__remote.md` + thư mục `_assets`.
Job lỗi được đưa lại `pending` bằng:

```powershell
python coordinator.py reset
```

## Engine

| `--engine` | Model / công cụ | Ghi chú |
|---|---|---|
| `mineru4` | MinerU 4.2.3, `--tier standard` = MinerU2.5-Pro 1.2B | Cân bằng, hợp sách scan |
| `paddle` | PaddleOCR-VL-1.6 (0.9B) | Điểm OmniDocBench cao nhất nhóm nhỏ |
| `paddle_structure` | PP-StructureV3 | Nhiều toạ độ bảng/biểu |
| `docling` | IBM Docling | Nhanh, layout tốt |
| `marker` | Marker 2.2 | 4 chế độ, tốt cho PDF gốc số |
| `dots` | dots.ocr 3B | Chất lượng cao, nặng |
| `unlimited` | Unlimited-OCR 3B | Đa trang, nặng |
| `mock` | chỉ trích text layer | Để test đường truyền |

Mặc định nên dùng `mineru4 --tier standard` hoặc `paddle` cho sách scan.

## Lưu ý

- Coordinator phải **bật** trong suốt thời gian worker chạy. Tắt máy → job
  đang chạy được thu hồi sau 30 phút và giao lại.
- Mỗi job tối đa 3 lần thử (`MAX_ATTEMPTS`).
- PDF được cache theo SHA1 trên worker, nên một file nhiều chunk chỉ tải 1 lần.
- Tunnel miễn phí đổi URL mỗi lần chạy; nhớ dán URL mới vào notebook.
