# MinerU25Tool — Batch OCR đa engine cho Windows

**[English](README.md)**

Tool WPF (.NET 8) chạy OCR hàng loạt file PDF thành Markdown, hỗ trợ nhiều engine,
chạy batch unattended (tự resume, tạm dừng, tự tắt máy), giao diện Việt/Anh,
Sáng/Tối.

![Tab Cấu hình](docs/screenshots/01-config.png)
![Tab Tiến độ](docs/screenshots/02-progress.png)

## Tính năng

- **Engines**: `hybrid-engine` (khuyên dùng), `vlm-engine`, `pipeline` (PP-OCR),
  `vlm-http-client` / `hybrid-http-client` (server GPU từ xa),
   `paddleocr` (PP-StructureV3, VRAM nhẹ, gộp 1 `.md` mỗi file qua
   `paddle_run.py`, tắt module seal/formula/chart), `mineru4x` (MinerU 4.x server thường trú),
  `windows-ocr` (OCR có sẵn của Windows, miễn phí, CPU).
- **Batch**: song song 1–3 file qua `mineru-api` thường trú, resume (marker `.done`),
  chạy lại khi lỗi (tuỳ chọn nâng effort), timeout mỗi file, **tạm dừng/tiếp tục**,
  kiểm tra chất lượng tự động (đánh dấu KHA NGHI + chéo kiểm engine 2).
- **Kết quả**: `.md` + `.txt` thuần (+ ảnh, bảng) mỗi file, `batch-report.html`,
  `batch-log.txt`, `batch-state.json`.
- **Tự động**: smart routing theo file (text/scan), tự quét file mới,
  tắt máy khi xong, âm báo, mở cùng Windows.
- **Tìm kiếm**: tìm toàn văn trong mọi file `.md` đã quét.
- **Hệ thống**: bảng kiểm tra từng thành phần (đỏ = bắt buộc, vàng = tuỳ chọn),
  **cài toàn bộ hoặc từng mục** bằng pip/venv tự động.
- **Giao diện**: 5 tabs (Cấu hình, Tiến độ, Tìm kiếm, Hệ thống, Cài đặt),
  cỡ chữ 80–150%, nền xuyên thấu, luôn trên cùng, trợ giúp F1, chạy CLI
  (`--autostart --src --out --engine`).

## Yêu cầu

- Windows 10/11 x64. [.NET 8 SDK](https://dotnet.microsoft.com/download) (chỉ để build).
- Python 3.10+ cho engines — dùng tab **Hệ thống** → **Kiểm tra** →
  **Cài đặt tự động** ngay trong tool.
- Khuyên dùng GPU NVIDIA (không có vẫn chạy CPU, chậm hơn nhiều).

## Build & chạy

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Mở `MinerU25Tool.exe`, vào tab **Hệ thống** → **Kiểm tra** → **Cài đặt tự động**
(nếu thiếu), chọn thư mục PDF rồi bấm **BẮT ĐẦU**.
Xem [HUONG-DAN.txt](HUONG-DAN.txt) để biết chi tiết.

## Ảnh giao diện

| Cấu hình | Tiến độ | Tìm kiếm |
|---|---|---|
| ![Cấu hình](docs/screenshots/01-config.png) | ![Tiến độ](docs/screenshots/02-progress.png) | ![Tìm kiếm](docs/screenshots/03-search.png) |

| Hệ thống | Cài đặt | Chế độ tối |
|---|---|---|
| ![Hệ thống](docs/screenshots/04-system.png) | ![Cài đặt](docs/screenshots/05-settings.png) | ![Tối](docs/screenshots/06-dark.png) |

## Giấy phép

MIT — xem [LICENSE](LICENSE).
