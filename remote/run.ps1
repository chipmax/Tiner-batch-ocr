param(
    [string]$Src = "F:\books",
    [string]$Out = "F:\books-ocr",
    [int]$Port = 8765,
    [int]$Chunk = 40
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Host "Khong tim thay python. Cai Python 3.10+ truoc." -ForegroundColor Red
    exit 1
}

Write-Host "== 1/2 Lap danh sach job ==" -ForegroundColor Cyan
python coordinator.py --db "$PSScriptRoot\remote-state.db" plan --src $Src --out $Out --chunk $Chunk
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== 2/2 Mo coordinator + Cloudflare Tunnel ==" -ForegroundColor Cyan
Write-Host "Dan URL + TOKEN hien ra vao notebook Colab/Kaggle." -ForegroundColor Yellow
python coordinator.py --db "$PSScriptRoot\remote-state.db" serve --src $Src --out $Out --port $Port --tunnel
