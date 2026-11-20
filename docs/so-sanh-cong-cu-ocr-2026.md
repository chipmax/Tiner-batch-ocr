# So sánh công cụ OCR / document parsing tương đương MinerU — 11/2026

Mục tiêu: chọn engine cho lô **2.193 PDF / 25,4 GB** sách kỹ thuật tiếng Việt
(nước cấp, nước thải, bùn, hoá chất, bơm–thiết bị, thuỷ lực, môi trường) chạy
trên GPU remote Colab/Kaggle T4.

> Lưu ý quan trọng: **đừng chọn theo một bảng điểm duy nhất**. OmniDocBench đo
> tài liệu sạch, số hoá; MDPBench đo tài liệu đa ngữ + ảnh chụp/scan. Sách của
> bạn thuộc nhóm thứ hai, nên cột **VI (MDPBench)** quan trọng hơn Overall của
> OmniDocBench.

---

## 1. Kết luận nhanh

| Hạng | Công cụ | Lý do |
|---|---|---|
| **1 (khuyên thử trước)** | **MonkeyOCRv2-B-Parsing** | 0,88B, Apache-2.0, **VI 83,2** — điểm tiếng Việt cao nhất trong nhóm nhỏ, chạy T4 rất nhẹ |
| **2** | **SenOCR-Vi** | 0,96B, Apache-2.0, **VI 82,83** (eval riêng) — fine-tune riêng cho tiếng Việt từ PaddleOCR-VL-1.6 |
| **3** | **PaddleOCR-VL-1.6** | 0,9B, Apache-2.0, **VI 79,3**, mạnh bảng/công thức, pipeline hoàn chỉnh, dễ tích hợp |
| **4** | **MinerU 4.2.3 / MinerU2.5-Pro** | 1,2B, **VI 74,2** nhưng tích hợp sẵn, ổn định, tốt cho PDF gốc số |
| **5 (chất lượng cao, license hạn chế)** | **Chandra 2** | 5B, **VI 85,6** — cao nhất mã nguồn mở, nhưng model OpenRAIL-M hạn chế thương mại |
| **6** | **olmOCR 2 (7B)** | Apache-2.0, **VI 84,0** — tốt nhưng 7B, T4 phải dùng 4-bit/FP8 |

**Tránh cho tiếng Việt scan**: Unlimited-OCR và DeepSeek-OCR (OmniDocBench rất
cao nhưng MDPBench VI chỉ ~54), PP-StructureV3 / MinerU-pipeline cho bản scan
(Việt 52,7 / 27,6).

---

## 2. Hai bộ benchmark

### 2.1 OmniDocBench v1.6 — tài liệu sạch, số hoá

Điểm Overall càng cao càng tốt (thang 100):

| Model | Loại | Tham số | Overall | Ghi chú |
|---|---|---:|---:|---|
| OvisOCR2 | VLM | 0,8B | **96,47** | Mới, ít tài liệu |
| MiniCPM-OCR 1.0 | VLM | 1,3B | **96,44** | Mới |
| PaddleOCR-VL-1.6 | VLM | 0,9B | **96,34** | Bảng/công thức rất mạnh |
| MinerU2.5-Pro | VLM | 1,2B | **95,75** | Model của MinerU 4.x standard |
| Unlimited-OCR | VLM | 3B | 94,00 | Sụp trên VI/scan (xem dưới) |
| MonkeyOCR V2 | VLM | ~0,9B | 91,80 | Bản Parsing |
| dots.ocr | VLM | 3B | 90,77 | |
| DeepSeek-OCR 2 | VLM | 3B | 90,25 | Sụp trên VI/scan |
| HunyuanOCR | VLM | 1B | 89,95 | License Tencent |
| Qwen3-VL-235B | General | 235B | 89,78 | Cần server lớn |
| MinerU-Pipeline | Pipeline | nhỏ | 86,47 | Bản pipeline cũ |
| olmOCR (7B) | VLM | 7B | 85,74 | |
| Marker | Pipeline | nhỏ | 78,44 | PDF gốc số |

### 2.2 MDPBench — 17 ngôn ngữ, tài liệu số + ảnh chụp

