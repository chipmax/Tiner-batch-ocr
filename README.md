# MinerU25Tool — Batch OCR đa engine cho Windows

Tool WPF (.NET 8) chạy OCR hàng loạt file PDF thành Markdown, hỗ trợ nhiều engine,
chạy batch unattended (tự resume, tự tắt máy), giao diện Việt/Anh, Sáng/Tối.

> **English summary:** a Windows WPF (.NET 8) batch-OCR tool that converts PDFs to
> Markdown with multiple engines (MinerU 3.x / MinerU 4.x / PaddleOCR / Windows OCR),
> unattended batch (resume, pause, auto-shutdown), Vietnamese/English UI, light/dark themes.

## Tính năng

- **Engines**: `hybrid-engine` (khuyên dùng), `vlm-engine`, `pipeline` (PP-OCR),
  `vlm-http-client` / `hybrid-http-client` (server GPU từ xa),
  `paddleocr` (PP-StructureV3, VRAM nhẹ), `mineru4x` (MinerU 4.x server thường trú),
  `windows-ocr` (WinRT, miễn phí, CPU).
- **Batch**: song song 1–3 file qua `mineru-api` thường trú, resume (`.done` marker),
  thử lại khi lỗi (tuỳ chọn nâng effort), timeout mỗi file, **tạm dừng/tiếp tục**,
  kiểm tra chất lượng tự động (đánh dấu KHA NGHI + chéo kiểm engine 2).
- **Tự động**: smart routing theo file (text/scan), watch folder (file mới tự chạy),
  tắt máy khi xong, âm báo, mở cùng Windows.
- **Tra cứu**: tìm kiếm toàn văn trong mọi `.md` đã quét; báo cáo `batch-report.html`.
- **Hệ thống**: kiểm tra từng thành phần theo bảng (bắt buộc = đỏ, tuỳ chọn = vàng),
  **cài đặt toàn bộ hoặc từng mục** bằng pip/venv tự động.
- **Giao diện**: 5 tabs (Cấu hình, Tiến độ, Tìm kiếm, Hệ thống, Cài đặt),
  cỡ chữ 80–150%, nền xuyên thấu, luôn trên cùng, F1 trợ giúp, chạy CLI
  (`--autostart --src --out --engine`).

## Yêu cầu

- Windows 10/11 x64, .NET 8 SDK (chỉ để build).
- Python 3.10+ cho engines (tool có nút **Kiểm tra** + **Cài đặt tự động**).
- GPU NVIDIA (khuyên dùng; không có vẫn chạy CPU, chậm).

## Build & chạy

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Mở `MinerU25Tool.exe`, vào tab **Hệ thống** → **Kiểm tra** → **Cài đặt tự động**
(nếu thiếu), chọn thư mục PDF rồi **BẮT ĐẦU**.

## Tài liệu

- `HUONG-DAN.txt` — hướng dẫn sử dụng (tiếng Việt).
- `HUONG-DAN-GPU-TU-XA.md` — chạy VLM trên GPU thuê ngoài.
- `SOSANH-MINERU4-vs-345.md` — so sánh MinerU 4.x vs 3.4.5.
- `HUONG-DAN-DE-XUAT-OCR-ENGINES.md` — các engine OCR khác đã khảo sát.

## Giấy phép

MIT — xem `LICENSE`.
