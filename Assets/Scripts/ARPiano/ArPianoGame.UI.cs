using System.IO;
using PianoARGame.UI;
using UnityEngine;

namespace PianoARGame
{
    public sealed partial class ArPianoGame
    {
        private void OnGUI()
        {
            EnsureGuiReady();
            ApplyDynamicGuiScale();

            if (ShouldUseStereoHmdLayout())
            {
                DrawStereoHmdView();
            }
            else
            {
                DrawCameraBackground(new Rect(0f, 0f, Screen.width, Screen.height));
                DrawCurrentState();
            }

            DrawHeader();
            DrawSettingsToggleButton();

            if (showSettingsOverlay)
            {
                DrawSettingsOverlay();
            }

            DrawMobileMidiPathKeyboardOverlay();
            DrawMidiImportNotification();

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                DrawTextBox(new Rect(U(20f), Screen.height - U(70f), Screen.width - U(40f), U(44f)), lastError, new Color(0.4f, 0.05f, 0.05f, 0.65f));
            }
        }

        private void DrawCameraBackground(Rect rect)
        {
            bool alignOrGame = state == GameState.Align || state == GameState.Game;
            if (alignOrGame && webcam != null)
            {
                GUI.DrawTexture(rect, webcam, ScaleMode.StretchToFill, false);
            }
            else if (frameTexture != null)
            {
                GUI.DrawTexture(rect, frameTexture, ScaleMode.StretchToFill, false);
            }
            else if (webcam != null)
            {
                GUI.DrawTexture(rect, webcam, ScaleMode.StretchToFill, false);
            }
            else
            {
                GUI.Box(rect, "");
            }
        }

        private void DrawCurrentState()
        {
            switch (state)
            {
                case GameState.MainMenu:
                    DrawMainMenu();
                    break;
                case GameState.SongSelect:
                    DrawSongSelection();
                    break;
                case GameState.Align:
                    DrawAlignment();
                    break;
                case GameState.Game:
                    DrawGameplay();
                    break;
                case GameState.End:
                    DrawEnd();
                    break;
            }
        }

        private bool ShouldUseStereoHmdLayout()
        {
            return hmdStereoPreviewEnabled && (state == GameState.Align || state == GameState.Game);
        }

        private void DrawStereoHmdView()
        {
            DrawStereoEye(0f);
            DrawStereoEye(Screen.width * 0.5f);
        }