Cột **VI** là điểm tiếng Việt (thang 100). Đây là bảng sát với sách scan của bạn:

| Model | Overall | **VI** | Ghi chú |
|---|---:|---:|---|
| Gemini-3-pro (cloud) | 86,4 | **85,9** | Đắt, gửi dữ liệu ra ngoài |
| kimi-K2.5 (cloud) | 77,5 | **86,2** | Đắt |
| **chandra-ocr-2** | 79,7 | **85,6** | 5B, OpenRAIL-M |
| **olmOCR 2 (7B)** | 70,4 | **84,0** | Apache-2.0 |
| Claude-Sonnet-4.6 (cloud) | 73,1 | 83,1 | |
| **MonkeyOCRv2-B-Parsing** | **83,3** | **83,2** | 0,88B, Apache-2.0 |
| HunyuanOCR-1.5 | 76,8 | 82,4 | License Tencent |
| ChatGPT-5.2 (cloud) | 68,6 | 82,1 | |
| MonkeyOCRv2-S-Parsing | 82,5 | 81,9 | Nhỏ hơn |
| dots.mocr | 80,5 | 79,9 | 3B, MIT |
| PaddleOCR-VL-1.6 | 75,0 | 79,3 | 0,9B, Apache-2.0 |
| dots.ocr | 76,5 | 79,1 | 3B, MIT |
| PaddleOCR-VL-1.5 | 78,3 | 78,9 | |
| **MinerU2.5-Pro** | 71,0 | **74,2** | 1,2B, model của MinerU 4.x |
| Qwen3.5-9B | 65,7 | 74,0 | General |
| Qwen3-VL-8B | 68,3 | 73,4 | General |
| HunyuanOCR (1.0) | 68,3 | 80,6 | |
| Unlimited-OCR | — | **~54** | Cảnh báo |
| DeepSeek-OCR | 51,8 | **54,1** | Cảnh báo |
| MinerU-2.5-VLM | 46,3 | 46,0 | |
| PP-StructureV3 | 45,4 | **52,7** | |
| MinerU-2.5-pipeline | 33,5 | **27,6** | |

> SenOCR-Vi không nằm trong MDPBench; tác giả công bố **82,83** trên bộ 160
> trang tiếng Việt tự hiệu chỉnh. Con số này ngang MonkeyOCRv2-B (83,2), nên
> cần kiểm chứng trên dữ liệu của bạn.

---

## 3. Bảng tổng hợp

