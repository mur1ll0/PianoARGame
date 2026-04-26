# PianoARGame — Lista lógica de tarefas (roadmap)

> **Última atualização:** 25 abril 2026  
> **Estado geral:** Fase 2 concluída / Fase 3 iniciada.  
> Legenda: ✅ concluído · 🔄 em andamento · ⬜ não iniciado

---

## Fase 0 — Preparação
- ✅ 1. Revisar `DESIGN_SPECS.md` e aprovar arquitetura.
- ✅ 2. Configurar pasta `Assets/StreamingAssets/MIDI/` e colocar exemplos `.mid`.

---

## Fase 1 — Infraestrutura & IO
- ✅ 3. Implementar `MidiLoader` mínimo (`Assets/Scripts/AR/MidiLoader.cs`).
- ✅ 4. Implementar `MidiService` com `Load`, `GetSongList`, `GetSongById` (`Assets/Scripts/Services/MidiService.cs`).
- ✅ 5. Criar `ScoreManager` com persistência JSON de HighScores (`Assets/Scripts/AR/ScoreManager.cs`).
- ✅ 6. Implementar `ConfigService` — leitura/gravação de `config.json` versionado, incluindo suporte a perfil de calibração (`Assets/Scripts/Services/ConfigService.cs`).

---

## Fase 2 — AR Detecção Básica (Editor first)  ✅ CONCLUÍDA
- ✅ 7. Implementar `PianoDetector` com pipeline grayscale → gradiente horizontal → detecção de picos de borda → `DetectionResult` com telemetria completa (`processingTimeMs`, `gradientMean`, `gradientMax`, `confidence`, `reprojectionError`, `statusMessage`) — `Assets/Scripts/AR/PianoDetector.cs`.
- ✅ 8. Implementar `KeyEstimator` que deriva `KeyInfo[]` (posições 2D/3D estimadas) a partir do `DetectionResult` — `Assets/Scripts/AR/KeyEstimator.cs`.
- ✅ 9. Implementar `CalibrationManager` — snapshot markerless, proposição de cantos, cálculo de homografia, persistência de perfil via `ConfigService` — `Assets/Scripts/AR/CalibrationManager.cs`.
- ✅ 10. Implementar `KeyboardTracker` — máquina de estados (Lost/Degraded/Tracked) baseada em confiança, com predição de gate de curto horizonte — `Assets/Scripts/AR/KeyboardTracker.cs`.
- ✅ 11. Implementar `ARSessionManager` — inicializa sessão AR/webcam fallback — `Assets/Scripts/AR/ARSessionManager.cs`.
- ✅ 12. Implementar `TestWebcamController` — captura de webcam no Editor com suporte a frame snapshot — `Assets/Scripts/AR/TestWebcamController.cs`.
- ✅ 13. Implementar `DetectionChecker` — helper de diagnóstico para validar pipeline de detecção — `Assets/Scripts/AR/DetectionChecker.cs`.
- ✅ 14. Criar `UIManager` com mensagens de estado AR — `Assets/Scripts/AR/UIManager.cs`.
- ✅ 15. Criar `HmdHudController` — HUD world-space para HMD com refresh 5 Hz, exibindo estado AR/calibração — `Assets/Scripts/UI/HmdHudController.cs`.
- ✅ 16. Criar editor script `CreateHmdBaseScene` — gera cena base `Assets/Scenes/HMD_Base.unity` via menu `PianoAR/Create HMD Base Scene` — `Assets/Editor/CreateHmdBaseScene.cs`.
- ✅ 17. Criar editor scripts auxiliares: `CreateTestWebcamScene`, `EnsureTestSceneWired`, `WebcamInspectorWindow`, `WriteWebcamsToFile`, `McpLogCleanup` — `Assets/Editor/`.

---

## Fase 3 — Gameplay MVP  🔄 EM ANDAMENTO
- ✅ 18. Implementar `SpawnManager` — spawna prefabs de notas visuais com lead-time configurável (`Assets/Scripts/Gameplay/SpawnManager.cs`).
- ✅ 19. Implementar `KeyHitDetector` — detecta pressões (colisão/mouse no Editor, touch no mobile) e emite evento `OnKeyHit(index, timeOffset, accuracy)` (`Assets/Scripts/Gameplay/KeyHitDetector.cs`).
- ✅ 20. Implementar `TrailRendererAR` — trilha visual por tecla ancorada à pose do teclado (`Assets/Scripts/Gameplay/TrailRendererAR.cs`).
- ✅ 21. Implementar `MidiMapper` — mapeia notas MIDI a índices de tecla com offset configurável (`Assets/Scripts/Midi/MidiMapper.cs`).
- ✅ 22. Construir cena `Gameplay.unity` com todos os sistemas ligados (AR + MIDI + Spawn + Score) — gerada via menu `PianoAR/Create Gameplay Scene`.
- ✅ 23. Construir UI de gameplay: score em tempo real, combo, feedback de acerto, botão pause (inclusa no editor script da cena).

---

## Fase 4 — Mobile / ARCore
- ⬜ 24. Substituir webcam fallback por ARCore/AR Foundation para mobile.
- ⬜ 25. Validar tracking em dispositivo Android; otimizar performance (target ≥30 FPS).

---

## Fase 5 — Polimento e Testes
- ⬜ 26. Adicionar modelos ML (Barracuda) para robustez da detecção se precisão < 95%.
- ⬜ 27. Criar testes automatizados EditMode para `MidiLoader`, `ScoreManager`, `KeyEstimator`, `CalibrationManager`.
- ⬜ 28. Criar testes PlayMode para ciclo AR → detecção → spawn → hit.
- ⬜ 29. Documentar limites conhecidos e guia de uso (iluminação, distância, alinhamento).

---

## Extras (opcionais)
- ⬜ Integrar gravação de sessões e replay.
- ⬜ Suporte completo a HMDs passthrough (expandir `HmdHudController` para XR Interaction Toolkit).

---

## Como retomar o trabalho
1. Abrir Unity com a cena `HMD_Base.unity` (gerar via menu `PianoAR/Create HMD Base Scene` se não existir).
2. Validar que todos os scripts compilam sem erros (checar Console).
3. Iniciar pela tarefa **18** (SpawnManager) — criar pasta `Assets/Scripts/Gameplay/` e `Assets/Scripts/Midi/` se não existirem.
4. Cada tarefa deve resultar em código compilável e testável isoladamente antes de avançar.
