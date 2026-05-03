# AR Piano Game (Python 3.13)

Projeto de jogo em realidade aumentada para estudo de piano usando:
- deteccao da area do teclado real com modelo ONNX
- leitura de arquivo MIDI
- renderizacao de notas em overlay na webcam, indo em direcao a tecla correta

O fluxo foi desenhado para ser simples:
1. selecionar a musica
2. clicar em Jogar
3. alinhar e estabilizar deteccao do teclado
4. iniciar e tocar acompanhando as notas

## Nota importante sobre ONNX

- O jogo detecta automaticamente o tamanho de entrada esperado pelo modelo ONNX.
- Exemplo: se o modelo foi exportado para 640x640, o jogo usa 640x640 automaticamente.
- Isso evita erro de dimensao em runtime (INVALID_ARGUMENT de input shape).

## Arquivos principais

- ar_piano_game.py: jogo completo (menu, deteccao/tracking, gameplay AR, HUD FPS)
- live_camera_detect.py: seu teste original de deteccao ao vivo
- piano_SGD.onnx: modelo ONNX para detectar area do teclado
- requirements.txt: dependencias Python

## Requisitos

- Windows
- Python 3.13
- Webcam (EMEET recomendada)
- Driver GPU/CUDA (opcional, mas recomendado para maior FPS)

## Instalacao

No diretorio do projeto:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
pip install -r requirements.txt
```

Se nao quiser/nao puder usar GPU, troque o pacote:

```powershell
pip uninstall -y onnxruntime-gpu
pip install onnxruntime
```

## Executar

Com configuracao padrao do seu caso:

```powershell
python ar_piano_game.py --model piano_SGD.onnx --midi-dir "C:\Users\Murillo\Music\MIDI" --camera 0 --width 1920 --height 1080 --fps 60
```

## Interface e uso

### 1) Menu de musica

- Lista todos os .mid e .midi da pasta definida em --midi-dir
- Use a roda do mouse para rolar a lista para cima e para baixo
- Fallback por teclado no menu: seta para cima/baixo, J/K, PageUp/PageDown
- Clique na musica desejada
- Clique em Jogar
- Botao Recarregar MIDIs atualiza a lista sem reiniciar

### 2) Alinhamento e tracking

- Mostra uma caixa guia para centralizar o teclado real
- Executa deteccao da area do teclado com o modelo ONNX
- Aplica estabilizacao temporal (tracking por redeteccao periodica + suavizacao)
- Exibe progresso de estabilidade (Tracking estavel)
- Quando estabilizar, o botao Iniciar jogo e liberado
- Permite ajustar a velocidade da musica antes de iniciar (botoes - e +)

### 3) Gameplay AR

- As notas da musica aparecem em queda em direcao ao teclado detectado
- Notas finas representam teclas pretas e notas grossas representam teclas brancas
- Mao direita em verde e mao esquerda em azul (com deteccao por canais MIDI quando disponivel, com fallback por altura da nota)
- Linha de impacto marca o instante da tecla
- Numero da nota MIDI aparece no momento exato de toque
- Barra de progresso mostra o andamento da musica
- As primeiras notas passam a nascer acima da area do teclado para melhorar leitura de aproximacao

### 4) Fim

- Tela de musica finalizada
- Opcoes: voltar ao menu ou jogar novamente

## Controles

- Mouse: UI (selecionar musica, clicar botoes)
- Tecla Q ou ESC: sair
- Tecla R: voltar ao menu

## Velocidade da musica e preparo

- Antes de iniciar o jogo (tela de alinhamento), ajuste a velocidade da musica nos botoes - e +.
- Faixa de velocidade: 0.5x ate 2.0x.
- Velocidade maior: notas chegam mais rapido.
- Velocidade menor: notas chegam mais devagar.
- Ao clicar em Iniciar jogo, ha contagem regressiva de 3 segundos antes das notas comecarem a ser desenhadas.

## FPS real e desempenho

O HUD exibe:
- Render FPS: FPS real da imagem renderizada (tempo por frame do loop)
- Camera: resolucao/FPS reais reportados pela webcam

### Meta de 30+ FPS

O sistema inclui otimizacoes para manter ~30 FPS ou mais:
- inferencia de deteccao feita a cada N frames (detect-interval)
- ajuste dinamico do detect-interval conforme FPS
- uso automatico do input esperado pelo ONNX (com fallback configuravel por --size)
- buffer da camera reduzido (CAP_PROP_BUFFERSIZE=1)

## Parametros importantes

- --model: caminho do ONNX
- --midi-dir: pasta com MIDIs
- --camera: indice da webcam
- --width --height --fps: configuracao solicitada da camera
- --size: fallback de input para inferencia quando o modelo ONNX tiver shape dinamico
- --conf --iou: limiares da deteccao
- --detect-interval: deteccao a cada N frames
- --stable-hits: quantidade de deteccoes estaveis para liberar inicio
- --travel-time: tempo de queda das notas ate a tecla
- --song-speed: velocidade inicial da musica (0.5 a 2.0)
- --countdown: contagem regressiva antes de desenhar notas (padrao: 3.0s)
- --auto-start-first: inicia direto no modo align com o primeiro MIDI (teste sem clique)

Exemplo para buscar mais desempenho:

```powershell
python ar_piano_game.py --model piano_SGD.onnx --detect-interval 7 --width 1920 --height 1080 --fps 60
```

Exemplo de autoteste (sem clicar no menu):

```powershell
python ar_piano_game.py --model piano_SGD.onnx --midi-dir "C:\Users\Murillo\Music\MIDI" --camera 0 --width 1920 --height 1080 --fps 60 --auto-start-first
```

## Como o sistema funciona (arquitetura)

1. Captura frame da webcam
2. Atualiza FPS real
3. Dependendo do estado:
   - menu: renderiza lista e botoes
   - align: roda detector ONNX periodicamente e estabiliza area
   - game: renderiza teclado e notas MIDI em funcao do tempo
   - end: renderiza tela final
4. Exibe frame no OpenCV

## Observacoes tecnicas

- O mapeamento de nota MIDI para posicao X e linear entre A0(21) e C8(108)
- O projeto atual e focado em orientacao visual para treino de timing
- Nao ha captura de acerto da tecla fisica neste momento (nao ha hand/key press tracking)

## Troubleshooting

### Nao detecta teclado

- Ajuste iluminacao
- Posicione o teclado inteiro na area visivel
- Reduza --conf (ex.: 0.25)
- Se o modelo for de shape dinamico, ajuste --size (ex.: 640)

### FPS baixo

- Use GPU (onnxruntime-gpu)
- Em modelos de shape dinamico, reduza --size (ex.: 416 ou 512)
- Aumente --detect-interval (ex.: 7 a 10)
- Garanta que a webcam esta em USB com banda suficiente

### Webcam nao abre

- Troque --camera (0, 1, 2...)
- Feche apps que ja usam a webcam

## Validacao feita

- Validacao de sintaxe: python -m py_compile ar_piano_game.py
- Sem erros de analise estatica reportados para ar_piano_game.py
- Validacao de input ONNX automatico em runtime (ex.: 640x640)

## Proximas evolucoes sugeridas

- Detecao de maos/teclas pressionadas para pontuacao real
- Mapeamento mais fiel de teclas brancas/pretas no teclado detectado
- Ajuste automatico de latencia de audio e calibracao por usuario