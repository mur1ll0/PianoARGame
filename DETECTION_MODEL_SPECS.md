# PianoARGame — Detection Model Specs

> **Data:** 25/04/2026  
> **Contexto:** Substituição do detector heurístico por pipeline IA em duas etapas para detecção robusta de piano/teclado.  
> **Base:** Análise do pipeline atual + plano técnico de sessões anteriores (sessões b959f15a / c0153faa).

---

## 1. Contexto e Motivação

### 1.1 Estado Atual do Detector

O `PianoDetector` atual (`Assets/Scripts/AR/PianoDetector.cs`) implementa um MVP baseado em:
1. Conversão do frame para grayscale
2. Gradiente horizontal por coluna (`colGrad`)
3. Suavização com média móvel
4. Limiarização: `mean + thresholdFactor * (max - mean)`
5. Detecção de picos (`peaks`) com separação mínima

**`DetectionResult` atual:**
```csharp
polygon       Vector2[]   // bounding polygon em pixels
pose          Pose        // SEMPRE Pose.identity (não implementado)
confidence    float       // 0..1
keyColumns    int[]       // posições X dos separadores de teclas
keyCount      int
// telemetria
processingTimeMs, gradientMean, gradientMax, detectionThreshold
isTrackingStable, reprojectionError, statusMessage
```

### 1.2 Limitações Críticas do MVP

| Problema | Impacto |
|---|---|
| Pose sempre `Pose.identity` | Overlay AR não alinhado em nenhuma cena real |
| Sem perspectiva/homografia | Teclas distorcidas em ângulo oblíquo |
| Sem validação de periodicidade | Confunde grade de fundo / estampas com teclas |
| Sem robustez a variações de luz | Falha em baixa luz ou reflexo |
| Limiar `thresholdFactor` manual | Não generaliza entre câmeras/pianos |
| Contagem de teclas não validada | Sem verificação contra padrões 49/61/76/88 |

### 1.3 Objetivo desta Spec

Definir dois candidatos de modelo IA para substituição do backend heurístico, mantendo o **contrato de API de `DetectionResult` existente** para minimizar regressão nos sistemas downstream (`KeyEstimator`, `CalibrationManager`, `KeyboardTracker`).

---

## 2. Arquitetura do Novo Pipeline

### 2.1 Visão Geral — Dois Estágios

```
Frame (Texture2D)
      │
      ▼
┌─────────────────────────────┐
│  Estágio 1: Detector Global │  ← CANDIDATO A ou B
│  Detecta bounding box        │
│  + pose estimada do teclado  │
└────────────┬────────────────┘
             │  BBox, polygon, confidence
             ▼
┌─────────────────────────────┐
│  Estágio 2: Key Estimator   │  ← Existente + upgrade geométrico
│  ROI warp + perfil de borda │
│  White-key periodicity      │
│  Black-key pattern validate │
└────────────┬────────────────┘
             │  KeyInfo[], keyCount, keyColumns
             ▼
        DetectionResult
        (contrato mantido)
             │
    ┌────────┴───────────┐
    │                    │
KeyboardTracker    CalibrationManager
```

### 2.2 Contrato de API Preservado

O `DetectionResult` existente **não muda campos públicos**. São adicionados apenas campos opcionais:

```csharp
// Campos existentes — sem alteração
public Vector2[] polygon;
public Pose pose;
public float confidence;
public int[] keyColumns;
public int keyCount;
public float processingTimeMs;
public float gradientMean;
public float gradientMax;
public float detectionThreshold;
public bool isTrackingStable;
public float reprojectionError;
public string statusMessage;

// Campos novos (adicionados sem quebrar compilação)
public Rect boundingBox;          // bbox 2D do teclado detectado (Stage 1)
public float stage1Confidence;    // confiança bruta do modelo IA (Stage 1)
public string modelBackend;       // "GradientMVP" | "ONNX_CandidateA" | "ONNX_CandidateB"
public float inferenceTimeMs;     // tempo de inferência ONNX (Stage 1)
public int stage2KeyCountRaw;     // contagem bruta antes de snap para 49/61/76/88
public float periodicityScore;    // regularidade das bordas (Stage 2)
```

---

## 3. Candidatos de Modelo

### 3.1 Candidato A — Roboflow pianoKeyDetection (Pré-treinado)

