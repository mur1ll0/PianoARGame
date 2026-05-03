# PianoARGame — Design Specs (Spec-Driven)

Visão Geral
- Objetivo: Aplicativo AR educativo para Android (testável via webcam no Editor) que reconhece um piano/teclado físico, desenha trilhas sobre cada tecla e sincroniza notas MIDI para treino e pontuação.
- Plataformas alvo: Android (principal), Unity Editor (webcam) para testes; HMDs opcionais.

Requisitos Funcionais (resumo)
- Detecção: `PianoDetector` usa ONNX obrigatoriamente para detectar a área do teclado e retorna polígono/pose.
- Contagem de teclas: `KeyEstimator` identifica e conta teclas e fornece mapeamento 2D/3D por tecla.
- Mapeamento MIDI→Teclas: `MidiMapper` associa notas MIDI a índices de tecla com offset configurável.
- Overlays: `TrailRendererAR` desenha trilhas por tecla e segue o tracking do teclado.
- Menu MIDI: UI permite configurar pasta de músicas `.mid`, listar e carregar músicas.
- Fluxo: notas aparecem com lead-time e seguem direção da tecla; `KeyHitDetector` registra pressões do usuário.
- Pontuação: `ScoreManager` computa score e salva HighScores por música.

Requisitos Não-Funcionais (resumo)
- Performance: alvo ≥30 FPS em Android médio; detecção inicial ≤2s.
- Latência: input-to-score ideal <80ms, aceitável <120ms.
- Privacidade: dados locais; MIDI lidos localmente.
- Persistência: usar `Application.persistentDataPath`, JSON versionado.

Arquitetura & APIs (propostas)
- `ARSessionManager` — inicializa sessão AR / webcam fallback.
- `PianoDetector` — `Detect(Texture2D frame) -> DetectionResult { polygon, pose, confidence }`.
- `KeyEstimator` — `EstimateKeys(DetectionResult) -> List<KeyInfo { index, bbox2D, pos3D, width }>`.
- `TrailRendererAR` — `AttachToKey(KeyInfo)`, `UpdatePose(pose)`.
- `MidiLoader` — `Load(string path) -> MidiSong { events, tempoMap }`.
- `MidiMapper` — `MapToKeys(MidiSong, KeyInfo[], baseMidiNote)`.
- `KeyHitDetector` — evento `OnKeyHit(index, timeOffset, accuracy)`.
- `ScoreManager` — `StartSession(songId)`, `AddHit(index, score)`, `GetHighScore(songId)`, `Save()`.

Detecção & Tracking (detalhes)
- Pipeline atual (Etapa 1 + Etapa 2 provisória):
  1. Stage-1 ONNX: detector segmentado de `keyboard_area` (obrigatório, sem modo heurístico global).
  2. Stage-2 heurístico local: contagem/colunas de teclas apenas dentro da ROI detectada.
  3. Calibração por perfil de teclado: `Auto`, `61`, `76`, `88` para reduzir falsos picos.
  4. Tracking: usa resultado de detecção da área do teclado para alinhamento dos sistemas de gameplay.
- Métricas de ACEITAÇÃO: contagem de teclas ≥95% precisão; overlay alinhado ≤10 px a 30 cm (Editor) / ≤2 cm no mundo real.

Debug visual no Editor
- `TestWebcamController` desenha overlay da bbox ONNX (stage-1), ROI do stage-2 e picos brutos detectados.
- O painel de detecção mostra métricas `stage2KeyCountRaw`, perfil esperado (`61/76/88`) e contagem final.

MIDI & Sincronização
- Ler Note On/Off, Set Tempo; usar `AudioSettings.dspTime` para sincronização.
- Lead time configurável (ms) para spawn de objetos visuais.
- Janelas de acerto: Perfect ≤80ms (+100 pts), Good ≤160ms (+50 pts), Miss >160ms.

