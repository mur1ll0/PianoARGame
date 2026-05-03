param(
    [string]$DatasetYaml = "datasets/piano_dataset.yaml",
    [string]$ConfigYaml = "configs/train_config.yaml",
    [string]$Project = "runs",
    [string]$RunName = "piano_detector_candidateB"
)

$ErrorActionPreference = "Stop"

Write-Host "[1/4] Training"
python src/train.py --dataset $DatasetYaml --config $ConfigYaml --project $Project

$weights = Join-Path $Project "$RunName/weights/best.pt"
$onnx = Join-Path $Project "$RunName/weights/best.onnx"

Write-Host "[2/4] Evaluation"
python src/evaluate.py --weights $weights --dataset $DatasetYaml --config $ConfigYaml --out "$Project/eval_metrics.json"

Write-Host "[3/4] Export ONNX"
python src/export_onnx.py --weights $weights --config $ConfigYaml

Write-Host "[4/4] ONNX contract check"
python src/check_onnx_contract.py --model $onnx --expect-input 1,3,640,640 --expect-output 1,6,8400 --strict

Write-Host "Pipeline complete. ONNX generated at: $onnx"