**Objetivo:** POC rápido para validar integração ONNX e baseline de precisão antes de treino custom.

| Atributo | Valor |
|---|---|
| Fonte | Roboflow Universe — `pianoKeyDetection` |
| Arquitetura base | YOLOv8n (nano) |
| mAP@50 | 78.1% |
| Precision | 75.2% |
| Recall | 71.5% |
| Classes detectadas | `piano`, `keyboard` (teclado inteiro como bounding box) |
| Formato de exportação alvo | ONNX (opset 12+) |
| Tamanho estimado | ~6 MB (YOLOv8n) |
| Inferência Android (CPU) | ~80–150 ms por frame (estimado) |

**Prós:**
- Download imediato, sem necessidade de dataset/treino
- Validação da integração ONNX em horas, não dias
- mAP aceitável para protótipo

**Contras:**
- Dataset Roboflow pode não incluir pianos no ângulo e iluminação do usuário
- Sem fine-tuning: Precision/Recall pode cair para cenários específicos (pianos digitais, teclados parcialmente fora de quadro)
- Recall 71.5% significa ~28% de frames sem detecção — inaceitável para tracking contínuo sem suavização temporal

**Uso recomendado:** Primeiro modelo integrado ao `PianoDetector` para validar:
1. Pipeline ONNX → Unity Sentis funcional
2. Formato de saída (bbox, confidence) mapeando para `DetectionResult`
3. FPS baseline no editor e Android

---

### 3.2 Candidato B — YOLOv8n Custom (Dataset Próprio)

**Objetivo:** Modelo robusto para produção, treinado com imagens do domínio do usuário.

| Atributo | Valor |
|---|---|
| Arquitetura | YOLOv8n (nano) — transfer learning de `yolov8n.pt` |
| Dataset seed | PSLeon24 (GitHub) — ~2.605 imagens YOLOv5/SSD format |
| Dataset alvo (fine-tuning) | 1.500–3.000 imagens anotadas do domínio + augmentations |
| Classes | `piano_keyboard` (bbox do teclado inteiro) |
| Formato de exportação | ONNX (opset 12+), com int8 quantization opcional |
| Meta de mAP@50 | ≥88% no dataset de validação do domínio |
| Inferência Android (CPU) | ~50–100 ms com quantização int8 |

**Estrutura do Dataset Alvo:**
```
dataset/
  train/
    images/   # JPG/PNG, resolução padrão 640×640
    labels/   # YOLO format: class cx cy w h (normalizado)
  val/
    images/
    labels/
  test/
    images/
    labels/
```

**Classes de anotação:**
- `0: piano_keyboard` — bounding box em torno do teclado visível completo
- *(Opcional, Fase 2)* `1: white_key`, `2: black_key` para stage 2 refinado

**Augmentations obrigatórias no treino:**
- Variação de exposição: ±40%
- Blur gaussiano: kernel 3–7
- Perspectiva: ±15°
- Oclusão aleatória: blocos 0–30% da área do teclado
- Flip horizontal
- Variação de brilho e contraste

**Prós:**
- Precisão esperada maior para o contexto do usuário
- Controle sobre distribuição de exemplos difíceis
- Pode incluir pianos digitais, teclados midi, parcialmente ocluídos

**Contras:**
- Requer coleta e anotação de dataset (~1–2 semanas de esforço)
- Treino: ~2–4h em GPU (Colab/local) para 100 épocas
- Avaliação de mAP levará mais iterações

**Uso recomendado:** Substituto do Candidato A após validação do pipeline ONNX. Candidato para release production.

---

## 4. Integração Unity — ONNX via Unity Sentis

### 4.1 Pacote Unity

| Pacote | Versão recomendada |
|---|---|
| `com.unity.sentis` | 1.4.x ou superior (substitui Barracuda) |

> ⚠️ **Verificar `Packages/manifest.json`:** O projeto tem `Barracuda` listado como dependência opcional em `TASKS.md` item 26. Sentis é o substituto oficial e compatível com Unity 2022+.

### 4.2 Arquivo do Modelo

```
Assets/
  StreamingAssets/
    Models/
      piano_detector_candidateA.onnx   ← Candidato A
      piano_detector_candidateB.onnx   ← Candidato B (após treino)
      piano_detector_active.onnx       ← Symlink/cópia do modelo ativo
```

