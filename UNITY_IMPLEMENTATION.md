# AR Piano Game em Unity (Documentacao Completa)

## 1. Objetivo

Este documento descreve a implementacao completa do AR Piano Game em Unity/C#, com paridade funcional em relacao ao prototipo Python em `tools/piano_ar_game/ar_piano_game.py`.

Fluxo funcional:
1. Menu com selecao de MIDI
2. Align com deteccao ONNX da area do teclado
3. Gameplay com notas MIDI em queda
4. Tela final

Tambem cobre:
- funcionamento em PC e Android
- ativacao de HMD no Android
- pipeline de preprocess/inferencia/decode
- troubleshooting real aplicado no projeto
- correcao de inversao de imagem/eixos no Unity

## 2. Estrutura Ativa no Projeto

Implementacao principal:
- `Assets/Scripts/ARPianoParity/ArPianoParityGame.cs`
- `Assets/Scripts/ARPianoParity/ArPianoParityBootstrap.cs`

Cena principal:
- `Assets/Scenes/Gameplay.unity`

Modelo ONNX:
- `Assets/AIModels/piano_SGD.onnx`
- `Assets/Resources/AIModels/piano_SGD.onnx` (fallback para carga via Resources)

Documentacao Python:
- `tools/piano_ar_game/README.md`

## 3. Arquitetura do Runtime Unity

### 3.1 Maquina de estados

`ArPianoParityGame` usa estes estados:
- `Menu`
- `Align`
- `Game`
- `End`

### 3.2 Bootstrap automatico

`ArPianoParityBootstrap` garante que o objeto `AR Piano Parity Game` exista em runtime, evitando dependencia de setup manual da cena.

### 3.3 Loop principal

- `Update()`:
  - calcula FPS
  - processa atalhos de teclado
  - atualiza tracker durante `Align/Game`

- `OnGUI()`:
  - desenha frame de camera como fundo
  - desenha HUD e UI por estado
  - desenha retangulos/overlay de teclado e notas

## 4. Pipeline de Camera e Inferencia

### 4.1 Captura de camera

- Usa `WebCamTexture`
- Parametros solicitados: largura/altura/FPS (configuraveis)
- Leitura de pixels por `GetPixels32()`

### 4.2 Correcao de frame da camera

Antes da inferencia, o frame passa por:
- correcao de mirror vertical (`videoVerticallyMirrored`)
- correcao de rotacao (`videoRotationAngle`)

Resultado: frame corrigido em `correctedFramePixels` + `frameTexture`.

### 4.3 Preprocess para ONNX (paridade com Python)

Implementado para reproduzir o Python:
- resize bilinear para `inputW x inputH`
- normalizacao `[0..255] -> [0..1]`
- formato NCHW (1, 3, H, W)

Detalhe importante:
- preprocess foi migrado para operar em `Color32[]` bruto, evitando diferencas de conversao via `Texture2D.GetPixels()` / `Color`.

### 4.4 Modelo e backend

- carregamento via `ModelAsset` (`Unity.InferenceEngine`)
- `Worker` com backend configuravel (`CPU` por padrao)
- selecao de output de deteccao por shape (ex.: `[1, 37, 8400]`)

### 4.5 Decode

- suporte a layouts tipo YOLO (`features_first` / `candidates_first`)
- score por melhor classe
- threshold de confianca
- conversao `cx, cy, w, h -> x1, y1, x2, y2`
- NMS por IoU

## 5. MIDI e Gameplay

### 5.1 Leitura MIDI

- DryWetMIDI
- extracao de eventos de nota com start/end/velocity
- heuristica de mao:
  - por canais (quando disponivel)
  - fallback por pitch

### 5.2 Desenho das notas

- mapeamento linear `A0(21) -> C8(108)` no eixo X
- notas pretas/brancas com largura diferente
- cores por mao
- linha de strike
- barra de progresso

## 6. Android e HMD

- fluxo igual ao desktop
- MIDIs via `StreamingAssets/MIDI`
- opcao de entrar em HMD ao iniciar gameplay (`enableHmdModeOnGameStart`)
- inicializacao via XR Management em runtime

## 7. Problema Real de Inversao no Unity e Solucao

Esta foi a parte critica do projeto.

### 7.1 Sintoma observado

- deteccao com score alto nos dumps
- retangulo desenhado fora da area correta
- em alguns momentos: X parecia certo, Y parecia invertido
- em outros momentos: leve offset visual no desenho

### 7.2 Causa raiz

