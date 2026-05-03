# AR Piano Game em Unity (Documentacao Completa)

## 1. Objetivo

Este documento descreve a implementacao completa do AR Piano Game em Unity/C#.

Fluxo funcional:
1. Menu principal (Selecionar musica / Configuracoes / Sair)
2. Tela de selecao de MIDI
3. Align com deteccao ONNX da area do teclado
4. Gameplay com notas MIDI em queda
5. Tela final

Tambem cobre:
- funcionamento em PC e Android
- ativacao de HMD no Android
- pipeline de preprocess/inferencia/decode
- troubleshooting real aplicado no projeto
- correcao de inversao de imagem/eixos no Unity

## 2. Estrutura Ativa no Projeto

### Classe principal (partial class)

`ARPianoGame` foi dividida em arquivos partial para facilitar manutencao. Todos os arquivos declaram `public sealed partial class ARPianoGame : MonoBehaviour` no namespace `PianoARGame`.

| Arquivo | Responsabilidade |
|---------|-----------------|
| `ARPianoGame.cs` | Nucleo: enums, structs, campos, `Awake`, `OnDestroy`, `Update`, `BootstrapCameraStartup` |
| `ARPianoGame.UI.cs` | Renderizacao IMGUI: `OnGUI`, todos os metodos `Draw*`, helpers de estilo e escala |
| `ARPianoGame.Camera.cs` | Gerenciamento de camera: iniciar, selecionar dispositivo, modos de resolucao |
| `ARPianoGame.Detection.cs` | Pipeline de inferencia ONNX: preprocess, inferencia, decode de bbox |
| `ARPianoGame.Midi.cs` | Descoberta e parsing de arquivos MIDI |
| `ARPianoGame.Gameplay.cs` | Transicoes de estado de jogo e calculos de notas |
| `ARPianoGame.Diagnostics.cs` | Dump de artefatos de debug (imagens, estatisticas) |
| `ARPianoGame.HMD.cs` | Modo XR/HMD, orientacao Android, helpers de input |

Arquivos de suporte:
- `Assets/Scripts/ARPiano/ARPianoBootstrap.cs`

Arquitetura complementar (separacao por contexto):
- UI
  - `Assets/Scripts/ARPiano/UI/UiScaleCalculator.cs`
- Services
  - `Assets/Scripts/ARPiano/Services/MidiRepository.cs`

Cena principal:
- `Assets/Scenes/Gameplay.unity`

Modelo ONNX:
- `Assets/AIModels/piano_SGD.onnx`
- `Assets/Resources/AIModels/piano_SGD.onnx` (fallback para carga via Resources)


## 3. Arquitetura do Runtime Unity

### 3.1 Maquina de estados

`ARPianoGame` usa estes estados:
- `MainMenu`
- `SongSelect`
- `Align`
- `Game`
- `End`

### 3.2 Bootstrap automatico

`ARPianoBootstrap` garante que o objeto `AR Piano Game` exista em runtime, evitando dependencia de setup manual da cena.

### 3.3 Loop principal

- `Update()`:
  - calcula FPS
  - processa atalhos de teclado
  - atualiza tracker durante `Align/Game`

- `OnGUI()`:
  - desenha frame de camera como fundo
  - desenha HUD e UI por estado com escala responsiva para mobile
  - desenha retangulos/overlay de teclado e notas
  - desenha botao global `MENU` no canto para retorno imediato ao menu principal

### 3.4 UI mobile-first

Foi aplicado ajuste de escala dinamica da interface para evitar texto/botoes pequenos no celular:
- considera menor dimensao da tela
- considera DPI em plataformas mobile
- ajusta fontes e dimensoes de elementos por `uiScale`

Principais telas:
- Main menu com 3 botoes grandes:
  - Selecionar musica
  - Configuracoes
  - Sair
- SongSelect dedicada apenas para lista de MIDIs e acoes de jogo
- Configuracoes em overlay scrollavel (acessivel no menu principal pelo botao `Configuracoes`)

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

### 4.6 Configuracoes em runtime

A overlay de configuracoes inclui abas:
- MIDI
  - Android: pasta fixa do app (`Application.persistentDataPath/MIDI`)
  - Android: botao `Importar MIDI` via seletor do sistema
  - Android: importacao de um ou multiplos arquivos por vez
  - Desktop/PC: caminho customizado editavel
  - recarregar lista de arquivos
- Camera
  - selecao de dispositivo
  - selecao de modo (resolucao/FPS)
  - aplicar camera
- Deteccao
  - `confThreshold`
  - `iouThreshold`
  - backend (CPU/GPUCompute)
  - secao avancada (detect interval, estabilidade, classes, fallback input)
- Gameplay
  - velocidade, countdown, travel time
  - auto-HMD
- Diagnostico
  - dumps de inferencia e limite

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
- MIDIs no Android via pasta interna do app: `Application.persistentDataPath/MIDI`
- importacao de MIDIs externos via seletor nativo do Android (SAF)
- opcao de entrar em HMD ao iniciar gameplay (`enableHmdModeOnGameStart`)
- inicializacao via XR Management em runtime

### 6.1 Correcao de descoberta de MIDI no Android

A busca de MIDIs no Android foi simplificada para estabilidade com scoped storage:
- raiz unica: `Application.persistentDataPath/MIDI`
- criacao automatica da pasta se nao existir
- varredura recursiva segura dentro da pasta do app

### 6.2 Importacao MIDI no Android (sem manifest custom)

Fluxo implementado:
- usuario toca `Importar MIDI`
- abre seletor nativo (ACTION_OPEN_DOCUMENT)
- usuario pode selecionar um ou mais arquivos MIDI
- arquivos sao copiados para `Application.persistentDataPath/MIDI`
- lista de MIDIs e recarregada automaticamente

Detalhes de implementacao:
- sem `AndroidManifest.xml` custom no projeto
- bridge Java acoplada a Activity atual da Unity via Fragment
- callback de retorno para Unity via `UnitySendMessage`

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

## 10. Troubleshooting Rapido

### 10.1 Detecta mas desenha fora do lugar

- conferir se o frame usado para desenhar e o mesmo referencial usado no decode
- conferir mapeamento frame -> tela
- conferir flip vertical no eixo Y

### 10.2 Score baixo no Unity vs Python

- comparar dumps (`input_tensor`, `top_scores`, `output shape`)
- confirmar input size real (`640x640`)
- conferir preprocess em pixel bruto

### 10.3 Camera ruim/instavel

- testar iluminacao
- reduzir detect interval para maior qualidade visual
- garantir USB/camera sem gargalo

## 11. Arquivos de Referencia

- Unity runtime principal:
  - `Assets/Scripts/ARPiano/ARPianoGame.cs`
- Unity bootstrap:
  - `Assets/Scripts/ARPiano/ARPianoBootstrap.cs`
- Servico de repositorio MIDI:
  - `Assets/Scripts/ARPiano/Services/MidiRepository.cs`
- Utilitario de escala da UI:
  - `Assets/Scripts/ARPiano/UI/UiScaleCalculator.cs`
- Cena:
  - `Assets/Scenes/Gameplay.unity`
- Este documento:
  - `tools/piano_ar_game/UNITY_IMPLEMENTATION.md`

## 12. Estado Atual

- paridade funcional com Python alcançada para o fluxo principal
- deteccao ONNX estabilizada
- pipeline de orientacao/inversao de imagem documentado e corrigido
- overlay em tela ajustado para o sistema de coordenadas do Unity
