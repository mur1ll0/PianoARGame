# PianoARGame - Specs de Treinamento de Modelo de Deteccao para ONNX

Data: 2026-04-26

## 1) Objetivo

Treinar um modelo de visao computacional para detectar:
- o piano (instrumento)
- a area do teclado
- opcionalmente teclas individuais

e exportar para ONNX, de forma compativel com o fluxo atual em `Assets/Scripts/AR/PianoDetector.cs`.

## 2) Resposta direta as suas perguntas

### Deve ser supervisionado?
Sim, para o seu caso a melhor abordagem e supervisionada. Voce quer deteccao precisa de objetos/areas especificas (piano, teclado e teclas), o que exige exemplos anotados.

### Deve ser rede neural?
Sim. O caminho com melhor relacao acuracia/tempo de implementacao para seu projeto e usar detector baseado em YOLO (rede neural convolucional moderna para deteccao).

### Como comecar?
Comece por um modelo de deteccao 2D com classes limitadas, valide no seu dominio real (camera do celular, angulos e luz reais), e so depois evolua para deteccao de teclas individuais.

## 3) Por que Python para treino

Recomendacao: **Python**.

Motivos:
- Ecossistema mais maduro para treino/export ONNX (PyTorch + Ultralytics + ONNX Runtime).
- Ciclo de iteracao rapido para experimentos de hiperparametros.
- Melhor disponibilidade de exemplos e tooling para avaliacao (mAP, PR curves, confusion matrix).

C# fica para inferencia/integração no Unity (ja e o seu caso). JavaScript nao e ideal para esse pipeline de treino.

## 4) Compatibilidade com o seu PianoDetector

Seu detector ja suporta saida no estilo YOLO (`output0` rank 3), com decodificacao geral. Para manter compatibilidade mais previsivel, o plano de producao usa:

- Input ONNX: `[1, 3, 640, 640]`
- Output ONNX: `[1, 6, 8400]` (2 classes)

Classes recomendadas para o modelo principal (Candidate B):
- `0: piano`
- `1: keyboard_area`

Isso preserva o shape de saida usado no modelo candidato atual.

Observacao importante: deteccao de teclas individuais em um unico detector tende a aumentar falsos positivos e custo de anotacao. Para estabilidade, recomenda-se em 2 fases:
- Fase 1 (producao): piano + keyboard_area (2 classes)
- Fase 2 (opcional): modelo dedicado para white_key/black_key ou keypoints/segmentacao

## 5) Fontes pesquisadas (internet)

1. Ultralytics - Train mode (parametros, augmentations, validacao)
   - https://docs.ultralytics.com/modes/train/
2. Ultralytics - Detect task (treino, val, export)
   - https://docs.ultralytics.com/tasks/detect/
3. Ultralytics - Export mode (ONNX args: opset, simplify, dynamic, nms)
   - https://docs.ultralytics.com/modes/export/
4. Ultralytics - Dataset format YOLO
   - https://docs.ultralytics.com/datasets/detect/
5. Ultralytics - Open Images V7 dataset
   - https://docs.ultralytics.com/datasets/detect/open-images-v7/
6. Roboflow - Anotacao e gestao de datasets
   - https://roboflow.com/annotate

## 6) Datasets que voce pode usar

### Opcao A (mais rapida): Roboflow Universe + curadoria manual
- Comecar com dataset publico de keyboard/piano (quando disponivel).
- Revisar anotacoes (qualidade varia bastante).
- Completar com imagens do seu dominio real.

### Opcao B (mais robusta): Open Images V7 filtrado por classes
Open Images V7 contem classes relevantes como:
- `Piano`
- `Musical keyboard`
- `Computer keyboard` (usar com cuidado para nao contaminar dominio)

Uso recomendado:
- Base inicial: Piano + Musical keyboard
- Evitar excesso de Computer keyboard para nao desviar o modelo
- Depois fazer fine-tuning com suas fotos reais

### Tamanho minimo recomendado
- MVP: 800-1500 imagens anotadas
- Produzivel: 3000+ imagens anotadas com distribuicao realista de angulos/luz/oclusao

## 7) Especificacao de anotacao

### 7.1 Modelo principal (recomendado para o projeto atual)
- Task: object detection
- Classes:
  - `piano`
  - `keyboard_area`
- Formato: YOLO txt (`class cx cy w h`, normalizado)

### 7.2 Modelo opcional de teclas (fase 2)
- Task: object detection ou segmentation
- Classes:
  - `white_key`
  - `black_key`
- Recomendacao: somente apos estabilizar modelo principal

## 8) Split e qualidade de dados

Split recomendado:
- train: 70%
- val: 20%
- test: 10%

Regras:
- Separar por cena/video para evitar data leakage entre train e val/test.
- Garantir variedade de:
  - distancia camera-teclado
  - inclinacao/perspectiva
  - luz forte/fraca
  - oclusoes parciais das maos
  - fundos poluidos

## 9) Treino recomendado (baseline)

Modelo: `yolov8n.pt` (leve, bom para mobile)

Parametros iniciais:
- imgsz: 640
- epochs: 120
- batch: 16 (ou `-1` auto)
- optimizer: auto
- patience: 20
- seed: 42

Augmentations sugeridas para seu caso:
- hsv_h=0.015
- hsv_s=0.6
- hsv_v=0.4
- degrees=8
- translate=0.08
- scale=0.4
- shear=2.0
- perspective=0.0008
- fliplr=0.5
- mosaic=0.5
- mixup=0.1

## 10) Criterios de qualidade antes de usar no Unity

No conjunto de teste (nao visto no treino):
- mAP50 (global) >= 0.85
- recall da classe `keyboard_area` >= 0.90
- precision da classe `keyboard_area` >= 0.90
- latencia media inferencia ONNX (CPU desktop) < 20 ms/imagem

Teste de campo (dados reais do app):
- >= 95% dos frames com teclado visivel devem retornar detecao util
- bbox deve cobrir area de teclas sem cortar bordas laterais em mais de 5% dos casos

## 11) Export ONNX para seu projeto

Export recomendado:
- format=onnx
- imgsz=640
- opset=12
- simplify=True
- dynamic=False (mais previsivel para Unity)

Se necessario, testar tambem:
- half=True (se backend suportar)
- nms=False (se post-processamento ficar no C#)

## 12) Validacao de contrato ONNX (obrigatoria)

Antes de copiar para `Assets/AIModels/`:
1. Verificar input tensor:
   - nome esperado: preferencialmente `images`
   - shape: `[1,3,640,640]`
2. Verificar output tensor:
   - nome esperado: geralmente `output0`
   - rank 3
   - shape esperado para 2 classes: `[1,6,8400]`
3. Rodar inferencia smoke test com ONNX Runtime em imagens de teste.
4. Rodar inferencia no Unity e validar bbox em frames reais.

## 13) Plano de execucao recomendado

1. Rodar baseline com dataset pequeno e validar pipeline completo (treino -> val -> onnx -> unity).
2. Expandir dataset com dados reais do seu dispositivo e cena.
3. Fazer 2-3 iteracoes de treino ajustando augmentations/thresholds.
4. Congelar melhor checkpoint (`best.pt`), exportar ONNX e registrar metrics finais.
5. Integrar no Unity e comparar com backend heuristico por A/B test.

## 14) Estrutura de projeto base criada

Foi criado em:
- `tools/piano_vision_training`

Inclui:
- treino
- avaliacao
- export ONNX
- checagem automatica de contrato de shape do ONNX
- smoke test de inferencia ONNX
