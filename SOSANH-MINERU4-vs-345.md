# SO SANH CHI TIET: MINERU 4.x (basic) vs 3.4.5 (hybrid/medium)
Ngay do: 2026-09-03. Cach do: cung 3 file, deu qua server thuong tru (API preload).
3.4.5: mineru-api --enable-vlm-preload, CLI --api-url. 4.x: mineru-kit api-server
--tier basic --preload-models --concurrency 1 --allow-local-source, CLI --remote-url.

## 1. TOC DO (giay, thap hon la nhanh hon)

| File | 3.4.5 hybrid/medium | 4.x basic | Nhan xet |
|---|---|---|---|
| Sanitaire catalogue 88KB (1 trang) | 13s | 8s / 23s (2 lan chay) | Tuong duong |
| DuPont AmberLite PDS 262KB (3 trang) | 212s | 9s / 16s | **4.x nhanh ~13-20 lan** |
| Grindex Drainage Sludge 7MB | 846s roi CRASH chet API | 32s / 42s, OK | **3.4.5 khong xong, 4.x xong** |

Luu y: 4.x co dao dong giua cac lan chay (tai he thong), nhung luon nhanh hon
3.4.5 tu vai lan den hang chuc lan tren file phuc tap.

## 2. CHAT LUONG (so ky tu trong .md)

| File | 3.4.5 (chars) | 4.x (chars) | Nhan xet |
|---|---|---|---|
| Sanitaire | 4801 | 4801 | Giong 100% |
| DuPont AmberLite | 6978 | 6665 | -4.5% (co the do format bang, can theo doi) |
| Grindex | FAIL (khong co output) | 25453 | 4.x thang tuyet doi |

Mat thuong: heading, bang, anh day du ca 2 ben.

## 3. TAI NGUYEN & ON DINH

| Tieu chi | 3.4.5 | 4.x basic |
|---|---|---|
| VRAM thuong tru | ~3.7GB (sat tran 4GB) | ~2.6GB (con ~1.4GB du phong) |
| File lon | OOM, crash chet ca API (Grindex la bang chung truc tiep) | OK |
| Effort | medium / high | basic (~medium). MAT high/xhigh (can lmdeploy — pha torch CUDA tren Windows) |
| Trang thai phan mem | stable | alpha (rui ro dai han chua kiem chung het) |

## 4. TINH NANG & TICH HOP TOOL

| Tieu chi | 3.4.5 | 4.x |
|---|---|---|
| Engine lua chon | hybrid / vlm / pipeline / http-client | chi hybrid (ten cu thanh alias vo nghia) |
| Progress | theo trang (Predict/window/page) | MU — chi 1 dong "Parsed" khi xong -> ETA theo file |
| Output | `<stem>\auto\<stem>.md` + content_list.json | `<stem>\<stem>.md` + anh hash (khong content_list) |
| Resume tool | dir co .md | giong nhau (tuong thich cheo 2 engine) |
| Quality gate tool | OK | OK |
| Cai dat | env Python chinh | venv rieng mineru4x-venv (do torch/paddle xung dot cudnn) |
| Model | mineru.json models-dir.vlm | MINERU_MODEL_BASE_DIR -> tai su dung model local (khong tai lai 2GB) |

## 5. KET LUAN

- 4.x basic thang ap dao ve toc do + on dinh tren may 4GB, chat luong ngang ngua.
- Diem yeu that su duy nhat: mat effort high, mu progress theo trang, alpha.
- Khuyen nghi: batch chinh chay 4.x; file nao [KHA NGHI] thi chay lai bang 3.4.5-high.
  Resume tuong thich cheo nen doi engine bat cu luc nao khong mat tien do.