### 4.3 Configuração no Inspector

Adicionar em `PianoDetector` (MonoBehaviour):

```csharp
[Header("IA Backend")]
public ModelAsset onnxModel;            // arrasta o .onnx no Inspector
public bool useOnnxBackend = false;     // feature flag — false = MVP heurístico
public BackendType backendType = BackendType.CPU;  // CPU ou GPUCompute
public int inferenceInputSize = 640;    // resolução de entrada (640×640 padrão YOLOv8)
public float onnxConfidenceThreshold = 0.45f;
public float onnxIouThreshold = 0.45f; // NMS IoU threshold
```

### 4.4 Pipeline de Inferência (Pseudocódigo)

```csharp
// Em PianoDetector.Detect(Texture2D frame)
if (useOnnxBackend && onnxModel != null)
{
    // 1. Pre-process
    var inputTensor = PreprocessFrame(frame, inferenceInputSize); // resize + normalize [0,1]

    // 2. Inference
    var worker = WorkerFactory.CreateWorker(backendType, Model.FromAsset(onnxModel));
    worker.Execute(inputTensor);
    var outputTensor = worker.PeekOutput("output0"); // YOLOv8 output shape: [1, 84, 8400]

    // 3. Post-process (NMS + decode)
    var detections = DecodeYolov8Output(outputTensor, frame.width, frame.height,
                                         onnxConfidenceThreshold, onnxIouThreshold);

    // 4. Map to DetectionResult (mantém contrato existente)
    return BuildDetectionResult(detections, frame);
}
else
{
    return DetectGradientMVP(frame); // fallback atual
}
```

### 4.5 Formato de Saída YOLOv8 ONNX

YOLOv8 exportado para ONNX produz tensor `[1, 84, 8400]`:
- Dim 0: batch
- Dim 1: 84 = 4 (cx, cy, w, h) + 80 classes COCO (ou N classes custom)
- Dim 2: 8400 âncoras candidatas

**Pós-processamento necessário:**
1. Transpor para `[8400, 84]`
2. Filtrar por `max_class_score >= confidence_threshold`
3. Converter `[cx, cy, w, h]` (normalizado) para `[x1, y1, x2, y2]` absoluto
4. Aplicar NMS (Non-Maximum Suppression)
5. Mapear classe detectada para `piano_keyboard` (índice 0 no modelo custom)

---

## 5. Mapeamento de Saída IA → `DetectionResult`

```csharp
// Detecção IA retorna: bbox normalizado, confidence, classe
DetectionResult BuildDetectionResult(YoloDetection best, Texture2D frame)
{
    int W = frame.width, H = frame.height;

    // BBox em pixels
    var bbox = new Rect(best.x1 * W, best.y1 * H,
                        (best.x2 - best.x1) * W,
                        (best.y2 - best.y1) * H);

    // Polygon = 4 cantos da bbox
    var polygon = new Vector2[] {
        new(bbox.xMin, bbox.yMin),
        new(bbox.xMax, bbox.yMin),
        new(bbox.xMax, bbox.yMax),
        new(bbox.xMin, bbox.yMax)
    };

    // Pose estimada via PnP com cantos bbox + plano Z=0 assumido
    // (refinado na CalibrationManager com homografia)
    var pose = EstimatePoseFromBbox(bbox, W, H);

    // Key columns derivados pelo Stage 2 (KeyEstimator) via ROI warp
    var (keyColumns, keyCount) = EstimateKeysInROI(frame, bbox);

    return new DetectionResult {
        polygon = polygon,
        pose = pose,
        confidence = best.score,
        keyColumns = keyColumns,
        keyCount = keyCount,
        boundingBox = bbox,
        stage1Confidence = best.score,
        modelBackend = useOnnxBackend ? "ONNX_CandidateA" : "GradientMVP",
        // ... telemetria
    };
}
```

---

## 6. Stage 2 — Estimativa de Teclas (Upgrade do `KeyEstimator`)

Após Stage 1 retornar bbox confiável, Stage 2 opera na ROI retificada:

### 6.1 Algoritmo Proposto