| Công cụ / model | Kiểu | Tham số | VRAM (ước tính) | OmniDoc | MDPBench VI | License (code / model) | Sao GH | T4 16GB | Ghi chú |
|---|---|---:|---|---:|---:|---|---:|---|---|
| **MonkeyOCRv2-B-Parsing** | VLM parser | 0,88B | 3,3 GB @8K; 6,8 GB @40K | 91,8 | **83,2** | Apache-2.0 | 6,7k | Rất tốt | Cân bằng nhất |
| **SenOCR-Vi** | VLM (PaddleOCR-VL ft) | 0,96B | ~3–4 GB | — | **82,83*** | Apache-2.0 | — | Rất tốt | Yếu bảng/formula |
| **PaddleOCR-VL-1.6** | VLM + pipeline | 0,9B | 4–6 GB | 96,34 | 79,3 | Apache-2.0 | 92,9k | Rất tốt | Bảng/formula mạnh |
| **MinerU 4.2.3** | VLM + pipeline | 1,2B | 4–8 GB | 95,75 | 74,2 | Apache-2.0 + điều khoản | 84k | Rất tốt | Tích hợp sẵn |
| **Chandra 2** | VLM | 5B | 12–16 GB | — | **85,6** | Apache-2.0 / OpenRAIL-M | 12,7k | Chật, nên 4-bit | Bố cục tốt |
| **olmOCR 2** | VLM | 7B | 12–16 GB (FP8 9,6 GB) | 85,7 | **84,0** | Apache-2.0 | 19,8k | Cần 4-bit | Dùng vLLM |
| **dots.mocr** | VLM | 3B | 5,8 GB → 8–10 GB | — | 79,9 | MIT | 416 | Tốt | Đa trang |
| **dots.ocr** | VLM | 3B | 5,8 GB → 8–10 GB | 90,77 | 79,1 | MIT | 9,2k | Tốt | |
| **HunyuanOCR-1.5** | VLM | ~1B | 4,4 GB → 6–8 GB | 89,95¹ | 82,4 | Tencent Community | 2,1k | Tốt | 1.5 chưa rõ weights |
| **MiniCPM-OCR 1.0** | VLM | 1,3B | 2,5 GB → 4–6 GB | 96,44 | — | Apache-2.0 | ~1k | Rất tốt | Ít benchmark VI |
| **OvisOCR2** | VLM | 0,8B | 1,6 GB → 3–5 GB | 96,47 | — | Apache-2.0 | — | Rất tốt | Mới, ít tài liệu |
| **Qwen3-VL-8B / 3.5-9B** | General VLM | 8–9B | 16 GB+ | — | 73,4 / 74,0 | Apache-2.0 | 20,2k | Chật | Cần prompt |
| **Unlimited-OCR** | VLM | 3B | 8–12 GB | 94,00 | ~54 | MIT | 31,3k | Tốt | Không hợp VI |
| **DeepSeek-OCR 2** | VLM | 3B | 8–10 GB | 90,25 | 54,1 | MIT | 24,2k | Tốt | Không hợp VI |
| **PP-StructureV3** | Pipeline | nhỏ | 2–6 GB | — | 52,7 | Apache-2.0 | 92,9k | Rất tốt | Nhanh, có toạ độ |
| **MinerU-pipeline** | Pipeline | nhỏ | 2–6 GB | 86,47 | 27,6 | Apache-2.0 + | 84k | Rất tốt | Bản cũ |
| **Docling** | Pipeline | nhỏ | 2–8 GB | — | — | MIT / CDLA-Permissive | 70,8k | Tốt | PDF gốc số |
| **Marker** | Pipeline | nhỏ | 2–8 GB | 78,44 | — | Apache-2.0 / OpenRAIL-M | 41k | Tốt | PDF gốc số |
| **VietOCR** | Nhận dòng chữ | ~0,1B | CPU | — | — | Apache-2.0 | ~2k | CPU | Không làm layout |
| **PDF-Extract-Kit** | Pipeline | nhỏ | 4–8 GB | — | — | AGPL-3.0 | 10,1k | Tốt | Nền MinerU cũ |

¹ điểm HunyuanOCR 1.0; ¹* SenOCR-Vi là eval riêng, không phải MDPBench.

---

## 4. Phân tích từng ứng viên

### 4.1 MinerU 4.2.3 / MinerU2.5-Pro (baseline hiện tại)

- **Ưu**: tích hợp sẵn trong tool, pipeline hoàn chỉnh (layout + OCR + bảng +
  công thức + reading order), xuất markdown, có `basic` (không VLM) và
  `standard/advanced` (VLM). Cộng đồng lớn nhất (84k sao), tài liệu tốt.
- **Nhược**: trên MDPBench tiếng Việt chỉ **74,2** — thấp hơn MonkeyOCR,
  SenOCR-Vi, PaddleOCR-VL, Chandra, olmOCR. Bản 4.1+ đổi config
  (`mineru.json` → `config.yaml`), bỏ `--allow-local-source` — tool hiện tại
  sẽ lỗi nếu nâng mà không sửa.
- **Kết luận**: giữ làm baseline và phương án ổn định, nhưng **không phải lựa
  chọn số 1 cho sách scan tiếng Việt**.

### 4.2 PaddleOCR-VL-1.6 / PP-StructureV3

- **Ưu**: 0,9B, Apache-2.0, OmniDoc **96,34** (nhất nhóm nhỏ), bảng/công thức
  rất mạnh, 109 ngôn ngữ, pipeline `paddleocr doc_parser` xuất markdown + JSON
  có toạ độ. T4 chạy thoải mái.
- **Nhược**: MDPBench VI **79,3** — khá nhưng dưới MonkeyOCR/SenOCR. Cài
  `paddlepaddle-gpu` trên Kaggle/Colab đôi khi kéo wheel CPU, cần kiểm tra
  `paddle.utils.run_check()`.
