# Piano Vision Training (Base Project)

Projeto base em Python para treinar detector YOLO, avaliar e exportar ONNX para uso no Unity.

## Requisitos

- Python 3.10+ (recomendado 3.11)
- Windows PowerShell
- GPU opcional (em CPU funciona, mas e mais lento)

## Setup rapido

No PowerShell, dentro desta pasta:

python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
pip install -r requirements.txt

## Guia de anotacao do dataset

Para montar e anotar o dataset inicial de forma pratica, use:

- DATASET_ANNOTATION_GUIDE.md

Ele inclui:
- estrutura de pastas
- regra de classes
- como desenhar a caixa de keyboard_area
- checklist de qualidade
- comandos de validacao visual

## Dataset

1. Copie datasets/piano_dataset.example.yaml para datasets/piano_dataset.yaml.
2. Ajuste os caminhos para suas pastas de images/ e labels/.
3. Garanta labels no formato YOLO Segmentation: class x1 y1 x2 y2 ... (normalizado).
4. Se vier em YOLO Detection (class cx cy w h), o script de unificacao converte para poligono retangular.

Classes esperadas:
- 0: keyboard_area

## Treinar

python src/train.py --dataset datasets/piano_dataset_seg.yaml --config configs/train_config.yaml

python src/train.py --dataset datasets/piano_dataset_seg.yaml --config configs/train_config_unity_focus.yaml

Observacao:
- O config atual usa GPU por padrao (`device: 0`).
- Para forcar CPU temporariamente, altere `device` para `cpu` em `configs/train_config.yaml`.

Output esperado:
- runs/piano_detector_candidateB_seg/weights/best.pt

## Avaliar

python src/evaluate.py --weights runs/segment/runs/piano_detector_candidateB_seg/weights/best.pt --dataset datasets/piano_dataset_seg.yaml --config configs/train_config.yaml --out runs/eval_metrics_seg.json

python src/evaluate.py --weights runs/segment/runs/piano_detector_candidateB_seg_unity_focus/weights/best.pt --dataset datasets/piano_dataset_seg.yaml --config configs/train_config_unity_focus.yaml --out runs/eval_metrics_unity_focus.json

## Exportar ONNX

python src/export_onnx.py --weights runs/segment/runs/piano_detector_candidateB_seg/weights/best.pt --config configs/train_config.yaml

python src/export_onnx.py --weights runs/segment/runs/piano_detector_candidateB_seg_unity_focus/weights/best.pt --config configs/train_config_unity_focus.yaml

## Validar contrato ONNX

python src/check_onnx_contract.py --model runs/segment/runs/piano_detector_candidateB_seg/weights/best.onnx --expect-input 1,3,640,640 --expect-output 1,37,8400 --strict

## Smoke test de inferencia ONNX

python src/smoke_infer_onnx.py --model runs/segment/runs/piano_detector_candidateB_seg/weights/best.onnx --image  test_images/IMG_20260426_153544995.jpg --size 640

## Teste visual em lote (gera imagens com deteccao desenhada)

Script:
- src/predict_test_images_onnx.py

### Comando padrao:

python src/predict_test_images_onnx.py --model runs/segment/runs/piano_detector_candidateB_seg/weights/best.onnx --images-dir test_images --out-dir runs/test_predictions --num-classes 1

python src/predict_test_images_onnx.py --model runs/segment/runs/piano_detector_candidateB_seg_unity_focus/weights/best.onnx --images-dir test_images --out-dir runs/test_predictions --num-classes 1

python src/predict_test_images_onnx.py --model "C:\Users\Murillo\Documents\Unity Projects\PianoARGame\Assets\AIModels\pianoB-seg_default640.onnx" --images-dir test_images --out-dir runs/test_predictions --num-classes 1

### Comando com limiar menor (mais sensivel):

python src/predict_test_images_onnx.py --model runs/segment/runs/piano_detector_candidateB_seg/weights/best.onnx --images-dir test_images --out-dir runs/test_predictions_conf005 --conf 0.05 --num-classes 1

### Saida:
- As imagens sao salvas no `out-dir` com prefixo `pred_`.
- Cada bbox desenhada representa `keyboard_area`.

## Validar qualidade das anotacoes antes do treino

1. Validar labels YOLO:

python src/validate_yolo_labels.py --dataset datasets/piano_dataset_seg --num-classes 1

## Usando datasets do Roboflow (piano3, my-first-project-9jrfe, pianokeyboard)

Formato recomendado para download no Roboflow:
- YOLOv8 Segmentation (preferido)
- YOLOv8 (Object Detection) tambem funciona; sera convertido para poligonos

Nao use:
- YOLOv8 Oriented Bounding Box (OBB).

Depois de baixar os 3 datasets, monte um dataset unificado de 1 classe (`keyboard_area`) com:

python src/build_keyboard_only_dataset.py --out datasets/piano_dataset_seg --inputs path\\to\\piano3 path\\to\\my-first-project-9jrfe path\\to\\pianokeyboard --keep-negatives

Esse script:
- converte classes equivalentes para `keyboard_area` (classe 0)
- descarta classe `head`
- preserva imagens negativas (sem teclado) quando `--keep-negatives` estiver ativo
- gera labels no formato de segmentacao (poligonos)

2. Gerar previews com caixas desenhadas:

python src/preview_yolo_labels.py --images datasets/piano_dataset_seg/images/train --labels datasets/piano_dataset_seg/labels/train --out datasets/piano_dataset_seg/previews/train --limit 200

## Deteccao ao vivo pela webcam

Script:
- src/live_camera_detect.py

Abre a camera default, roda o modelo ONNX frame a frame e desenha em verde a
area de teclas detectada com a confianca da predicao ao lado.
Pressione Q ou ESC para fechar.

### Comando padrao (camera 0, modelo unity_focus):

python src/live_camera_detect.py --model runs/segment/runs/piano_detector_candidateB_seg_unity_focus/weights/best.onnx

### Com o modelo exportado para Assets do Unity:

python src/live_camera_detect.py --model "C:\Users\Murillo\Documents\Unity Projects\PianoARGame\Assets\AIModels\pianoB-seg_default640.onnx"

### Opcoes disponiveis:

| Parametro       | Padrao | Descricao                                          |
|-----------------|--------|----------------------------------------------------|
| --model         | —      | Caminho para o .onnx (obrigatorio)                 |
| --camera        | 0      | Indice da camera (0, 1, ...) ou caminho de video   |
| --size          | 640    | Tamanho de entrada do modelo                       |
| --num-classes   | 1      | Numero de classes (1 para keyboard_area)           |
| --conf          | 0.25   | Limiar de confianca minima para exibir deteccao    |
| --iou           | 0.45   | Limiar IoU para NMS                                |
| --width         | 1280   | Largura solicitada da camera                       |
| --height        | 720    | Altura solicitada da camera                        |

### Exemplos adicionais:

# Usar segunda camera com limiar mais baixo:
python src/live_camera_detect.py --model <onnx> --camera 1 --conf 0.15

# Forcar resolucao menor na camera:
python src/live_camera_detect.py --model <onnx> --width 640 --height 480

## Integracao no Unity

1. Copie best.onnx para Assets/AIModels/.
2. No PianoDetector, arraste o model para o campo onnxModel.
3. Ative useOnnxBackend.
4. Configure inferenceInputSize = 640.
5. Teste com seu fluxo real e ajuste onnxConfidenceThreshold.
