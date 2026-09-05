# HUONG DAN: CHAY MINERU VOI GPU TU XA (B7)

## 1. Nguyen tac hoat dong

- May local chay MinerU voi backend `vlm-http-client` hoac `hybrid-http-client`:
  render PDF thanh anh tung trang, gui anh den server tu xa, nhan ket qua JSON ve
  va tu sinh file .md/content_list nhu binh thuong.
- Server tu xa chay model VLM MinerU2.5 tren GPU manh, phoi ra API tuong thich
  OpenAI tai endpoint: `<server_url>/v1/chat/completions`
- Da kiem chung trong ma nguon `mineru_vl_utils/vlm_client/http_client.py`:
  - `MINERU_VL_SERVER` : URL server (hoac dung `-u/--url` khi goi mineru)
  - `MINERU_VL_API_KEY`: API key dang Bearer token (tuy chon)
  - `MINERU_VL_MODEL_NAME`: ten model (tuy chon; khong co thi tu hoi /v1/models)
  - Timeout 600 giay/yeu cau, toi da 100 yeu cau dong thoi, tu retry 3 lan.
- `vlm-http-client` : moi thu VLM chay tren server.
- `hybrid-http-client`: phan VLM chay tren server, cac buoc con lai chay local.

## 2. Dung trong tool MinerU25Tool

1. O "Engine", chon `vlm-http-client — VLM qua server tu xa (GPU manh)`
   hoac `hybrid-http-client`.
2. Hang "Server URL" hien ra: nhap URL server, vi du `http://203.0.113.10:30000`
   hoac `https://xxx.trycloudflare.com` (khong can them /v1/chat/completions,
   tool chi truyen URL goc).
3. Neu server co dat API key: nhap vao o "API key".
4. Bat "So file dong thoi" = 2 hoac 3 de tan dung server manh.
5. Bam Quet. Tool se goi mineru.exe voi `-b vlm-http-client -u "<url>"`.

## 3. Phuong an A — RunPod / Vast.ai (khuyen dung, GPU manh, tra phi theo gio)

Buoc 1. Thue instance:
- RunPod.io > Deploy > Community Cloud > chon template "RunPod PyTorch 2.x"
  voi GPU RTX 4090 24GB (hoac A100 neu muon nhanh nua).
- Vast.ai: chon template PyTorch tuong tu.

Buoc 2. Mo terminal (SSH hoac web terminal), cai va chay server:
```bash
pip install -U vllm
vllm serve opendatalab/MinerU2.5-Pro-2605-1.2B \
    --host 0.0.0.0 --port 30000 \
    --gpu-memory-utilization 0.9 \
    --max-model-len 16384 \
    --api-key "BI_MAT_CUA_BAN"
```
(Doi model lan dau ~5 phut de tai. Co the dung model cu
`opendatalab/MinerU2.5-2509-1.2B` neu muon.)

Buoc 3. Lay URL:
- RunPod: nut "Connect" > "TCP Port Mapping" — copy dia chi dang
  `tcp://x.x.x.x:yyyyy` -> URL la `http://x.x.x.x:yyyyy`
  (hoac dung Proxy URL HTTP cua RunPod).
- Vast.ai: copy Public IP + port da map.

Buoc 4. Nhap URL + API key vao tool va quet.

Chi phi tham khao: RTX 4090 ~0.3-0.5 USD/gio. 185 file (7340 trang) chay
mat vai chuc phut thay vi nhieu gio.

## 4. Phuong an B — Google Colab (mien phi T4 16GB)

Tao notebook voi 3 cell:

Cell 1:
```python
!pip install -U vllm
```

Cell 2 (chay server o nen):
```python
import subprocess, threading
def run():
    subprocess.run([
        "vllm", "serve", "opendatalab/MinerU2.5-2509-1.2B",
        "--host", "0.0.0.0", "--port", "30000",
        "--gpu-memory-utilization", "0.9",
    ])
threading.Thread(target=run, daemon=True).start()
import time; time.sleep(60)  # cho model nap
```

Cell 3 (mo cong public qua cloudflared):
```python
!wget -q https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64
!chmod +x cloudflared-linux-amd64
import subprocess, re, time
p = subprocess.Popen(["./cloudflared-linux-amd64", "--url", "http://localhost:30000"],
                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
time.sleep(10)
# doc URL dang https://xxxx.trycloudflare.com tu output
import os
os.set_blocking(p.stdout.fileno(), False)
print(p.stdout.read())
```
Lay URL `https://xxxx.trycloudflare.com` nhap vao tool (khong can API key).

Luu y Colab mien phi:
- Session toi da ~12h, mat ket noi la mat server.
- T4 16GB chay duoc model 1.2B, toc do vua phai (nhanh hon T400 local ~5-10 lan).
- Cloudflared URL la tam thoi, chi dung trong phien do.

## 5. Phuong an C — May Linux khac trong cung mang LAN

Tren may Linux co GPU NVIDIA:
```bash
pip install -U vllm
vllm serve opendatalab/MinerU2.5-2509-1.2B --host 0.0.0.0 --port 30000
```
Tren may nay, nhap URL `http://<IP-may-Linux>:30000`.
(vLLM khong ho tro Windows native; neu may GPU chay Windows thi phai dung WSL2.)

## 6. Bao mat

- KHONG mo port 0.0.0.0 ra internet ma khong co API key.
- Luon dat `--api-key` cho vLLM khi server phoi ra cong cong,
  va nhap key do vao tool.
- URL cloudflared/RunPod proxy la cong khai bat ky ai co URL deu goi duoc —
  chi dung tam thoi.

## 7. Hieu nang tham khao (benchmark chinh thuc MinerU)

| He thong                | Toc do        |
|-------------------------|---------------|
| T400 4GB local (hien tai)| ~0.05-0.1 trang/s |
| A100 80GB + vLLM        | ~2.1 trang/s  |
| RTX 4090 + vLLM         | ~0.8-1.2 trang/s (uoc tinh) |

=> 7340 trang: local ~20-40 gio; 4090 ~2 gio; A100 ~1 gio.

## 8. Loi thuong gap

- "Connection refused / timeout": kiem tra URL, port, firewall, server da chay chua.
- Server tra loi cham o yeu cau dau: model dang nap, doi 1-5 phut.
- Loi model name: dat bien moi truong Windows `MINERU_VL_MODEL_NAME` =
  ten model ma server dang serve (vd `opendatalab/MinerU2.5-2509-1.2B`).
- Ket qua trung lap/loi khi chay nhieu file dong thoi tren server yeu:
  giam "So file dong thoi" xuong 1.
