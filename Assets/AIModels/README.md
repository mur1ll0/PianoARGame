# AI Models (Pasta oficial)

Esta e a pasta oficial para modelos ONNX usados pelo PianoDetector.

## Modelo entregue

- Arquivo: `piano_detector_candidateA.onnx`
- Origem: checkpoint YOLOv8 de deteccao de teclado exportado para ONNX
- Formato:
  - Input: `images` -> `[1, 3, 640, 640]`
  - Output: `output0` -> `[1, 6, 8400]`

## Como usar no Unity

1. Selecione o GameObject com `PianoDetector`.
2. No campo `Onnx Model`, arraste `Assets/AIModels/piano_detector_candidateA.onnx`.
3. Ative `Use Onnx Backend`.
4. Configure `Inference Input Size = 640`.
5. Em `TestWebcamController`, rode `Detectar Agora`.

## Observacao importante

Este modelo e especifico para deteccao de teclado em geral (keyboard), nao foi fine-tuned com dataset proprio de piano deste projeto.
Para melhor desempenho em piano real, substitua por um modelo treinado no seu dominio mantendo o mesmo formato de exportacao YOLOv8 ONNX.