        private void DrawStereoEye(float horizontalOffset)
        {
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3(horizontalOffset, 0f, 0f), Quaternion.identity, new Vector3(0.5f, 1f, 1f));
            DrawCameraBackground(new Rect(0f, 0f, Screen.width, Screen.height));
            DrawCurrentState();
            GUI.matrix = previousMatrix;
        }

        private void EnsureGuiReady()
        {
            if (guiInitialized)
            {
                return;
            }

            CreateGuiStyles();
            guiInitialized = true;
        }

        private void CreateGuiStyles()
        {
            whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whitePixel.SetPixel(0, 0, Color.white);
            whitePixel.Apply();

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            textStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Color.white }
            };

            smallTextStyle = new GUIStyle(textStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold
            };

            iconButtonStyle = new GUIStyle(buttonStyle)
            {
                alignment = TextAnchor.MiddleCenter
            };

            selectedRowStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.95f, 0.98f, 0.95f, 1f) }
            };

            rowStyle = new GUIStyle(selectedRowStyle)
            {
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f, 1f) }
            };
        }

        private void ApplyDynamicGuiScale()
        {
            uiScale = UiScaleCalculator.Compute(Application.isMobilePlatform, Screen.width, Screen.height, Screen.dpi);

            titleStyle.fontSize = UnityEngine.Mathf.RoundToInt(46f * uiScale);
            headerStyle.fontSize = UnityEngine.Mathf.RoundToInt(26f * uiScale);
            textStyle.fontSize = UnityEngine.Mathf.RoundToInt(16f * uiScale);
            smallTextStyle.fontSize = UnityEngine.Mathf.RoundToInt(13f * uiScale);
            buttonStyle.fontSize = UnityEngine.Mathf.RoundToInt(16f * uiScale);
            selectedRowStyle.fontSize = UnityEngine.Mathf.RoundToInt(14f * uiScale);
            rowStyle.fontSize = selectedRowStyle.fontSize;
            iconButtonStyle.fontSize = UnityEngine.Mathf.RoundToInt(14f * uiScale);
        }

        private float U(float value)
        {
            return value * uiScale;
        }

        private void DrawHeader()
        {
            DrawRect(new Rect(0f, 0f, Screen.width, U(86f)), new Color(0.08f, 0.08f, 0.08f, 0.6f));
            GUI.Label(new Rect(U(18f), U(8f), U(520f), U(32f)), "AR Piano Trainer", headerStyle);

            string camInfo = webcam == null
                ? "Camera: unavailable"
                : $"Camera: {webcam.width}x{webcam.height} @ {measuredCameraFps:0.0} fps real | {requestedFps} req";
            GUI.Label(new Rect(U(18f), U(46f), Screen.width - U(40f), U(24f)), $"Render FPS: {renderFps,5:0.0} | {camInfo}", textStyle);
        }

        private void DrawMainMenu()
        {
            float panelWidth = Mathf.Min(U(860f), Screen.width - U(64f));
            float panelHeight = Mathf.Min(U(620f), Screen.height - U(150f));
            Rect panel = new Rect((Screen.width - panelWidth) * 0.5f, U(110f), panelWidth, panelHeight);
            DrawRect(panel, new Color(0.05f, 0.05f, 0.05f, 0.72f));

            float titleH = U(70f);
            GUI.Label(new Rect(panel.x + U(30f), panel.y + U(16f), panel.width - U(60f), titleH), "AR PIANO TRAINER", titleStyle);

            float buttonWidth = Mathf.Min(U(420f), panel.width - U(60f));
            float buttonX = panel.x + (panel.width - buttonWidth) * 0.5f;
            float buttonHeight = U(68f);
            float gap = U(14f);
            float totalButtonH = buttonHeight * 3f + gap * 2f;
            float startY = panel.y + Mathf.Max(U(96f), (panel.height - totalButtonH) * 0.5f);

            if (GUI.Button(new Rect(buttonX, startY, buttonWidth, buttonHeight), "Selecionar musica", buttonStyle))
            {
                state = GameState.SongSelect;
                selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, midiFiles.Count - 1));
            }

            if (GUI.Button(new Rect(buttonX, startY + buttonHeight + gap, buttonWidth, buttonHeight), "Configuracoes", buttonStyle))
            {
                showSettingsOverlay = true;
                settingsSection = SettingsSection.Midi;
            }

            if (GUI.Button(new Rect(buttonX, startY + (buttonHeight + gap) * 2f, buttonWidth, buttonHeight), "Sair", buttonStyle))
            {
                Application.Quit();
            }
        }

        private void DrawSongSelection()
        {
            Rect panel = new Rect(U(40f), U(110f), Screen.width - U(80f), Screen.height - U(180f));
            DrawRect(panel, new Color(0.06f, 0.06f, 0.06f, 0.58f));

            GUI.Label(new Rect(U(72f), U(130f), Screen.width - U(144f), U(30f)), "Selecionar musica MIDI", headerStyle);

            float listTop = U(170f);
            float bottomAreaH = U(80f);
            float listHeight = Mathf.Max(U(60f), Screen.height - listTop - bottomAreaH);
            Rect listRect = new Rect(U(70f), listTop, Screen.width - U(140f), listHeight);
            float verticalScrollbarWidth = Application.isMobilePlatform ? U(56f) : U(24f);
            Rect viewRect = new Rect(0f, 0f, listRect.width - verticalScrollbarWidth, Mathf.Max(listRect.height, midiFiles.Count * U(52f)));
            float maxScrollY = Mathf.Max(0f, viewRect.height - listRect.height);
            currentSongListRect = listRect;
            currentSongListMaxScrollY = maxScrollY;
            currentSongListRowHeight = U(52f);

            GUIStyle verticalScrollbarStyle = GUI.skin.verticalScrollbar;
            GUIStyle originalVerticalScrollbarStyle = GUI.skin.verticalScrollbar;
            GUIStyle originalVerticalScrollbarThumbStyle = GUI.skin.verticalScrollbarThumb;
            GUIStyle originalVerticalScrollbarUpButtonStyle = GUI.skin.verticalScrollbarUpButton;
            GUIStyle originalVerticalScrollbarDownButtonStyle = GUI.skin.verticalScrollbarDownButton;
            if (Application.isMobilePlatform)
            {
                verticalScrollbarStyle = new GUIStyle(GUI.skin.verticalScrollbar)
                {
                    fixedWidth = verticalScrollbarWidth
                };

                GUIStyle verticalScrollbarThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb)
                {
                    fixedWidth = verticalScrollbarWidth,
                    stretchWidth = true
                };

                GUIStyle verticalScrollbarUpButtonStyle = new GUIStyle(GUI.skin.verticalScrollbarUpButton)
                {
                    fixedWidth = verticalScrollbarWidth,
                    fixedHeight = U(28f),
                    stretchWidth = true
                };

                GUIStyle verticalScrollbarDownButtonStyle = new GUIStyle(GUI.skin.verticalScrollbarDownButton)
                {
                    fixedWidth = verticalScrollbarWidth,
                    fixedHeight = U(28f),
                    stretchWidth = true
                };

                GUI.skin.verticalScrollbar = verticalScrollbarStyle;
                GUI.skin.verticalScrollbarThumb = verticalScrollbarThumbStyle;
                GUI.skin.verticalScrollbarUpButton = verticalScrollbarUpButtonStyle;
                GUI.skin.verticalScrollbarDownButton = verticalScrollbarDownButtonStyle;
            }

            menuScrollPos = GUI.BeginScrollView(
                listRect,
                menuScrollPos,
                viewRect,
                false,
                true,
                GUI.skin.horizontalScrollbar,
                verticalScrollbarStyle);
            for (int i = 0; i < midiFiles.Count; i++)
            {
                string fileName = Path.GetFileName(midiFiles[i]);
                bool active = i == selectedIndex;
                Color bg = active ? new Color(0.16f, 0.4f, 0.16f, 0.9f) : new Color(0.13f, 0.13f, 0.13f, 0.9f);
                Rect row = new Rect(0f, i * U(52f), viewRect.width - U(10f), U(44f));
                DrawRect(row, bg);

                if (GUI.Button(row, "  " + fileName, active ? selectedRowStyle : rowStyle))
                {
                    selectedIndex = i;
                }
            }
            GUI.EndScrollView();

            if (Application.isMobilePlatform)
            {
                GUI.skin.verticalScrollbar = originalVerticalScrollbarStyle;
                GUI.skin.verticalScrollbarThumb = originalVerticalScrollbarThumbStyle;
                GUI.skin.verticalScrollbarUpButton = originalVerticalScrollbarUpButtonStyle;
                GUI.skin.verticalScrollbarDownButton = originalVerticalScrollbarDownButtonStyle;
            }

            if (midiFiles.Count == 0)
            {
                GUI.Label(new Rect(U(74f), U(230f), Screen.width - U(148f), U(24f)), "Nenhum MIDI encontrado no diretorio configurado.", textStyle);
            }

            float bY = Screen.height - U(72f);
            float bH = U(52f);
            if (GUI.Button(new Rect(U(70f), bY, U(220f), bH), "Jogar", buttonStyle))
            {
                if (midiFiles.Count > 0)
                {
                    StartSelectedSong();
                }
            }

            if (GUI.Button(new Rect(U(300f), bY, U(240f), bH), "Recarregar MIDIs", buttonStyle))
            {
                LoadMidiList();
                selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, midiFiles.Count - 1));
            }

            if (GUI.Button(new Rect(U(550f), bY, U(180f), bH), "Voltar", buttonStyle))
            {
                state = GameState.MainMenu;
            }

            if (!Application.isMobilePlatform)
            {
                HandleMouseWheelMenuScroll();
            }
        }

        private void UpdateSongSelectionTouchInput()
        {
            if (Application.platform != RuntimePlatform.Android || currentSongListRect.width <= 0f || currentSongListRect.height <= 0f)
            {
                return;
            }

            if (Input.touchCount <= 0)
            {
                menuTouchScrollActive = false;
                menuTouchScrollDragging = false;
                menuTouchScrollFingerId = -1;
                return;
            }

            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                Vector2 guiTouch = new Vector2(touch.position.x, Screen.height - touch.position.y);

                if (!menuTouchScrollActive)
                {
                    if (touch.phase == TouchPhase.Began && currentSongListRect.Contains(guiTouch))
                    {
                        menuTouchScrollActive = true;
                        menuTouchScrollDragging = false;
                        menuTouchScrollFingerId = touch.fingerId;
                        menuTouchScrollStartY = touch.position.y;
                        menuTouchScrollStartScrollY = menuScrollPos.y;
                    }

                    continue;
                }

                if (touch.fingerId != menuTouchScrollFingerId)
                {
                    continue;
                }

                if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                {
                    float dragDistance = touch.position.y - menuTouchScrollStartY;
                    if (!menuTouchScrollDragging && Mathf.Abs(dragDistance) >= U(10f))
                    {
                        menuTouchScrollDragging = true;
                    }

                    if (menuTouchScrollDragging)
                    {
                        menuScrollPos.y = Mathf.Clamp(menuTouchScrollStartScrollY - dragDistance, 0f, currentSongListMaxScrollY);
                    }
                }

                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    if (!menuTouchScrollDragging && currentSongListRect.Contains(guiTouch) && currentSongListRowHeight > 0f)
                    {
                        float localY = guiTouch.y - currentSongListRect.y + menuScrollPos.y;
                        int tappedIndex = Mathf.FloorToInt(localY / currentSongListRowHeight);
                        if (tappedIndex >= 0 && tappedIndex < midiFiles.Count)
                        {
                            selectedIndex = tappedIndex;
                        }
                    }

                    menuTouchScrollActive = false;
                    menuTouchScrollDragging = false;
                    menuTouchScrollFingerId = -1;
                }

                break;
            }
        }

        private void DrawAlignment()
        {
            DrawGuideBox();

            if (keyboardArea.HasValue)
            {
                Rect displayArea = MapFrameRectToScreen(keyboardArea.Value);
                DrawKeyboardRect(displayArea, new Color(0.1f, 0.9f, 0.25f, 1f));
                DrawTextBox(new Rect(displayArea.x, Mathf.Max(U(88f), displayArea.y - U(34f)), U(340f), U(32f)),
                    $"Area detectada conf: {bestConf * 100f:0.0}%", new Color(0.08f, 0.08f, 0.08f, 0.65f));
            }

            float ratio = Mathf.Clamp01(stableHits / (float)Mathf.Max(1, stableHitsRequired));
            Rect bar = new Rect(U(70f), Screen.height - U(140f), Screen.width - U(140f), U(28f));
            DrawRect(bar, new Color(0.16f, 0.16f, 0.16f, 0.8f));
            DrawRect(new Rect(bar.x, bar.y, bar.width * ratio, bar.height), new Color(0.15f, 0.72f, 0.23f, 0.9f));
            DrawOutline(bar, Color.white, 1f);
            GUI.Label(new Rect(U(70f), Screen.height - U(180f), U(380f), U(24f)), $"Tracking estavel: {stableHits}/{stableHitsRequired}", textStyle);

            bool canStart = keyboardArea.HasValue && stableHits >= stableHitsRequired;

            float bottomY = Screen.height - U(90f);
            float controlH = U(58f);
            float gap = U(12f);
            float left = U(70f);
            float right = Screen.width - U(70f);
            bool showHmdButton = Application.platform == RuntimePlatform.Android;
            int slotCount = showHmdButton ? 4 : 3;
            float slotWidth = (right - left - gap * (slotCount - 1)) / slotCount;

            Rect startButtonRect = new Rect(left, bottomY, slotWidth, controlH);
            Rect songButtonRect = new Rect(startButtonRect.xMax + gap, bottomY, slotWidth, controlH);
            Rect hmdButtonRect = new Rect(songButtonRect.xMax + gap, bottomY, slotWidth, controlH);
            Rect speedPanelRect = new Rect((showHmdButton ? hmdButtonRect.xMax : songButtonRect.xMax) + gap, bottomY, slotWidth, controlH);

            GUI.enabled = canStart;
            if (GUI.Button(startButtonRect, "Iniciar jogo", buttonStyle))
            {
                StartGameplay();
            }

            GUI.enabled = true;
            if (GUI.Button(songButtonRect, "Trocar musica", buttonStyle))
            {
                state = GameState.SongSelect;
            }

            if (showHmdButton)
            {
                string hmdLabel = hmdStereoPreviewEnabled ? "Sair do modo HMD" : "Entrar modo HMD";
                if (GUI.Button(hmdButtonRect, hmdLabel, buttonStyle))
                {
                    if (hmdStereoPreviewEnabled)
                    {
                        ExitHmdMode();
                    }
                    else
                    {
                        RequestHmdMode();
                    }
                }
            }

            DrawRect(speedPanelRect, new Color(0.05f, 0.05f, 0.05f, 0.78f));
            DrawOutline(speedPanelRect, new Color(0.72f, 0.72f, 0.72f, 0.92f), 1f);
            GUI.Label(
                new Rect(speedPanelRect.x + U(10f), speedPanelRect.y + U(4f), speedPanelRect.width - U(20f), U(20f)),
                $"Velocidade: {songSpeed:0.00}x",
                textStyle);

            float speedButtonY = speedPanelRect.y + U(28f);
            float speedButtonW = U(52f);
            float speedButtonH = U(24f);
            if (GUI.Button(new Rect(speedPanelRect.x + U(10f), speedButtonY, speedButtonW, speedButtonH), "-", buttonStyle))
            {
                songSpeed = Mathf.Clamp(songSpeed - 0.1f, 0.5f, 2f);
            }

            if (GUI.Button(new Rect(speedPanelRect.xMax - U(10f) - speedButtonW, speedButtonY, speedButtonW, speedButtonH), "+", buttonStyle))
            {
                songSpeed = Mathf.Clamp(songSpeed + 0.1f, 0.5f, 2f);
            }
        }

        private void DrawSettingsToggleButton()
        {
            Rect buttonRect = new Rect(Screen.width - U(110f), U(16f), U(92f), U(50f));
            if (GUI.Button(buttonRect, "MENU", iconButtonStyle))
            {
                ResetToMenu();
                if (midiPathKeyboard != null)
                {
                    midiPathKeyboard.active = false;
                    midiPathKeyboard = null;
                }
            }
        }

        private void ShowMidiImportNotification(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            midiImportNotification = message;
            midiImportNotificationUntil = Time.realtimeSinceStartup + MidiImportNotificationDurationSeconds;
        }

        private void DismissMidiImportNotification()
        {
            midiImportNotification = string.Empty;
            midiImportNotificationUntil = 0f;
        }

        private void DrawMidiImportNotification()
        {
            if (string.IsNullOrWhiteSpace(midiImportNotification))
            {
                return;
            }

            if (Time.realtimeSinceStartup >= midiImportNotificationUntil)
            {
                DismissMidiImportNotification();
                return;
            }

            float panelWidth = Mathf.Min(U(860f), Screen.width - U(180f));
            Rect panel = new Rect((Screen.width - panelWidth) * 0.5f, U(96f), panelWidth, U(56f));
            DrawRect(panel, new Color(0.05f, 0.24f, 0.08f, 0.92f));
            DrawOutline(panel, new Color(0.58f, 0.95f, 0.62f, 0.9f), 1f);

            GUI.Label(new Rect(panel.x + U(14f), panel.y + U(10f), panel.width - U(90f), U(36f)), midiImportNotification, textStyle);
            if (GUI.Button(new Rect(panel.xMax - U(64f), panel.y + U(8f), U(50f), U(40f)), "X", buttonStyle))
            {
                DismissMidiImportNotification();
            }
        }

        private void DrawSettingsOverlay()
        {
            Rect backdrop = new Rect(0f, 0f, Screen.width, Screen.height);
            DrawRect(backdrop, new Color(0f, 0f, 0f, 0.5f));

            float panelWidth = Mathf.Min(U(980f), Screen.width - U(40f));
            float panelHeight = Mathf.Min(U(760f), Screen.height - U(54f));
            Rect panel = new Rect((Screen.width - panelWidth) * 0.5f, U(40f), panelWidth, panelHeight);
            DrawRect(panel, new Color(0.07f, 0.07f, 0.07f, 0.96f));
            DrawOutline(panel, new Color(0.55f, 0.55f, 0.55f, 0.9f), 1f);

            GUI.Label(new Rect(panel.x + U(20f), panel.y + U(14f), panel.width - U(180f), U(34f)), "Configuracoes", headerStyle);
            if (GUI.Button(new Rect(panel.xMax - U(110f), panel.y + U(10f), U(90f), U(40f)), "Fechar", buttonStyle))
            {
                showSettingsOverlay = false;
            }

            float tabY = panel.y + U(58f);
            float tabWidth = U(160f);
            DrawSettingsTab(panel.x + U(16f), tabY, tabWidth, "MIDI", SettingsSection.Midi);
            DrawSettingsTab(panel.x + U(182f), tabY, tabWidth, "Camera", SettingsSection.Camera);
            DrawSettingsTab(panel.x + U(348f), tabY, tabWidth, "Deteccao", SettingsSection.Detection);
            DrawSettingsTab(panel.x + U(514f), tabY, tabWidth, "Gameplay", SettingsSection.Gameplay);
            DrawSettingsTab(panel.x + U(680f), tabY, tabWidth, "Debug", SettingsSection.Diagnostics);

            Rect scrollRect = new Rect(panel.x + U(16f), panel.y + U(104f), panel.width - U(32f), panel.height - U(124f));
            Rect viewRect = new Rect(0f, 0f, scrollRect.width - U(28f), U(900f));
            settingsScrollPos = GUI.BeginScrollView(scrollRect, settingsScrollPos, viewRect);

            float y = U(8f);
            switch (settingsSection)
            {
                case SettingsSection.Midi:
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), "Repositorio MIDI", headerStyle);
                    y += U(42f);

                    bool isAndroidRuntime = Application.platform == RuntimePlatform.Android;
                    bool isMobile = Application.isMobilePlatform;

                    if (isAndroidRuntime)
                    {
                        DrawTextBox(new Rect(U(8f), y, viewRect.width - U(20f), U(62f)),
                            "Android usa somente a pasta interna do app.\nUse Importar MIDI para copiar arquivos para essa pasta.",
                            new Color(0.12f, 0.12f, 0.12f, 0.85f));
                        y += U(74f);

                        if (GUI.Button(new Rect(U(8f), y, U(240f), U(46f)), "Importar MIDI", buttonStyle))
                        {
                            ImportMidiOnAndroid();
                        }

                        if (GUI.Button(new Rect(U(258f), y, U(220f), U(46f)), "Recarregar MIDIs", buttonStyle))
                        {
                            LoadMidiList();
                        }
                    }
                    else
                    {
                        if (isMobile)
                        {
                            DrawTextBox(new Rect(U(8f), y, viewRect.width - U(320f), U(52f)), midiDirectoryInput ?? string.Empty, new Color(0.12f, 0.12f, 0.12f, 0.85f));
                            if (GUI.Button(new Rect(viewRect.width - U(304f), y + U(4f), U(144f), U(44f)), "Editar", buttonStyle))
                            {
                                BeginMidiPathKeyboardEdit();
                            }
                        }
                        else
                        {
                            midiDirectoryInput = GUI.TextField(new Rect(U(8f), y, viewRect.width - U(180f), U(44f)), midiDirectoryInput ?? string.Empty);
                        }

                        if (GUI.Button(new Rect(viewRect.width - U(160f), y + (isMobile ? U(4f) : 0f), U(152f), U(44f)), "Aplicar", buttonStyle))
                        {
                            ApplyMidiRepository(midiDirectoryInput);
                        }

                        y += U(56f);
                        if (GUI.Button(new Rect(U(8f), y, U(220f), U(46f)), "Recarregar MIDIs", buttonStyle))
                        {
                            LoadMidiList();
                        }
                    }

                    y += U(58f);
                    DrawTextBox(new Rect(U(8f), y, viewRect.width - U(20f), U(52f)), "Pasta ativa: " + ResolveMidiRoot(), new Color(0.12f, 0.12f, 0.12f, 0.85f));
                    y += U(66f);
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(32f)), $"MIDIs encontrados: {midiFiles.Count}", textStyle);
                    break;

                case SettingsSection.Camera:
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), "Camera", headerStyle);
                    y += U(44f);
                    GUI.Label(new Rect(U(8f), y, U(380f), U(30f)), GetSelectedCameraLabel(), textStyle);
                    if (GUI.Button(new Rect(U(400f), y - U(4f), U(60f), U(38f)), "<", buttonStyle))
                    {
                        SelectCameraRelative(-1);
                    }

                    if (GUI.Button(new Rect(U(468f), y - U(4f), U(60f), U(38f)), ">", buttonStyle))
                    {
                        SelectCameraRelative(1);
                    }

                    if (GUI.Button(new Rect(U(536f), y - U(4f), U(192f), U(38f)), "Atualizar lista", buttonStyle))
                    {
                        RefreshCameraSelectionState();
                    }

                    y += U(48f);
                    GUI.Label(new Rect(U(8f), y, U(360f), U(30f)), "Resolucao/FPS:", textStyle);
                    GUI.Label(new Rect(U(220f), y, U(380f), U(30f)), GetSelectedModeLabel(), textStyle);
                    if (GUI.Button(new Rect(U(600f), y - U(4f), U(60f), U(38f)), "<", buttonStyle))
                    {
                        SelectModeRelative(-1);
                    }

                    if (GUI.Button(new Rect(U(668f), y - U(4f), U(60f), U(38f)), ">", buttonStyle))
                    {
                        SelectModeRelative(1);
                    }

                    y += U(50f);
                    if (GUI.Button(new Rect(U(8f), y, U(260f), U(46f)), "Aplicar camera", buttonStyle))
                    {
                        ApplySelectedCameraAndMode();
                    }

                    GUI.Label(new Rect(U(282f), y + U(6f), viewRect.width - U(292f), U(34f)), GetCameraStatusLabel(), textStyle);
                    break;

                case SettingsSection.Detection:
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), "Ajustes de deteccao", headerStyle);
                    y += U(46f);
                    DrawSliderRow(ref y, viewRect.width, "Confianca minima", ref confThreshold, 0.05f, 0.95f, 0.01f);
                    DrawSliderRow(ref y, viewRect.width, "IoU threshold", ref iouThreshold, 0.05f, 0.95f, 0.01f);
                    if (GUI.Button(new Rect(U(8f), y, U(320f), U(40f)), "Alternar backend: " + backendType, buttonStyle))
                    {
                        backendType = backendType == Unity.InferenceEngine.BackendType.CPU ? Unity.InferenceEngine.BackendType.GPUCompute : Unity.InferenceEngine.BackendType.CPU;
                        modelInitAttempted = false;
                        if (worker != null)
                        {
                            worker.Dispose();
                            worker = null;
                        }
                    }

                    y += U(50f);

                    settingsShowAdvancedDetection = GUI.Toggle(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), settingsShowAdvancedDetection, "Mostrar ajustes avancados");
                    y += U(42f);

                    if (settingsShowAdvancedDetection)
                    {
                        DrawIntStepperRow(ref y, viewRect.width, "Intervalo de inferencia", ref detectInterval, 1, 20, 1);
                        DrawIntStepperRow(ref y, viewRect.width, "Frames estaveis para liberar jogo", ref stableHitsRequired, 1, 100, 1);
                        DrawIntStepperRow(ref y, viewRect.width, "Input fallback do modelo", ref fallbackInputSize, 128, 1280, 32);
                        DrawIntStepperRow(ref y, viewRect.width, "Numero de classes", ref numClasses, 1, 8, 1);
                    }
                    break;

                case SettingsSection.Gameplay:
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), "Gameplay", headerStyle);
                    y += U(46f);
                    DrawSliderRow(ref y, viewRect.width, "Velocidade da musica", ref songSpeed, 0.5f, 2f, 0.05f);
                    DrawSliderRow(ref y, viewRect.width, "Contagem inicial (s)", ref countdownSeconds, 1f, 8f, 0.25f);
                    DrawSliderRow(ref y, viewRect.width, "Tempo de viagem das notas", ref travelTime, 0.5f, 4f, 0.1f);
                    enableHmdModeOnGameStart = GUI.Toggle(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), enableHmdModeOnGameStart, "Ativar HMD automaticamente ao iniciar o jogo");
                    y += U(42f);
                    autoStartFirst = GUI.Toggle(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), autoStartFirst, "Auto iniciar primeira musica no boot");
                    break;

                case SettingsSection.Diagnostics:
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), "Diagnostico", headerStyle);
                    y += U(46f);
                    dumpInferenceArtifacts = GUI.Toggle(new Rect(U(8f), y, viewRect.width - U(20f), U(34f)), dumpInferenceArtifacts, "Salvar dumps de inferencia");
                    y += U(42f);
                    DrawIntStepperRow(ref y, viewRect.width, "Limite de dumps", ref dumpInferenceArtifactLimit, 1, 20, 1);
                    DrawTextBox(new Rect(U(8f), y, viewRect.width - U(20f), U(54f)), "Pasta de dump: " + EnsureDumpDirectory(), new Color(0.12f, 0.12f, 0.12f, 0.85f));
                    y += U(66f);
                    GUI.Label(new Rect(U(8f), y, viewRect.width - U(20f), U(40f)), "Backend atual: " + backendType, textStyle);
                    break;
            }

            GUI.EndScrollView();
        }

        private void BeginMidiPathKeyboardEdit()
        {
            if (!Application.isMobilePlatform)
            {
                return;
            }

            string current = midiDirectoryInput ?? string.Empty;
            midiPathKeyboard = TouchScreenKeyboard.Open(current, TouchScreenKeyboardType.Default, false, false, false, false, "Pasta MIDI");
        }

        private void DrawMobileMidiPathKeyboardOverlay()
        {
            if (!Application.isMobilePlatform || midiPathKeyboard == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(midiPathKeyboard.text))
            {
                midiDirectoryInput = midiPathKeyboard.text;
            }

            TouchScreenKeyboard.Status status = midiPathKeyboard.status;
            bool isCompleted = status == TouchScreenKeyboard.Status.Done || status == TouchScreenKeyboard.Status.Canceled;
            bool isActive = midiPathKeyboard.active;

            Rect panel = new Rect(U(16f), U(90f), Screen.width - U(32f), U(190f));
            DrawRect(panel, new Color(0.03f, 0.03f, 0.03f, 0.95f));
            DrawOutline(panel, new Color(0.6f, 0.6f, 0.6f, 0.9f), 1f);
            GUI.Label(new Rect(panel.x + U(12f), panel.y + U(10f), panel.width - U(24f), U(28f)), "Editando caminho MIDI", headerStyle);
            DrawTextBox(new Rect(panel.x + U(10f), panel.y + U(42f), panel.width - U(20f), U(86f)), midiDirectoryInput ?? string.Empty, new Color(0.12f, 0.12f, 0.12f, 0.9f));

            if (GUI.Button(new Rect(panel.x + U(10f), panel.yMax - U(50f), U(150f), U(38f)), "Aplicar", buttonStyle))
            {
                ApplyMidiRepository(midiDirectoryInput);
                if (midiPathKeyboard != null)
                {
                    midiPathKeyboard.active = false;
                }

                midiPathKeyboard = null;
            }

            if (GUI.Button(new Rect(panel.x + U(170f), panel.yMax - U(50f), U(150f), U(38f)), "Fechar", buttonStyle))
            {
                if (midiPathKeyboard != null)
                {
                    midiPathKeyboard.active = false;
                }

                midiPathKeyboard = null;
            }

            if (!isActive && isCompleted)
            {
                midiPathKeyboard = null;
            }
        }

        private void DrawSettingsTab(float x, float y, float width, string label, SettingsSection section)
        {
            GUIStyle style = settingsSection == section ? selectedRowStyle : rowStyle;
            if (GUI.Button(new Rect(x, y, width, U(38f)), label, style))
            {
                settingsSection = section;
                settingsScrollPos = Vector2.zero;
            }
        }

        private void DrawSliderRow(ref float y, float width, string label, ref float value, float min, float max, float step)
        {
            GUI.Label(new Rect(U(8f), y, U(360f), U(30f)), $"{label}: {value:0.00}", textStyle);
            if (GUI.Button(new Rect(U(378f), y - U(4f), U(54f), U(38f)), "-", buttonStyle))
            {
                value = Mathf.Clamp(value - step, min, max);
            }

            if (GUI.Button(new Rect(U(438f), y - U(4f), U(54f), U(38f)), "+", buttonStyle))
            {
                value = Mathf.Clamp(value + step, min, max);
            }

            value = GUI.HorizontalSlider(new Rect(U(500f), y + U(8f), width - U(520f), U(26f)), value, min, max);
            y += U(44f);
        }

        private void DrawIntStepperRow(ref float y, float width, string label, ref int value, int min, int max, int step)
        {
            GUI.Label(new Rect(U(8f), y, width - U(180f), U(30f)), $"{label}: {value}", textStyle);
            if (GUI.Button(new Rect(width - U(162f), y - U(4f), U(70f), U(38f)), "-", buttonStyle))
            {
                value = Mathf.Clamp(value - step, min, max);
            }

            if (GUI.Button(new Rect(width - U(84f), y - U(4f), U(70f), U(38f)), "+", buttonStyle))
            {
                value = Mathf.Clamp(value + step, min, max);
            }

            y += U(44f);
        }

        private void DrawGameplay()
        {
            if (!keyboardArea.HasValue)
            {
                DrawTextBox(new Rect(U(40f), U(120f), U(680f), U(42f)), "Perdi o teclado. Reposicione e aguarde redeteccao.", new Color(0.05f, 0.2f, 0.35f, 0.68f));
                return;
            }

            Rect area = MapFrameRectToScreen(keyboardArea.Value);
            DrawKeyboardRect(area, new Color(0f, 0.67f, 1f, 1f));

            float strikeY = area.y + 0.86f * area.height;
            float spawnY = Mathf.Clamp(area.y - 0.32f * Screen.height, 16f, area.y - 40f);
            DrawRect(new Rect(area.x, strikeY, area.width, 2f), new Color(0.27f, 0.86f, 1f, 0.9f));

            float t = UnityEngine.Time.realtimeSinceStartup - gameStartTime - countdownSeconds;
            if (t < 0f)
            {
                int count = Mathf.CeilToInt(-t);
                DrawTextBox(new Rect(U(40f), U(120f), U(460f), U(42f)), $"Prepare-se... {count}", new Color(0.08f, 0.08f, 0.08f, 0.65f));
                DrawTextBox(new Rect(U(40f), U(166f), U(540f), U(34f)), $"Inicio em {countdownSeconds:0}s | Velocidade {songSpeed:0.00}x", new Color(0.08f, 0.08f, 0.08f, 0.65f));
                return;
            }

            float musicT = t * songSpeed;
            float visualT = musicT - travelTime;
            float windowStart = visualT - 0.2f;
            float windowEnd = visualT + travelTime + 0.2f;

            for (int i = 0; i < events.Count; i++)
            {
                MidiNoteEvent ev = events[i];
                if (ev.end < windowStart || ev.start > windowEnd)
                {
                    continue;
                }

                float x = PitchToX(ev.pitch, area);
                float approach = (visualT - (ev.start - travelTime)) / Mathf.Max(0.001f, travelTime);
                float y = Mathf.Lerp(spawnY, strikeY, Mathf.Clamp01(approach));

                float durPx = Mathf.Clamp((ev.end - ev.start) * 140f, 20f, 300f);
                float yTop = y - durPx;
                bool black = IsBlackKey(ev.pitch);
                float half = black ? 4f : 9f;

                Color border;
                Color fill;
                if (ev.hand == 'R')
                {
                    border = new Color(0.27f, 0.9f, 0.35f, 1f);
                    fill = black ? new Color(0.08f, 0.27f, 0.08f, 0.9f) : new Color(0.72f, 0.95f, 0.72f, 0.92f);
                }
                else
                {
                    border = new Color(1f, 0.47f, 0.16f, 1f);
                    fill = black ? new Color(0.35f, 0.14f, 0.07f, 0.9f) : new Color(1f, 0.83f, 0.67f, 0.93f);
                }

                Rect noteRect = new Rect(x - half, yTop, 2f * half, Mathf.Max(1f, y - yTop));
                DrawRect(noteRect, fill);
                DrawOutline(noteRect, border, 1f);

                if (Mathf.Abs(ev.start - musicT) <= 0.08f)
                {
                    DrawOutline(new Rect(x - 12f, strikeY - 12f, 24f, 24f), Color.yellow, 2f);
                }
            }

            float progress = Mathf.Clamp01(musicT / Mathf.Max(0.001f, songDuration));
            Rect progressBar = new Rect(U(40f), Screen.height - U(48f), Screen.width - U(80f), U(24f));
            DrawRect(progressBar, new Color(0.14f, 0.14f, 0.14f, 0.88f));
            DrawRect(new Rect(progressBar.x, progressBar.y, progressBar.width * progress, progressBar.height), new Color(0.12f, 0.72f, 0.24f, 0.95f));
            DrawOutline(progressBar, Color.white, 1f);

            float remaining = Mathf.Max(0f, songDuration - musicT);
            DrawTextBox(new Rect(U(40f), U(120f), U(640f), U(36f)), $"Musica: {songName}", new Color(0.08f, 0.08f, 0.08f, 0.65f));
            DrawTextBox(new Rect(U(40f), U(160f), U(700f), U(34f)), $"Tempo restante: {remaining:0.0}s | Velocidade: {songSpeed:0.00}x", new Color(0.08f, 0.08f, 0.08f, 0.65f));

            if (musicT > songDuration + 0.5f)
            {
                state = GameState.End;
            }
        }

        private void DrawEnd()
        {
            Rect panel = new Rect(U(80f), U(140f), Screen.width - U(160f), Screen.height - U(260f));
            DrawRect(panel, new Color(0.05f, 0.05f, 0.05f, 0.72f));

            GUI.Label(new Rect(U(120f), U(220f), U(560f), U(40f)), "Musica finalizada!", headerStyle);
            GUI.Label(new Rect(U(120f), U(260f), U(760f), U(30f)), songName, textStyle);

            if (GUI.Button(new Rect(U(120f), Screen.height - U(190f), U(260f), U(58f)), "Voltar ao menu", buttonStyle))
            {
                ResetToMenu();
            }

            if (GUI.Button(new Rect(U(400f), Screen.height - U(190f), U(260f), U(58f)), "Jogar novamente", buttonStyle))
            {
                state = GameState.Align;
                stableHits = 0;
            }
        }

        private void DrawGuideBox()
        {
            float guideW = Screen.width * 0.7f;
            float guideH = Screen.height * 0.22f;
            float gx = (Screen.width - guideW) * 0.5f;
            float gy = Screen.height * 0.55f;
            DrawOutline(new Rect(gx, gy, guideW, guideH), new Color(0.35f, 0.35f, 0.35f, 1f), 1f);
            DrawTextBox(new Rect(gx + U(10f), gy - U(40f), U(520f), U(34f)), "Centralize o teclado real dentro da caixa", new Color(0.08f, 0.08f, 0.08f, 0.65f));
        }

        private void DrawKeyboardRect(Rect area, Color color)
        {
            DrawOutline(area, color, 2f);
            for (int i = 1; i < 16; i++)
            {
                float x = area.x + area.width * (i / 16f);
                DrawRect(new Rect(x, area.y, 1f, area.height), new Color(0.16f, 0.31f, 0.43f, 0.75f));
            }
        }

        private Rect MapFrameRectToScreen(Rect frameRect)
        {
            GetActiveFrameSize(out int frameWidth, out int frameHeight);
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return frameRect;
            }

            float scaleX = Screen.width / (float)frameWidth;
            float scaleY = Screen.height / (float)frameHeight;
            float mappedX = frameRect.x * scaleX;
            float mappedY = (frameHeight - frameRect.yMax) * scaleY;
            return new Rect(mappedX, mappedY, frameRect.width * scaleX, frameRect.height * scaleY);
        }

        private void GetActiveFrameSize(out int width, out int height)
        {
            if (frameTexture != null)
            {
                width = frameTexture.width;
                height = frameTexture.height;
                return;
            }

            if (latestCorrectedFrameWidth > 0 && latestCorrectedFrameHeight > 0)
            {
                width = latestCorrectedFrameWidth;
                height = latestCorrectedFrameHeight;
                return;
            }

            if (webcam != null)
            {
                width = Mathf.Max(1, webcam.width);
                height = Mathf.Max(1, webcam.height);
                return;
            }

            width = Screen.width;
            height = Screen.height;
        }

        private void DrawRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, whitePixel);
            GUI.color = previous;
        }

        private void DrawOutline(Rect rect, Color color, float thickness)
        {
            DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private void DrawTextBox(Rect rect, string text, Color bgColor)
        {
            DrawRect(rect, bgColor);
            GUI.Label(new Rect(rect.x + U(8f), rect.y + U(4f), rect.width - U(16f), rect.height - U(8f)), text, textStyle);
        }

        private void HandleMenuKeyboardFallback()
        {
            float rowHeight = U(52f);
            if (IsKeyPressed(KeyCode.UpArrow) || IsKeyPressed(KeyCode.K))
            {
                menuScroll = Mathf.Max(0, menuScroll - 1);
                menuScrollPos.y = Mathf.Max(0f, menuScroll * rowHeight);
            }
            else if (IsKeyPressed(KeyCode.DownArrow) || IsKeyPressed(KeyCode.J))
            {
                menuScroll = Mathf.Min(Mathf.Max(0, midiFiles.Count - 1), menuScroll + 1);
                menuScrollPos.y = menuScroll * rowHeight;
            }
            else if (IsKeyPressed(KeyCode.PageUp))
            {
                menuScroll = Mathf.Max(0, menuScroll - 5);
                menuScrollPos.y = Mathf.Max(0f, menuScroll * rowHeight);
            }
            else if (IsKeyPressed(KeyCode.PageDown))
            {
                menuScroll = Mathf.Min(Mathf.Max(0, midiFiles.Count - 1), menuScroll + 5);
                menuScrollPos.y = menuScroll * rowHeight;
            }
        }

        private void HandleMouseWheelMenuScroll()
        {
            Event current = Event.current;
            if (current == null || current.type != UnityEngine.EventType.ScrollWheel)
            {
                return;
            }

            float wheel = -current.delta.y;
            if (Mathf.Abs(wheel) <= 0.01f)
            {
                return;
            }

            menuScrollPos.y = Mathf.Max(0f, menuScrollPos.y - wheel * U(54f));
            current.Use();
        }
    }
}