Gameplay & UI
- Menu principal: `Detect Piano`, `Open Music Folder`, lista `.mid`, `Start`.
- Configs: `Music Folder`, `Base MIDI Note Offset`, `Lead Time`, `Sensitivity`.
- Em jogo: score, combo, próxima nota, botão pause.

Persistência
- HighScores JSON por música em `persistentDataPath/HighScores/`.
- Configs em `persistentDataPath/config.json`.

Testes & Critérios de Aceitação (exemplos GWT)
- Given: teclado frontal visível ≥50% → When: clique `Detect Piano` → Then: detector retorna pose e overlays alinhados em ≤2s.
- Given: teclado 61 teclas → When: detecção concluída → Then: `KeyEstimator` retorna 61 keys (0..60).
- Given: pasta com `song.mid` → When: carregar música → Then: notas mapeadas conforme offset configurado.

Dependências recomendadas
- Unity LTS (2021/2022/2023), AR Foundation, ARCore XR Plugin; opcional Barracuda/Burst para modelos ML.

Riscos & Mitigações
- Baixa luz: indicar instruções ao usuário; fallback para homography.
- Oclusões: usar tracking temporal e previsão (Kalman).

Estado de implementação (01/05/2026)
> Scripts já implementados e compilando sem erros:

**Camada AR / Detecção**
- `Assets/Scripts/AR/PianoDetector.cs` — ONNX obrigatório para stage-1 (`keyboard_area`) + stage-2 heurístico local na ROI detectada; calibração por perfil (`Auto/61/76/88`) para reduzir falsos picos; `DetectionResult` inclui telemetria de debug (`stage2Roi`, `stage2PeaksRaw`, `stage2KeyCountRaw`, `stage2ExpectedKeyCount`, `periodicityScore`).
- `Assets/Scripts/AR/KeyEstimator.cs` — deriva `KeyInfo[]` (bbox2D, pos3D, width) de `DetectionResult`.
- `Assets/Scripts/AR/CalibrationManager.cs` — snapshot markerless, proposição de cantos, homografia simples, persistência via `ConfigService`.
- `Assets/Scripts/AR/KeyboardTracker.cs` — FSM Lost/Degraded/Tracked por limiar de confiança com gate de predição.
- `Assets/Scripts/AR/ARSessionManager.cs` — inicializa sessão AR/webcam fallback.
- `Assets/Scripts/AR/TestWebcamController.cs` — captura de webcam para Editor, snapshot de frame, seleção de perfil de teclado do stage-2 e overlay debug ROI/picos para validação visual rápida.
- `Assets/Scripts/AR/DetectionChecker.cs` — helper de diagnóstico do pipeline.
- `Assets/Scripts/AR/UIManager.cs` — gerencia mensagens de estado AR.
- `Assets/Scripts/AR/MidiLoader.cs` — parser MIDI mínimo (Note On/Off, Set Tempo).
- `Assets/Scripts/AR/ScoreManager.cs` — pontuação em sessão + persistência JSON de HighScores.

**Camada Services**
- `Assets/Scripts/Services/ConfigService.cs` — leitura/escrita de `config.json` versionado; inclui `CalibrationProfile` (corners, homography, keyCount, timestamp).
- `Assets/Scripts/Services/MidiService.cs` — expõe `Load`, `GetSongList`, `GetSongById`.

**Camada UI / HMD**
- `Assets/Scripts/UI/HmdHudController.cs` — HUD world-space para HMD, refresh 5 Hz, exibe estado AR, calibração e confiança.

**Editor Tools**
- `Assets/Editor/CreateHmdBaseScene.cs` — gera `Assets/Scenes/HMD_Base.unity` (menu `PianoAR/Create HMD Base Scene`).
- `Assets/Editor/CreateTestWebcamScene.cs` — gera cena de teste com webcam.
- `Assets/Editor/EnsureTestSceneWired.cs` — verifica referências na cena de teste.
- `Assets/Editor/WebcamInspectorWindow.cs` — janela de inspeção de webcams disponíveis.
- `Assets/Editor/WriteWebcamsToFile.cs` — exporta lista de webcams para arquivo.
- `Assets/Editor/McpLogCleanup.cs` — limpa logs MCP.