No Unity, coexistem sistemas de coordenadas/pixels com convencoes diferentes:
- arrays de pixel e texturas podem operar com origem bottom-left
- `OnGUI` usa coordenadas de tela com origem top-left
- frame de camera pode ser rotacionado/espelhado pelo device
- desenho final ocupa `Screen.width x Screen.height`, enquanto deteccao ocorre em resolucao do frame da camera

Se qualquer conversao faltar ou for duplicada, o overlay desloca/inverte.

### 7.3 Correcoes aplicadas

1. Correcao de orientacao da camera antes da inferencia
- aplicacao de mirror/rotacao no buffer de camera

2. Preprocess em pixels brutos
- resize bilinear em `Color32[]`
- normalizacao direta `/255f`

3. Conversao explicita de origem no desenho
- deteccao decodificada para referencia top-left quando necessario
- mapeamento de retangulo do frame para a tela

4. Flip de Y no mapeamento para `OnGUI`
- ajuste no mapeamento vertical para alinhar frame-space e screen-space

Formula aplicada no mapeamento:
- `mappedX = frameRect.x * scaleX`
- `mappedY = (frameHeight - frameRect.yMax) * scaleY`

Com isso, o eixo Y desenhado em tela passa a coincidir com o frame visual.

## 8. Deteccao e Debug com Dumps

Foi adicionado dump de inferencia no Unity para comparacao 1:1 com Python.

### 8.1 Pasta de dumps Unity

- `Application.persistentDataPath/DebugDumps/Unity`

Arquivos por dump:
- `unity_dump_000_frame.png`
- `unity_dump_000_input.png`
- `unity_dump_000_overlay.png`
- `unity_dump_000_stats.txt`

### 8.2 Conteudo do stats

- resolucao de frame
- input do modelo
- thresholds
- melhor deteccao
- estatisticas do tensor de entrada (min/max/mean)
- shape/layout dos outputs
- top scores

### 8.3 Comparacao com Python

Python tambem gera dumps equivalentes em:
- `tools/piano_ar_game/tools/piano_ar_game/debug_dumps/python`

Essa comparacao foi essencial para identificar:
- preprocess incorreto
- eixos invertidos no desenho
- diferenca entre coordenadas de frame e coordenadas de tela

## 9. Como Executar (Unity)

1. Abrir projeto Unity
2. Confirmar cena principal em Build Settings:
   - `Assets/Scenes/Gameplay.unity`
3. Confirmar modelo ONNX no inspector (ou fallback por Resources)
4. Ajustar pasta MIDI desktop em `Midi Directory Desktop`
5. Play

Fluxo:
- escolher musica
- Jogar
- alinhar teclado
- iniciar jogo

## 10. Como Executar (Python de referencia)

No diretorio `tools/piano_ar_game`:

```powershell
python ar_piano_game.py --model "C:\Users\Murillo\Documents\Unity Projects\PianoARGame\Assets\AIModels\piano_SGD.onnx" --midi-dir "C:\Users\Murillo\Music\MIDI" --camera 0 --width 1920 --height 1080 --fps 60
```

## 11. Troubleshooting Rapido

### 11.1 Detecta mas desenha fora do lugar

- conferir se o frame usado para desenhar e o mesmo referencial usado no decode
- conferir mapeamento frame -> tela
- conferir flip vertical no eixo Y

### 11.2 Score baixo no Unity vs Python

- comparar dumps (`input_tensor`, `top_scores`, `output shape`)
- confirmar input size real (`640x640`)
- conferir preprocess em pixel bruto

### 11.3 Camera ruim/instavel

- testar iluminacao
- reduzir detect interval para maior qualidade visual
- garantir USB/camera sem gargalo

## 12. Arquivos de Referencia

- Unity runtime principal:
  - `Assets/Scripts/ARPianoParity/ArPianoParityGame.cs`
- Unity bootstrap:
  - `Assets/Scripts/ARPianoParity/ArPianoParityBootstrap.cs`
- Cena:
  - `Assets/Scenes/Gameplay.unity`
- Python base:
  - `tools/piano_ar_game/ar_piano_game.py`
- Documentacao Python:
  - `tools/piano_ar_game/README.md`
- Este documento:
  - `tools/piano_ar_game/UNITY_IMPLEMENTATION.md`

## 13. Estado Atual

- paridade funcional com Python alcançada para o fluxo principal
- deteccao ONNX estabilizada
- pipeline de orientacao/inversao de imagem documentado e corrigido
- overlay em tela ajustado para o sistema de coordenadas do Unity
