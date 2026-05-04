# Plano Tecnico de Refatoracao de Performance - AR Piano (Unity)

## 1. Contexto

Objetivo deste plano:
- Confirmar o que mudou do arquivo monolitico para a versao em partial classes.
- Identificar mudancas que podem ter causado queda de desempenho e piora de usabilidade.
- Definir uma refatoracao tecnica faseada para recuperar FPS e fluidez visual (PC e Android), sem perder estabilidade funcional.

Escopo:
- Pipeline de camera, preprocess, inferencia, decode e desenho na tela Align/Game.
- Politica de backend (CPU/GPUCompute).
- Cadencia de inferencia versus cadencia de renderizacao.

Nao escopo:
- Refatoracao de gameplay MIDI que nao impacta a deteccao.

---

## 2. Conclusao Principal da Analise

Dividir em arquivos partial, por si so, nao deveria degradar performance de forma relevante.

A regressao percebida veio principalmente de comportamento de runtime do pipeline, nao da separacao fisica dos arquivos.

Pontos mais criticos:
1. Pipeline de deteccao continua sincrono na main thread.
2. Processamento pesado por frame de deteccao permanece em C# (GetPixels32 + rotacao/espelho + resize bilinear + alocacoes + readback).
3. Preview de fundo em Align/Game depende da frame corrigida (frameTexture) que so atualiza no tick de deteccao, gerando sensacao de video travado.
4. Logica de intervalo efetivo de deteccao atual pode inferir com frequencia maior que a versao antiga em cenario de queda de FPS.

---

## 3. O que mudou e pode ter impactado

## 3.1 Mudancas potencialmente regressivas

### A) Mudanca da logica de throttle de inferencia
Versao antiga (monolito):
- `detectInterval` era adaptado dinamicamente com memoria de estado:
  - FPS baixo: aumentava ate 12
  - FPS alto: reduzia ate 3

Versao atual:
- `GetEffectiveDetectInterval()` calcula intervalo instantaneo, porem no Align sem area detectada limita em 8.
- Em queda de FPS, o teto efetivo passa a 8 (em vez de 12 da logica antiga).

Impacto:
- Em situacoes de sobrecarga, a versao nova pode rodar mais inferencias do que a antiga, piorando FPS medio.

### B) Preview acoplado ao frame corrigido de deteccao
- Em Align/Game, o background prefere `frameTexture` corrigida.
- `frameTexture` e atualizada durante `DetectKeyboard`.
- Se inferencia ocorre em intervalo maior, o video visual atualiza menos vezes por segundo.

Impacto:
- Queda de usabilidade perceptiva (video "engasgando"), mesmo quando custo computacional melhora.

### C) Custo de preprocess/decode continua alto
- `webcam.GetPixels32()` + loop duplo de correcao por pixel.
- `ResizePixelsBilinear()` em C# puro.
- `new float[...]` por inferencia.
- `ReadbackAndClone()` no decode.

Impacto:
- Stall de main thread, GC pressure e frame pacing ruim.

## 3.2 Mudancas que nao parecem causa primaria

### A) Separacao em partial classes
- Semantica de execucao permanece igual em runtime.
- Nao e esperado overhead relevante apenas por organizar metodos em varios arquivos.

### B) Guard `webcam.didUpdateThisFrame`
- Esta mudanca tende a ajudar, pois evita reprocesar frame repetido.

---

## 4. Hipotese consolidada de regressao

A queda de desempenho e de usabilidade foi causada por combinacao de:
1. Pipeline pesado ainda sincronizado na main thread.
2. Menor agressividade de throttle sob baixa performance (teto 8 versus 12).
3. Acoplamento do preview ao ciclo de deteccao (causando choppiness visual).

---

## 5. Diretrizes de refatoracao

Principios:
1. Separar cadencia de preview da cadencia de inferencia.
2. Reduzir trabalho por inferencia (CPU e memoria).
3. Tornar degradacao adaptativa previsivel por plataforma.
4. Priorizar frame pacing (p95/p99 frame time) e nao so FPS medio.

---

## 6. Plano de Refatoracao por Fases

## Fase 0 - Baseline e Instrumentacao (obrigatoria)
Objetivo:
- Medir exatamente onde custa.

Acoes:
1. Adicionar profiler markers para etapas:
   - CaptureRead
   - CorrectRotateMirror
   - ResizeNormalize
   - ScheduleInference
   - DecodeReadback
   - DrawOverlay
2. Logar medias e p95 por etapa (janela de 300 frames).
3. Salvar baseline por plataforma/perfil:
   - PC 1080p@60
   - Android 720p@30

Entrega:
- Tabela com ms por etapa + FPS + p95 frame time.

## Fase 1 - Recuperar Paridade de Comportamento (baixo risco) (IMPLEMENTADA)
Objetivo:
- Eliminar regressao funcional de throttle/usabilidade sem reescrever tudo.

