# Guia Pratico: Dataset Inicial e Anotacao Supervisionada

Data: 2026-04-26

## Objetivo deste guia

Este guia mostra, na pratica, como montar seu primeiro dataset e como anotar as imagens para o modelo principal do projeto.

Resposta curta para sua pergunta principal:
- Sim, voce deve desenhar a area das teclas.
- Para o seu caso atual, use 1 classe:
  - classe 0: keyboard_area (somente a faixa de teclas)

## 1) Estrutura recomendada do dataset inicial

Dentro de tools/piano_vision_training, use esta estrutura:

- datasets/piano_dataset/
- datasets/piano_dataset/images/train/
- datasets/piano_dataset/images/val/
- datasets/piano_dataset/images/test/
- datasets/piano_dataset/labels/train/
- datasets/piano_dataset/labels/val/
- datasets/piano_dataset/labels/test/

Cada imagem deve ter um arquivo .txt com o mesmo nome na pasta labels correspondente.

Exemplo:
- images/train/scene01_frame001.jpg
- labels/train/scene01_frame001.txt

## 2) Quantidade inicial (MVP)

Comece com:
- 300 imagens train
- 80 imagens val
- 80 imagens test

Depois suba para 800-1500 imagens totais.

## 3) Como capturar imagens boas para treino

Capture com diversidade real:
- Distancia curta, media e longa
- Angulo frontal e inclinado
- Luz forte, fraca e mista
- Parte do teclado ocluida por mao/braco
- Fundo simples e fundo poluido
- Diferentes modelos de piano/teclado (se possivel)

Regra de ouro: capture cenas parecidas com as que o app vai ver em producao.

## 4) Como anotar (passo a passo)

Ferramentas sugeridas:
- Roboflow Annotate (web)
- CVAT
- Label Studio

Crie 1 classe:
- 0 = keyboard_area

Para cada imagem:
1. Desenhe uma bounding box da classe keyboard_area cobrindo apenas a faixa de teclas.
2. Ajuste as caixas para ficarem justas, sem margem exagerada.
3. Exporte em formato YOLO.

## 5) Regras de anotacao (muito importante)

### Classe keyboard_area (0)
- Deve cobrir somente a area das teclas (brancas + pretas).
- Nao incluir tampa, corpo lateral, pedestal, partitura.
- Mesmo com perspectiva inclinada, desenhe a menor caixa retangular que cubra toda a faixa de teclas visivel.

### Oclusao
- Se as teclas estiverem parcialmente cobertas (ex.: mao), anote do mesmo jeito.
- Se a area de teclas visivel for menor que ~20% da area total esperada, descarte a imagem ou marque para analise posterior.

### Corte de borda (teclado saindo da imagem)
- Pode manter no dataset, desde que haja exemplos completos tambem.
- Mantenha maximo de 20% de imagens muito truncadas.

## 6) Exemplo de arquivo YOLO

Formato de cada linha:
- class x_center y_center width height
- Valores normalizados entre 0 e 1

Exemplo (1 caixa na imagem):

0 0.505000 0.660000 0.760000 0.180000

## 7) Checklist de qualidade antes de treinar

- Todas as imagens possuem label correspondente? (exceto negativas planejadas)
- Existem caixas fora da imagem? (valores < 0 ou > 1)
- Classes estao corretas (0 keyboard_area)
- Caixa keyboard_area esta realmente apenas na faixa de teclas
- Split por cena (nao misturar frames da mesma cena entre train e val/test)

## 8) Como validar visualmente

Use os scripts adicionados neste projeto:

1. Validar labels (integridade)
- python src/validate_yolo_labels.py --dataset datasets/piano_dataset --num-classes 1

2. Gerar previews com caixas desenhadas
- python src/preview_yolo_labels.py --images datasets/piano_dataset/images/train --labels datasets/piano_dataset/labels/train --out datasets/piano_dataset/previews/train --limit 200 --only-class 0

Observacao:
- O script mostra no resumo quantas linhas foram desenhadas como bbox e quantas como poligonos.
- Assim, voce identifica rapidamente se o export veio em deteccao retangular ou por pontos.

Atualizacao:
- O script preview_yolo_labels agora desenha tanto bbox (detecção) quanto labels por pontos/poligonos.
- Assim, voce consegue validar visualmente datasets exportados com formas geometricas rotacionadas.

Depois abra as imagens em previews e confira a qualidade da anotacao.

## 9) Como aproveitar seus datasets do Roboflow

Datasets informados por voce:
- piano3: classe 0 representa area de teclas
- my-first-project-9jrfe: classe keyboard representa area de teclas
- pianokeyboard: classes head e keyboard

Formato para download no Roboflow:
- YOLOv8 Segmentation (preferido para labels por pontos/poligonos)
- YOLOv8 Object Detection (aceito; sera convertido para poligono retangular)

Nao usar:
- YOLOv8 OBB

Estrategia de unificacao:
- mapear tudo que for teclado para classe unica 0 (keyboard_area)
- descartar classe head
- manter imagens sem teclado como negativas (opcional, recomendado)

Comando para gerar dataset unificado:
- python src/build_keyboard_only_dataset.py --out datasets/piano_dataset_seg --keep-negatives

Saida gerada:
- datasets/piano_dataset_seg/ (labels em formato de segmentacao)
- datasets/piano_dataset_seg.yaml

Opcional (forcando entradas manualmente):
- python src/build_keyboard_only_dataset.py --inputs datasets/baixados/My\ First\ Project.v1i.yolov8 datasets/baixados/piano3.v1i.yolov8 datasets/baixados/pianokeyboard.v7i.yolov8 --out datasets/piano_dataset_seg --keep-negatives
- python src/build_keyboard_only_dataset.py --inputs datasets/baixados/Find_keyboard_area.yolov8 --out datasets/piano_dataset_seg --keep-negatives
python src/build_keyboard_only_dataset.py --inputs "C:\Users\Murillo\Documents\Unity Projects\PianoARGame\tools\piano_vision_training\datasets\baixados\Find keyboard_area.v7-v3-area-keys-detector.yolov8" --out datasets/piano_dataset_seg --keep-negatives

## 10) Quando anotar teclas individuais?

Somente na fase 2.

Motivo:
- O custo de anotacao sobe muito.
- O ruido aumenta em camera real (motion blur, reflexo, perspectiva).
- O pipeline atual ja funciona melhor com deteccao global + refinamento no codigo.

Se for evoluir:
- Adicione classes white_key e black_key em um segundo modelo dedicado.

## 11) Erros comuns que derrubam performance

- Caixa keyboard_area muito larga incluindo corpo do piano
- Falta de diversidade de luz/angulo
- Data leakage (mesma cena em train e val)
- Dataset pequeno demais com muitos frames quase identicos
- Classe errada (computer keyboard misturado sem controle)