- **Kết luận**: lựa chọn **an toàn cho bảng biểu/công thức**, đặc biệt nếu sách
  có nhiều bảng số liệu.

### 4.3 MonkeyOCRv2-B-Parsing

- **Ưu**: 0,88B, Apache-2.0, **VI 83,2** — cao nhất trong nhóm dưới 1B, VRAM
  chỉ 3,3 GB @8K / 6,8 GB @40K. Có bản `-DFlash` chạy vLLM nhanh gấp đôi.
  Repo `Yuliang-Liu/MonkeyOCR` (6,7k sao) đang active (11/2026).
- **Nhược**: ít tài liệu tiếng Việt hơn Paddle/MinerU; cần `transformers 4.57`
  + `flash-attn` (hoặc vLLM) — trên T4 nên dùng vLLM để tránh build flash-attn.
- **Kết luận**: **ứng viên số 1** cho sách scan tiếng Việt trên T4.

### 4.4 SenOCR-Vi

- **Ưu**: fine-tune 1,26% từ PaddleOCR-VL-1.6 cho tiếng Việt, **82,83** (eval
  riêng), Apache-2.0, 0,96B, dùng được như model nhận dạng trong pipeline
  PaddleOCR-VL hoặc qua `transformers`. Rất nhẹ.
- **Nhược**: tác giả nói rõ **không chuyên bảng/công thức** (TEDS 70,7); chưa
  có benchmark production về tốc độ/VRAM; là nghiên cứu mới (11/2026), ít
  người dùng.
- **Kết luận**: đáng thử song song với PaddleOCR-VL-1.6 **nếu sách chủ yếu là
  chữ**; không dùng một mình cho sách nhiều bảng.

### 4.5 dots.ocr / dots.mocr

- **Ưu**: MIT, 3B, OmniDoc 90,77 (dots.ocr), MDPBench VI 79,1–79,9; dots.mocr
  hỗ trợ đa trang. Chất lượng bố cục tốt.
- **Nhược**: 3B → 8–10 GB, nặng hơn nhóm 1B; repo dots.ocr chậm cập nhật
  (03/2026), dots.mocr ít sao (416).
- **Kết luận**: phương án dự phòng tốt nếu MonkeyOCR/Paddle gặp lỗi.

### 4.6 Chandra 2

- **Ưu**: **VI 85,6** — cao nhất trong các model mở, xuất markdown/HTML/JSON
  kèm layout, mạnh bảng/công thức/scan cũ. Code Apache-2.0.
- **Nhược**: 5B, 10,1 GB weights → 12–16 GB, T4 rất chật (nên 4-bit/GGUF);
  **model dùng OpenRAIL-M sửa đổi**: miễn phí cho nghiên cứu/cá nhân/startup
  dưới 2 triệu USD, **không được dùng cạnh tranh với API Datalab**, thương mại
  lớn phải mua license.
- **Kết luận**: dùng để **đo trần chất lượng** trên mẫu nhỏ; nếu thắng rõ và
  bạn không bị ràng buộc thương mại, có thể dùng 4-bit trên T4.

### 4.7 olmOCR 2 (7B)

- **Ưu**: Apache-2.0, **VI 84,0**, chuyên tài liệu scan, có FP8.
- **Nhược**: 7B, khuyến nghị ≥12 GB VRAM, 30 GB đĩa; T4 16 GB phải dùng FP8
  (T4 không có FP8 native) hoặc 4-bit; cần vLLM riêng.
- **Kết luận**: chất lượng cao, license tốt, nhưng **chi phí vận hành trên T4
  cao**; phù hợp nếu có L4/A100 hoặc chấp nhận 4-bit chậm.

### 4.8 HunyuanOCR

- **Ưu**: 1B, OmniDoc 89,95, VI (bản 1.5) 82,4.
- **Nhược**: license **Tencent Hunyuan Community** (không phải Apache/MIT; có
  điều kiện thương mại/khu vực), bản 1.5 chưa thấy weights chính thức rõ ràng
  trên HF tại thời điểm khảo sát.
- **Kết luận**: theo dõi, chưa ưu tiên.

### 4.9 Unlimited-OCR / DeepSeek-OCR 2 — cảnh báo