Acoes:
1. Restaurar politica de throttle adaptativo semelhante ao monolito:
   - Em FPS baixo permitir subir intervalo ate 12 (ou 15 em Android).
2. Separar preview do tick de deteccao:
   - Preview deve atualizar a cada frame de camera (ou quase), independente da inferencia.
3. Manter deteccao com ultimo frame valido (snapshot) quando necessario.

Entrega:
- UX de camera fluida no Align/Game.
- Queda de spikes comparado ao baseline.

## Fase 2 - Refatorar Pipeline de Deteccao (medio risco) (IMPLEMENTADA)
Objetivo:
- Reduzir custo estrutural de preprocess em C#.

Acoes:
1. Remover alocacoes por inferencia:
   - Reusar buffers de input e resized.
   - Reusar estruturas de decode.
2. Evitar recorrecoes full-frame desnecessarias:
   - Aplicar transformacoes de orientacao no caminho grafico quando possivel.
3. Reduzir custo de decode:
   - Limitar candidatos (top-k / threshold antecipado).
   - NMS com estrutura sem sort completo quando aplicavel.

Entrega:
- Menos GC spikes.
- Menor custo medio por inferencia.

## Fase 2.5 - Pipeline Concorrente (3 workers logicos) (IMPLEMENTADA)
Objetivo:
- Garantir fluidez do video principal e das notas desacoplando inferencia pesada do loop de render.

Arquitetura alvo (Android-first, CPU-first):
1. Worker Camera (main thread Unity):
   - Captura frame da camera.
   - Atualiza preview e UI sempre.
   - Publica snapshot imutavel para deteccao (double buffer + versionamento).
2. Worker Deteccao (thread dedicada CPU):
   - Consome ultimo snapshot disponivel (dropa antigos).
   - Executa preprocess + inferencia + decode.
   - Publica ultimo resultado valido atomico (bbox + confidence + timestamp).
3. Worker Jogo (main thread Unity, etapa logica separada):
   - Desenha notas MIDI usando o ultimo resultado publicado pela deteccao.
   - Nunca bloqueia aguardando inferencia.

Regras de sincronizacao:
1. Unity API fica na main thread (camera texture, GUI, draw, Texture2D, WebCamTexture).
2. Thread de deteccao processa apenas buffers de dados puros (Color32[]/float[]/structs), sem chamadas Unity API.
3. Comunicacao lock-free com troca por indice/version e `volatile`/`Interlocked`.
4. Backpressure: fila tamanho 1 (latest-wins) para impedir backlog e latencia crescente.

Acoes:
1. Criar `FrameSnapshot` (pixels + width + height + frameId + timestamp).
2. Implementar double buffer de entrada para deteccao.
3. Implementar `DetectionResultShared` atomico para leitura da main thread.
4. Definir timeout watchdog para reiniciar worker de deteccao em erro.
5. Medir latencia capture->detect e jitter de render apos desacoplamento.

Entrega:
- Video principal fluido independente do tempo de inferencia.
- Notas MIDI estaveis usando a ultima deteccao valida.
- Reducao de travadas perceptiveis em cenario sem piano na imagem.

## Fase 3 - Caminho GPU/Zero-Copy para Preprocess (alto impacto)
Objetivo:
- Tirar resize/normalizacao da CPU gerenciada.

Acoes:
1. Preprocess por RenderTexture/Compute (quando backend suportar).
2. Evitar round-trip CPU-GPU desnecessario.
3. Minimizar uso de `ReadbackAndClone`; reduzir sincronizacoes bloqueantes.

Entrega:
- Queda relevante do tempo de preprocess/decode no PC.
- Melhor escalabilidade para input maior.

## Fase 4 - Politica de Backend por Plataforma
Objetivo:
- Usar GPU quando realmente vantajoso, com fallback confiavel.

Acoes:
1. Heuristica de selecao inicial:
   - PC: tentar GPUCompute, fallback CPU se p95 piorar.
   - Android: iniciar em CPU otimizada; habilitar GPUCompute em allowlist de aparelhos.
2. Warm-up e benchmark curto de 2-3 segundos para decidir backend no boot.
3. Persistir backend escolhido por device/modelo.

Entrega:
- Selecao automatica com criterio de frame time real.

## Fase 5 - Perfis de Qualidade por Estado
Objetivo:
- Ajustar custo de camera/inferencia conforme contexto.

Acoes:
1. Perfis por estado:
   - SongSelect/MainMenu: preview simples.
   - Align: detecao mais frequente, resolucao moderada.
   - Game: tracking estavel com inferencia menos frequente.
2. Perfis por plataforma:
   - PC default: 1280x720@30 no Align (subir opcional).
   - Android default: 960x540@30 ou 640x480@30.