**Pendente para Fase 3 (Gameplay MVP)**
- `SpawnManager`, `KeyHitDetector`, `TrailRendererAR` — não implementados.
- `MidiMapper` — não implementado.
- Cena `Gameplay.unity` — não criada.
- UI de gameplay (score, combo, pause) — não implementada.

Próximos passos
1. Criar pastas `Assets/Scripts/Gameplay/` e `Assets/Scripts/Midi/`.
2. Implementar `MidiMapper` (mapear MidiSong + KeyInfo[] → sequência de hit esperado).
3. Implementar `SpawnManager` (prefab de nota, lead-time, pista por tecla).
4. Implementar `KeyHitDetector` (mouse/touch → evento `OnKeyHit`).
5. Criar cena `Gameplay.unity` ligando todos os sistemas.
6. Criar testes unitários para `MidiLoader` e `ScoreManager`.

----
Documento gerado via spec-driven; usar como contrato para PRs e implementação.

## Arquitetura recomendada
- Arquitetura em camadas: Core (domínio), AR (detecção/tracking), Midi (parsing/sincronização), Gameplay (spawning, scoring), UI, Persistence.
- Comunicação entre camadas via eventos e interfaces: evite acoplamento direto a MonoBehaviours nos serviços de domínio.
- Serviços centrais (singleton pattern leve) para `ARSessionManager`, `MidiService`, `ScoreService`.

## Organização de pastas (recomendação)
Estrutura proposta para o projeto Unity, seguindo boas práticas:

- `Assets/` — código e assets do jogo.
  - `Assets/Scenes/` — cenas: `Main.unity`, `Test_Editor_Webcam.unity`, `Gameplay.unity`.
  - `Assets/Scripts/` — código fonte C# organizado por domínio.
    - `Assets/Scripts/Core/` — modelos de dados, utilitários, constantes, interfaces.
    - `Assets/Scripts/AR/` — `ARSessionManager`, `PianoDetector`, `KeyEstimator`, tracking helpers.
    - `Assets/Scripts/Midi/` — `MidiLoader`, `MidiMapper`, tempo/clock abstractions.
    - `Assets/Scripts/Gameplay/` — `TrailRendererAR`, `KeyHitDetector`, `SpawnManager`, `ScoreManager`.
    - `Assets/Scripts/UI/` — `UIManager`, controllers de menu, binding de UI.
    - `Assets/Scripts/Services/` — persistência, config service, file browser abstractions.
  - `Assets/Prefabs/` — prefabs reutilizáveis (NotePrefab, KeyOverlay, UI prefabs).
  - `Assets/StreamingAssets/MIDI/` — pasta padrão para arquivos MIDI usados em builds.
  - `Assets/Resources/` — recursos carregáveis via Resources (usar com parcimônia).
  - `Assets/Plugins/` — bibliotecas nativas / pacotes terceiros (ex.: DryWetMIDI.dll se usado).
  - `Assets/Tests/` — testes de PlayMode/EditMode específicos.

- `Docs/` — documentação do projeto, specs, dataset, instruções de teste.
- `Tools/` — scripts de utilitários, ferramentas editoriais.
- `Packages/` — packages manifest.

## Convenções de código e estilo
- Nomes de classes PascalCase (`PianoDetector`), métodos PascalCase (`EstimateKeys`).
- Fiducia em tipos fortemente tipados para mensagens/eventos (não strings mágicas).
- Componentes MonoBehaviour com APIs públicas mínimas e métodos `Initialize(IService)` para injeção leve em testes.
- Seriais públicos com `[SerializeField] private` para campos expostos no Inspector.

