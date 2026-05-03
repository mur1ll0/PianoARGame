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

## Fase 1 - Recuperar Paridade de Comportamento (baixo risco)
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

## Fase 2 - Refatorar Pipeline de Deteccao (medio risco)
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

---

## 9. Ordem Recomendada de PRs

PR-1: Instrumentacao + baseline.
PR-2: Restaurar throttle adaptativo + desacoplar preview da inferencia.
PR-3: Reuso de buffers + reduzir alocacoes no preprocess/decode.
PR-4: Caminho GPU preprocess e reducao de readback bloqueante.
PR-5: Auto-selecao de backend e perfis por plataforma/estado.

---

## 10. Resposta direta a pergunta

Sim: existe alteracao de comportamento que pode ter causado perda de desempenho.

Mais relevante:
1. Mudanca da politica de intervalo de inferencia (perdeu agressividade sob baixa FPS, com teto efetivo menor para aliviar carga).
2. Preview em Align/Game acoplado a `frameTexture` atualizada no tick de deteccao, causando pior usabilidade perceptiva.

A separacao em arquivos (partial class) nao e a causa primaria.