Entrega:
- Melhor equilibrio entre qualidade e fluidez.

---

## 7. Criterios de Aceite

## 7.1 Performance
1. PC Align:
   - FPS medio >= 24
   - p95 frame time <= 55 ms
2. Android Align:
   - FPS medio >= 18 (meta inicial), alvo futuro >= 24
   - p95 frame time <= 70 ms

## 7.2 Usabilidade
1. Preview visual sem efeito de "travamento" perceptivel quando detectInterval aumenta.
2. Overlay de deteccao continua alinhado.

## 7.3 Estabilidade
1. Sem alocacoes criticas por inferencia (ou reducao comprovada >80%).
2. Sem regressao de fluxo (MainMenu -> SongSelect -> Align -> Game -> End).

---

## 8. Riscos e Mitigacoes

Risco 1: Melhorar FPS mas piorar alinhamento visual.
- Mitigacao: testes A/B com dumps de overlay e comparacao de bbox.

Risco 2: GPUCompute pior em alguns Android.
- Mitigacao: fallback automatico por benchmark curto e allowlist.

Risco 3: Refatoracao grande quebrar fluxo de jogo.
- Mitigacao: fases pequenas, PRs incrementais e flags de runtime.

Risco 4: Deadlock/race condition ao introduzir worker de deteccao.
- Mitigacao: protocolo latest-wins sem lock pesado, testes de stress e watchdog de thread.

Risco 5: Tentar usar API Unity fora da main thread.
- Mitigacao: regra explicita de isolamento (thread secundaria apenas dados puros).

---

## 9. Ordem Recomendada de PRs

PR-1: Instrumentacao + baseline.
PR-2: Restaurar throttle adaptativo + desacoplar preview da inferencia.
PR-3: Reuso de buffers + reduzir alocacoes no preprocess/decode.
PR-4: Pipeline concorrente (camera/main + worker deteccao + jogo/main).
PR-5: Perfis de qualidade por estado/plataforma + ajuste de latencia/jitter.
PR-6: (Opcional) caminho GPU preprocess e reducao de readback bloqueante.
PR-7: (Opcional) auto-selecao de backend por dispositivo.

---

## 10. Resposta direta a pergunta

Sim: existe alteracao de comportamento que pode ter causado perda de desempenho.

Mais relevante:
1. Mudanca da politica de intervalo de inferencia (perdeu agressividade sob baixa FPS, com teto efetivo menor para aliviar carga).
2. Preview em Align/Game acoplado a `frameTexture` atualizada no tick de deteccao, causando pior usabilidade perceptiva.

A separacao em arquivos (partial class) nao e a causa primaria.

---

## 11. Metricas Reais Coletadas - Android (Threading ON, CPU backend, OpenGLES3)

Fonte:
- Logcat da execucao em dispositivo real em 2026-05-03.
- Marcadores: `[ArPianoGame][AndroidThreadDiag]`.

Configuracao observada:
1. `threading=True`, `backend=CPU (0)`, `gfxApi=OpenGLES3`, `detectInterval=5`.
2. Worker ativo e estavel (`threadReady=True`, `initFailed=False`, `workerThreadId` valido).
3. Sem erro de crash de worker nesta captura.

Tempos por etapa (modo atual):
1. Preprocess no worker (`workerPreMs`): aproximadamente 24 ms a 59 ms.
2. Inference + decode na main thread (`mainInferMs`): aproximadamente 442 ms a 582 ms.
3. Intervalo dinamico requerido (`reqInferInterval`): aproximadamente 1.33 s a 1.75 s.
4. Idade da ultima inferencia (`inferAge`) durante ciclo estavel: aproximadamente 0.56 s a 2.14 s.

Indicadores de sistema durante o teste:
1. `renderFpsEma` em torno de 60 fps.
2. `camFps` variando aproximadamente de 32 fps a 60 fps.
3. `pendingPre` variando entre 2 e 4 (pipeline latest-wins com backlog controlado).

Conclusao tecnica desta coleta:
1. O pipeline concorrente estabilizou fluidez geral do video.
2. Ainda ocorre travada perceptivel a cada inferencia, causada pelo custo alto de inferencia/decode na main thread (pico de ~0.45 s a ~0.58 s por execucao).
3. Gargalo dominante atual permanece em inferencia/decode na main thread, nao no preprocess do worker.

## 12. O que esta dentro de `mainInferMs`

`mainInferMs` representa o tempo da etapa mais cara da pipeline na main thread, medido no bloco:
1. Montagem de `Tensor<float>` de entrada a partir do buffer preprocessado.
2. `worker.Schedule(inputTensor)`.
3. `PickDetectionOutput(worker)`.
4. `DecodeBest(...)` (inclui leitura do output e NMS).
5. Conversao final de bbox (`ConvertDetectionToTopLeft`).