Cả hai đạt OmniDocBench rất cao (94,0 / 90,25) nhưng **sụp trên MDPBench tiếng
Việt/scan** (~54). Đây là ví dụ điển hình của việc benchmark sạch không đại
diện cho sách scan đa ngữ. **Không chọn** cho lô này.

### 4.10 MiniCPM-OCR / OvisOCR2

- OmniDoc 96,44 / 96,47 — top đầu, rất nhỏ (1,3B / 0,8B), Apache-2.0.
- Chưa có điểm MDPBench VI; tài liệu và cộng đồng còn mới. Đáng đưa vào **thử
  nghiệm 30–50 trang** nếu muốn tối đa chất lượng/nhỏ gọn.

### 4.11 Pipeline: Docling, Marker, PP-StructureV3, MinerU-pipeline

- **Docling** (MIT, 70,8k sao): mạnh PDF gốc số, layout/bảng, xuất markdown;
  không phải VLM nên với bản scan tiếng Việt thường kém hơn.
- **Marker** (41k sao): 4 chế độ, tốt PDF gốc số; model OpenRAIL-M; OmniDoc
  78,44.
- **PP-StructureV3**: nhanh, có toạ độ ô bảng; MDPBench VI 52,7 — **không dùng
  cho scan tiếng Việt**.
- **MinerU-pipeline**: bản cũ, VI 27,6 — tránh.
- **Kết luận**: chỉ dùng pipeline cho **PDF gốc số có text layer** (miễn phí,
  chính xác) hoặc khi cần toạ độ; không dùng cho scan.

### 4.12 Cloud API (tham khảo)

Gemini-3-Pro (VI 85,9), Kimi-K2.5 (86,2), Claude-Sonnet-4.6 (83,1) đứng đầu
MDPBench VI nhưng: tốn tiền theo trang, gửi tài liệu ra ngoài, không chạy
offline, khó xử lý 2.193 file với chi phí thấp. Chỉ nên dùng để **tạo gold
set** cho 30–50 trang đánh giá.

---

## 5. Giấy phép & dùng thương mại

| License | Công cụ | Điều kiện chính |
|---|---|---|
| Apache-2.0 | PaddleOCR, MonkeyOCR, olmOCR, dots.*, DeepSeek-OCR, Unlimited-OCR, Qwen3-VL, MiniCPM-OCR, OvisOCR2, SenOCR-Vi, VietOCR | Dùng thương mại tự do, giữ notice |
| MIT | Docling, dots.ocr, dots.mocr, DeepSeek-OCR | Gần như tự do |
| Apache-2.0 + điều khoản | **MinerU** | Thương mại miễn phí nếu MAU <100 triệu và doanh thu <20 triệu USD/tháng; nếu cung cấp dịch vụ online phải ghi công MinerU |
| OpenRAIL-M | **Marker**, **Chandra 2** | Miễn phí nghiên cứu/cá nhân/startup nhỏ; hạn chế dùng cạnh tranh API gốc; thương mại lớn phải xin phép |
| Tencent Hunyuan Community | **HunyuanOCR** | Có điều kiện thương mại/khu vực, không tương đương Apache |
| CDLA-Permissive-2.0 | Model của Docling | Dùng thương mại được |
| AGPL-3.0 | PDF-Extract-Kit | Copyleft mạnh; tránh nếu không muốn mở source |

Với mục đích nội bộ của bạn, **MonkeyOCR / SenOCR-Vi / PaddleOCR-VL / MinerU**
đều dùng thương mại được. **Chandra/Marker** cần cân nhắc nếu sau này bán dịch
vụ.

---

## 6. Khuyến nghị cho `F:\books`

### Bước 0 — tách PDF gốc số khỏi bản scan

Trước khi OCR, dò text layer bằng PyMuPDF. PDF gốc số (có text) chỉ cần trích
text + layout, không cần GPU. Ước tính sẽ giảm đáng kể 25,4 GB cần xử lý.

### Bước 1 — benchmark 4 ứng viên trên 30–50 trang thật

Chọn trang đại diện 8 nhóm, có đủ: chữ thường, chữ nhỏ, bảng, công thức, hình,
scan mờ, chữ có dấu.