1. **Crop + warp perspectivo** usando homografia da CalibrationManager (se disponível) ou bbox direta
2. **Perfil longitudinal de bordas**: somar gradiente vertical coluna por coluna na ROI retificada
3. **Periodicity constraint**: buscar padrão com espaçamento regular via autocorrelação
4. **Snap para N válido**: mapear contagem bruta para {49, 61, 76, 88} mais próximo dentro de tolerância ±3 teclas
5. **Validação do padrão preto/branco**: verificar proporção de pixels escuros nas posições inferidas de teclas pretas

### 6.2 Critério de Rejeição

| Condição | Ação |
|---|---|
| `periodicityScore < 0.4` | `confidence *= 0.3`, `statusMessage = "low_periodicity"` |
| `keyCount < 25 || keyCount > 92` | `confidence = 0`, `statusMessage = "invalid_key_count"` |
| `bbox.width / bbox.height < 2.0` | `confidence *= 0.5` (aspecto não plausível de teclado) |

---

## 7. Fases de Implementação

### PR-1 — Setup ONNX + Candidato A (POC)
**Estimativa:** 1–2 dias

- [ ] Adicionar `com.unity.sentis` ao `Packages/manifest.json`
- [ ] Download e conversão do modelo Roboflow para ONNX (Colab/Python: `yolo export format=onnx`)
- [ ] Criar `Assets/StreamingAssets/Models/` e adicionar `.onnx`
- [ ] Adicionar campos `useOnnxBackend`, `onnxModel`, `backendType` ao `PianoDetector`
- [ ] Implementar `PreprocessFrame()` e `DecodeYolov8Output()`
- [ ] Feature flag: `useOnnxBackend = false` por padrão (MVP heurístico não é removido)
- [ ] Adicionar novos campos opcionais ao `DetectionResult`
- [ ] Validar: Editor webcam detecta teclado, bbox visível no overlay cyan do `TestWebcamController`

**Critério de aceite PR-1:**
- Build compila sem erros com `com.unity.sentis`
- Com `useOnnxBackend = true`, `DetectionResult.stage1Confidence > 0` para frame de piano
- `TestWebcamController` exibe bbox no overlay com label "ONNX_CandidateA"
- FPS no Editor não cai abaixo de 15 FPS durante inferência

---

### PR-2 — Benchmark Comparativo A vs MVP

**Estimativa:** 1 dia

- [ ] Adicionar modo benchmark ao `DetectionChecker` (rodar ambos backends no mesmo frame, logar métricas CSV)
- [ ] Coletar 50+ frames de teste (3 teclados diferentes, 3 condições de luz)
- [ ] Medir: `confidence`, `keyCount`, `processingTimeMs`, `inferenceTimeMs`
- [ ] Gerar tabela comparativa: MVP Heurístico vs Candidato A

**Métricas a coletar:**

| Métrica | Heurístico MVP | Candidato A | Meta (prod) |
|---|---|---|---|
| Taxa de detecção (frames com conf > 0.45) | — | — | ≥90% |
| Erro médio keyCount (abs) | — | — | ≤2 |
| processingTimeMs médio | — | — | ≤50ms (Editor) |
| inferenceTimeMs médio | N/A | — | ≤150ms Android |

---

### PR-3 — Treino Candidato B (Custom YOLOv8n)

**Estimativa:** 1–2 semanas (inclui coleta de dados)

- [ ] Coletar imagens do domínio: câmera do usuário, diferentes pianos, iluminações
- [ ] Anotar com Roboflow ou Label Studio (formato YOLO)
- [ ] Treinar via Ultralytics + Colab:
  ```python
  from ultralytics import YOLO
  model = YOLO("yolov8n.pt")
  model.train(data="piano_dataset.yaml", epochs=100, imgsz=640, batch=16)
  model.export(format="onnx", opset=12, simplify=True)
  ```
- [ ] Validar mAP@50 ≥ 88% no set de validação
- [ ] Adicionar modelo ao projeto e testar via feature flag `"ONNX_CandidateB"`

---

### PR-4 — Stage 2 Upgrade (KeyEstimator com ROI Warp)

**Estimativa:** 2–3 dias

- [ ] Adicionar `EstimateKeysInROI(Texture2D frame, Rect bbox)` ao `PianoDetector`
- [ ] Implementar crop + warp perspectivo usando homografia da `CalibrationManager`
- [ ] Substituir gradiente MVP por perfil de borda em ROI retificada
- [ ] Adicionar autocorrelação para detecção de periodicidade
- [ ] Implementar snap para contagem válida {49, 61, 76, 88}
- [ ] Adicionar `periodicityScore` ao `DetectionResult`