## Detalhamento dos módulos (contratos)

1) `ARSessionManager`
- Responsabilidades: inicializar sessão AR, selecionar backend (ARCore/Editor webcam), expor estado da sessão.
- Contrato:
  - `void StartSession()`
  - `void StopSession()`
  - `bool IsRunning { get; }`

2) `PianoDetector`
- Responsabilidades: detectar a área de teclado via ONNX (stage-1) e estimar contagem/bordas de teclas via heurística local calibrada por perfil (stage-2), entregando `DetectionResult` com polígono, bbox, confiança e telemetria de debug.
- Contrato:
  - `DetectionResult Detect(Texture2D frame)` (sincrono para testes) e `Task<DetectionResult> DetectAsync(Texture2D frame)` em implementação futura.

3) `KeyEstimator`
- Responsabilidades: a partir de `DetectionResult`, estimar `KeyInfo[]` com índices e transformações 2D/3D consolidadas.

4) `MidiLoader` / `MidiService`
- Responsabilidades: carregar/parsing de arquivos MIDI, expor `MidiSong` com eventos absolutos em segundos, fornecer relógio de referência (DSP time).

5) `MidiMapper`
- Responsabilidades: associar notas MIDI a índices de tecla detectados levando em conta `baseMidiNote` configurável.

6) `SpawnManager` / `TrailRendererAR`
- Responsabilidades: instanciar prefabs de notas com lead-time configurável, mover objetos em direção à tecla alvo com predição de pose.

7) `KeyHitDetector` e `ScoreManager`
- `KeyHitDetector` observa input (colisão virtual, hands tracking, touch) e emite `OnKeyHit(index, dspTime, offsetMs)`.
- `ScoreManager` consome `OnKeyHit`, calcula pontos, combos, e persiste HighScores.

## Formatos e persistência (detalhes)
- `MIDI` — suportar Standard MIDI Files (Format 0/1). Prefer usar biblioteca testada (ex.: DryWetMIDI) por estabilidade.
- `HighScore` — JSON com schema:

```
{
  "songId":"<string>",
  "timestamp":"<ISO8601>",
  "score":12345,
  "accuracy":0.912
}
```

- Local de salvamento: `Application.persistentDataPath/HighScores/`.

## Testes, QA e dataset
- Unit tests:
  - `MidiLoader` parsing: evento counts, tempo maps, edge cases (running status, multiple tracks).
  - `MidiMapper`: mapping com offsets e teclados parciais.
  - `ScoreManager`: janelas de acerto e pontuação.
- Integration tests (Editor with webcam): feed gravado/fixtures que simulam teclados; validação de overlays (scripted frame-by-frame).
- Dados de teste: coletar 10+ imagens por tipo de teclado (49/61/76/88), várias iluminações e ângulos.

## Performance & profiling
- Medir FPS via `UnityEngine.Profiling.Profiler` no Android; usar builds de desenvolvimento com `Development Build` e `Autoconnect Profiler`.
- Estratégias: downscale frames para detecção, executar detector em thread separada / job system, usar Burst/Barracuda se ML on-device.

## CI / Build
- Recomendar pipeline básico (opcional):
  - Windows build for Editor tests (run EditMode tests).
  - Android build (Development) to smoke test AR integration.

## Riscos adicionais e mitigação (detalhado)
- Variação visual dos teclados: mitigar com detector ONNX da área e stage-2 calibrado por perfil (61/76/88), reduzindo falsos picos em iluminação/ângulo variáveis.
- Latência de input em mobile: reduzir pre-processing, priorizar tempo real do `AudioSettings.dspTime`.

## Quiosques e testes manuais
- Criar cena `Assets/Scenes/Test_Editor_Webcam.unity` com botões para executar detecção automática em frames de webcam.

----
Atualizei o documento com arquitetura e organização de pastas recomendada.