| Engine | Model | Lý do chọn |
|---|---|---|
| `monkeyocr` | MonkeyOCRv2-B-Parsing | VI cao nhất nhóm nhỏ |
| `paddle` | PaddleOCR-VL-1.6 | Bảng/công thức tốt nhất |
| `senocr` | SenOCR-Vi | Chuyên tiếng Việt, nhẹ |
| `mineru4` | MinerU2.5-Pro | Baseline, tích hợp sẵn |

Nếu cần đo trần chất lượng, thêm `chandra` (4-bit) và `olmocr` (4-bit).

### Bước 2 — chấm điểm

- **CER/WER** tiếng Việt (giữ dấu và bỏ dấu), chuẩn hoá Unicode NFC.
- **Bảng**: TEDS hoặc so khớp ô thủ công.
- **Công thức**: edit distance LaTeX hoặc đếm lỗi.
- **Reading order**: đánh giá thủ công 1–5.
- **Hallucination**: đếm câu/đoạn model tự thêm.
- **Tốc độ**: giây/trang, trang/giờ trên T4.
- **VRAM đỉnh**: `nvidia-smi` trong lúc chạy.

### Bước 3 — chọn

- Nếu **MonkeyOCRv2-B** thắng hoặc ngang Paddle: chọn nó (nhẹ, license sạch).
- Nếu sách nhiều bảng/công thức: chọn **PaddleOCR-VL-1.6**, có thể thay model
  nhận dạng bằng **SenOCR-Vi** cho phần chữ.
- Nếu cần chất lượng tối đa và chấp nhận license/VRAM: **Chandra 2** hoặc
  **olmOCR 2** bản 4-bit.
- Giữ **MinerU 4.2.3** làm fallback và cho PDF gốc số.

---

## 7. Tích hợp vào remote worker

Worker hiện có: `mineru4`, `paddle`, `paddle_structure`, `dots`, `unlimited`,
`docling`, `marker`, `mock`. Cần thêm:

| Engine mới | Cài đặt | Lệnh chính |
|---|---|---|
| `monkeyocr` | `pip install transformers==4.57.1 accelerate qwen_vl_utils` + tải `zenosai/MonkeyOCRv2-B-Parsing` | Dùng `parsing/` của repo MonkeyOCR hoặc vLLM với bản `-DFlash` |
| `senocr` | `pip install "transformers>=5.0" paddleocr[doc-parser]` | Thay model nhận dạng trong pipeline PaddleOCR-VL bằng `VietAlphaLabs/SenOCR-Vi` |
| `chandra` | `pip install chandra-ocr` (hoặc GGUF 4-bit) | `chandra_vllm` + `chandra-ocr` |
| `olmocr` | `pip install olmocr` + vLLM | `olmocr <workspace> --markdown --pdfs ...` |

Sau khi thêm, chạy cùng một coordinator với `--engine monkeyocr` cho lô thử để
so sánh trực tiếp.

---

## 8. Nguồn

- OmniDocBench v1.6 — https://github.com/opendatalab/OmniDocBench
- MDPBench — https://github.com/Yuliang-Liu/MultimodalOCR/tree/main/MDPBench
- MinerU — https://github.com/opendatalab/MinerU (license + docs 4.2.3)
- PaddleOCR / PaddleOCR-VL — https://github.com/PaddlePaddle/PaddleOCR
- MonkeyOCR — https://github.com/Yuliang-Liu/MonkeyOCR
- SenOCR-Vi — https://huggingface.co/VietAlphaLabs/SenOCR-Vi
- dots.ocr — https://github.com/rednote-hilab/dots.ocr
- Chandra — https://github.com/datalab-to/chandra
- olmOCR — https://github.com/allenai/olmocr
- Docling — https://github.com/docling-project/docling
- Marker — https://github.com/datalab-to/marker
- HunyuanOCR — https://github.com/Tencent-Hunyuan/HunyuanOCR

*Số liệu lấy ngày 20/11/2026. Điểm benchmark thay đổi theo phiên bản model và
cách chuẩn hoá; luôn kiểm chứng lại trên 30–50 trang dữ liệu thật trước khi
chạy toàn bộ 2.193 PDF.*