**Critério de aceite PR-4:**
- Teclado 61 teclas: `keyCount` retorna 61 em ≥85% dos frames em boa iluminação
- `periodicityScore > 0.5` em frames com teclado frontal
- Sem regressão: testes EditMode existentes continuam passando

---

### PR-5 — Tracking + Integração CalibrationManager

**Estimativa:** 2 dias

- [ ] Atualizar `CalibrationManager` para usar `bbox` do Stage 1 como proposta inicial de cantos
- [ ] Atualizar `KeyboardTracker` para usar `stage1Confidence` como sinal de confiança
- [ ] Reduzir cadência de inferência ONNX: re-detectar a 5–10 Hz, tracker a 30 Hz
- [ ] Adicionar relocalization usando último `LastStableDetection.polygon` como template
- [ ] Benchmark final Android: FPS, latência lock, accuracy keyCount

---

### PR-6 — Validação Final e Gates de Release

**Estimativa:** 1–2 dias

- [ ] Rodar testes EditMode: `MidiLoader`, `KeyEstimator`, `ScoreManager`
- [ ] Rodar benchmark em Android com clips gravados
- [ ] Validar todos os critérios de aceite (seção 8)
- [ ] Atualizar `DESIGN_SPECS.md` com estado final do pipeline IA

---

## 8. Critérios de Aceitação

### 8.1 Detecção Geral

| # | Cenário | Critério |
|---|---|---|
| AC-01 | Teclado 61 teclas, boa luz, frontal | `keyCount ∈ [59..63]`, `confidence ≥ 0.7` |
| AC-02 | Teclado 88 teclas, boa luz | `keyCount ∈ [86..90]`, `confidence ≥ 0.65` |
| AC-03 | Teclado 49 teclas, boa luz | `keyCount ∈ [47..51]` |
| AC-04 | Teclado em ângulo oblíquo (±20°) | `confidence ≥ 0.5`, overlay ≤ 15 px de desvio |
| AC-05 | Baixa iluminação (ambiente ≤ 100 lux) | Detecção inicial em ≤ 4s; fallback message "low_light" |
| AC-06 | Oclusão parcial (≤ 30% do teclado coberto) | Tracking mantido; sem re-lock falso |
| AC-07 | Camera shake moderado | `KeyboardTrackingState` não cai para Lost em ≤ 1s de agitação |

### 8.2 Performance

| # | Métrica | Meta |
|---|---|---|
| P-01 | FPS de render (Android mid-range) | ≥ 30 FPS |
| P-02 | Tempo de detecção inicial (lock) | ≤ 2s |
| P-03 | `inferenceTimeMs` ONNX (CPU, Android) | ≤ 150 ms |
| P-04 | `processingTimeMs` total (Stage 1 + 2) | ≤ 200 ms a 5 Hz |
| P-05 | Memória adicional do modelo | ≤ 30 MB RAM |

### 8.3 Integração (sem regressão)

| # | Critério |
|---|---|
| I-01 | `MidiMapper`, `SpawnManager`, `KeyHitDetector` funcionam sem alteração |
| I-02 | `CalibrationManager.RunSingleStepCalibration()` continua funcional |
| I-03 | `KeyboardTracker` FSM opera corretamente com novos campos de `DetectionResult` |
| I-04 | Feature flag `useOnnxBackend = false` retorna ao MVP heurístico sem crash |

---

## 9. Dependências

### 9.1 Unity Packages

```json
// Adicionar em Packages/manifest.json
"com.unity.sentis": "1.4.0"
```

> Verificar compatibilidade com Unity versão do projeto (Unity 2022/2023 LTS recomendado).

### 9.2 Ferramentas Externas (fora do projeto Unity)

| Ferramenta | Uso |
|---|---|
| Python 3.10+ + Ultralytics | Treino YOLOv8n (Candidato B) + export ONNX |
| Roboflow / Label Studio | Anotação do dataset |
| Google Colab (T4 GPU) | Treino se GPU local indisponível |
| `onnxsim` (onnx-simplifier) | Simplificação do grafo ONNX antes de importar no Unity |