Observacao importante:
1. Mesmo com preprocess em thread separada, esta etapa ainda bloqueia a main thread durante a inferencia/decode.
2. E exatamente esse bloqueio que gera a travada perceptivel no video a cada execucao.

## 13. O que esta dentro de `reqInferInterval`

`reqInferInterval` e o intervalo minimo dinamico exigido antes de iniciar nova inferencia no Android.

Regra atual:
1. Parte de um `baseInterval` configuravel (`androidMinInferenceIntervalSeconds`, atualmente 1.2s).
2. Calcula `costSeconds = mainInferMs / 1000`.
3. Aplica multiplicador dinamico:
   - `x3` se custo >= 0.35s.
   - `x2` caso contrario.
4. Faz clamp com limites configuraveis (`androidMinInferenceIntervalSeconds` e `androidMaxInferenceIntervalSeconds`, com teto efetivo minimo de 2s).

Consequencia pratica com os logs atuais:
1. Com `mainInferMs` tipicamente entre ~0.44s e ~0.58s, o `reqInferInterval` naturalmente sobe para faixa de ~1.33s a ~1.75s.
2. Isso evita saturacao total da main thread, mas aumenta latencia de atualizacao de deteccao.

## 14. Melhorias propostas por etapa para reduzir gargalo

### Etapa A - Capture/Correct/Preprocess
1. Reduzir resolucao real de captura para inferencia no Android (ex.: 960x540 ou 640x480), mantendo preview separado quando necessario.
2. Evitar rotacao/espelho full-frame em CPU quando nao muda orientacao; reutilizar transformacao ate evento de rotacao.
3. Trocar resize bilinear C# por caminho nativo/Burst/Jobs para reduzir `workerPreMs` e custo de memoria.

### Etapa B - Inference (maior impacto esperado)
1. Reduzir input do modelo (ex.: de 640 para 416/320) para cortar custo de inferencia de forma nao linear.
2. Testar variante ONNX mais leve/quantizada (FP16/INT8 quando viavel), mantendo acuracia minima aceitavel.
3. Validar backend alternativo no Android somente se manter preview estavel (sem regressao de camera).

### Etapa C - Decode/Readback
1. Aplicar threshold antecipado e top-k antes de NMS para reduzir volume de candidatos.
2. Limitar NMS a candidatos ja filtrados por classe/score alto.
3. Investigar possibilidade de reduzir custo de `ReadbackAndClone`/materializacao de output (evitar copias grandes por inferencia).

### Etapa D - Scheduling / Cadencia
1. Tornar `detectInterval` e `reqInferInterval` dependentes de estado (Align vs Game), com prioridade para estabilidade visual.
2. Disparar inferencia por tempo (timer) em vez de somente por modulo de frame (`frameCount % interval`) para reduzir jitter.
3. Manter politica latest-wins com fila de tamanho 1 e descarte explicito de snapshots antigos.

### Etapa E - UX de Preview
1. Desacoplar totalmente a taxa de preview da taxa de inferencia (preview sempre em fps alto da camera).
2. Aplicar overlay da ultima deteccao valida sem bloquear desenho da camera.

## 15. Prioridade de implementacao recomendada (curto prazo)

1. Reduzir `modelInputW/H` no Android e medir novo `mainInferMs`.
2. Otimizar decode (threshold antecipado + top-k + NMS reduzido).
3. Ajustar scheduler por tempo e perfis Align/Game.
4. So depois reavaliar mudancas de backend/API grafica.

Meta objetiva:
1. Trazer `mainInferMs` para faixa sustentavel (< 150-220 ms) para eliminar travada perceptivel por inferencia.

## 16. Evidencia mais recente (AndroidInferBreakdown)

Resumo dos logs de 2026-05-03 (OpenGLES3, CPU backend):
1. `input=640x640` e `staticInput=True` (modelo com shape fixo, sem reduzir input por runtime).
2. `totalMs` tipicamente entre ~441 ms e ~574 ms.
3. `scheduleMs` tipicamente entre ~2 ms e ~6 ms (pico isolado ~20 ms no primeiro ciclo).
4. `pickMs` ~0.01 ms (irrelevante no custo total).
5. `decodeMs` tipicamente entre ~435 ms e ~572 ms (gargalo dominante).

Conclusao objetiva:
1. O problema principal nao esta no `worker.Schedule`.
2. O gargalo principal esta no bloco de decode/readback/materializacao na main thread.

Implicacao para `reqInferInterval`:
1. Remover `reqInferInterval` agora (rodar inferencia imediatamente em sequencia) tende a piorar a travada visual, porque aumentaria a frequencia de um bloco que ainda custa ~0.45-0.57s na main thread.
2. O caminho correto e reduzir primeiro o custo de `decodeMs`, depois reavaliar reducao/agressividade do intervalo.