### 9.3 Datasets

| Dataset | Fonte | Uso |
|---|---|---|
| pianoKeyDetection (Roboflow) | `universe.roboflow.com/pianoKeyDetection` | Candidato A — modelo pré-treinado |
| PSLeon24 piano dataset | GitHub PSLeon24 | Seed para dataset custom do Candidato B |
| Dataset próprio | Coleta manual | Fine-tuning obrigatório para prod (Candidato B) |

---

## 10. Arquivos Afetados

| Arquivo | Alteração |
|---|---|
| `Assets/Scripts/AR/PianoDetector.cs` | Adicionar backend ONNX (Sentis), preservar heurístico como fallback |
| `Assets/Scripts/AR/KeyEstimator.cs` | Stage 2 com ROI warp + periodicity |
| `Assets/Scripts/AR/CalibrationManager.cs` | Usar bbox Stage 1 como entrada de cantos |
| `Assets/Scripts/AR/KeyboardTracker.cs` | Usar `stage1Confidence` no FSM |
| `Assets/Scripts/AR/TestWebcamController.cs` | Overlay de bbox + label de backend + modo benchmark |
| `Assets/Scripts/AR/DetectionChecker.cs` | Modo benchmark comparativo, export CSV |
| `Assets/Scripts/Services/ConfigService.cs` | Persistir `activeModelBackend` em `config.json` |
| `DESIGN_SPECS.md` | Atualizar seção "Detecção & Tracking" |
| `TASKS.md` | Adicionar itens PR-1..PR-6 |
| `Packages/manifest.json` | Adicionar `com.unity.sentis` |
| `Assets/StreamingAssets/Models/` | Nova pasta — modelos `.onnx` |

---

## 11. Riscos e Mitigações

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| Candidato A com recall baixo no domínio do usuário | Alta | Alto | Sempre prever Candidato B; feature flag para rollback |
| Sentis 1.4 incompatível com versão Unity do projeto | Média | Alto | Verificar versão Unity antes de `manifest.json`; testar em branch separada |
| ONNX export YOLOv8 com ops não suportadas pelo Sentis | Média | Alto | Usar `onnxsim`, opset 12; evitar ops dinâmicas |
| Inferência lenta em Android mid-range (> 200ms) | Média | Médio | Cadência 5 Hz para Stage 1; int8 quantization no Candidato B |
| Dataset custom insuficiente/desequilibrado | Média | Médio | Usar augmentations agressivas; min 300 imagens por condição de luz |
| Regressão em `KeyboardTracker` após mudança de confiança | Baixa | Alto | Testes EditMode para FSM + benchmark antes/depois |

---

## 12. Decisões de Arquitetura

| Decisão | Escolha | Justificativa |
|---|---|---|
| Runtime de inferência | Unity Sentis (não ONNX Runtime native plugin) | Integração nativa com Unity; sem bridge C++; suporte oficial mobile |
| Arquitetura do modelo | YOLOv8n (nano) | Melhor trade-off size/accuracy para mobile; export ONNX estável |
| Stage 1 vs End-to-end | Dois estágios (teclado → teclas) | Menor risco; Stage 1 pode usar modelo pré-treinado; Stage 2 geométrico robusto |
| Manutenção do contrato DetectionResult | Sim | Evita retrabalho em 6+ sistemas downstream já implementados |
| Feature flag | `useOnnxBackend` no Inspector | Rollback imediato em produção sem recompilar |
| Offline-first | 100% local, sem API cloud | Requisito de privacidade e latência do projeto |
| Treino custom obrigatório | Candidato B para prod | Candidato A é POC; mAP 78% insuficiente para release |

---

## 13. Sequência de Validação Recomendada

```
1. PR-1: Setup Sentis + Candidato A funcional no Editor
       ↓ (bloqueador)
2. PR-2: Benchmark A vs MVP → confirmar ganho real
       ↓ (paralelo com PR-3)
3. PR-3: Treino Candidato B → mAP ≥ 88%
       ↓ (após PR-2)
4. PR-4: Stage 2 com ROI warp
       ↓ (após PR-1 e PR-4)
5. PR-5: Tracking + Calibração integrados
       ↓
6. PR-6: Validação final + gates Android
```

---

*Documento gerado em 25/04/2026. Atualizar após cada PR concluído.*
