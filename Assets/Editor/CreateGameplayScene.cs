using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using PianoARGame.AR;
using PianoARGame.Services;
using PianoARGame.Gameplay;

/// <summary>
/// Gera a cena Assets/Scenes/Gameplay.unity com todos os sistemas de gameplay ligados.
/// Menu: PianoAR/Create Gameplay Scene
/// </summary>
public static class CreateGameplayScene
{
    private const string ScenePath = "Assets/Scenes/Gameplay.unity";

    [MenuItem("PianoAR/Create Gameplay Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- Câmera ---
        var cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        var cam = cameraGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.1f);
        cameraGo.AddComponent<AudioListener>();
        cameraGo.transform.position = new Vector3(0f, 1.4f, -0.5f);
        cameraGo.transform.eulerAngles = new Vector3(15f, 0f, 0f);

        // --- Luz ambiente ---
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 0.8f;
        lightGo.transform.eulerAngles = new Vector3(45f, -30f, 0f);

        // --- Serviços ---
        var configGo = new GameObject("ConfigService");
        var configSvc = configGo.AddComponent<ConfigService>();

        var midiSvcGo = new GameObject("MidiService");
        var midiSvc = midiSvcGo.AddComponent<MidiService>();
        midiSvc.configService = configSvc;

        // --- AR ---
        var detectorGo = new GameObject("PianoDetector");
        var detector = detectorGo.AddComponent<PianoDetector>();

        var webcamGo = new GameObject("TestWebcamController");
        var webcam = webcamGo.AddComponent<TestWebcamController>();
        webcam.detector = detector;
        webcam.configService = configSvc;

        var arSessionGo = new GameObject("ARSessionManager");
        var arSession = arSessionGo.AddComponent<ARSessionManager>();
        arSession.useWebcamInEditor = true;
        arSession.webcamController = webcam;

        var estimatorGo = new GameObject("KeyEstimator");
        var estimator = estimatorGo.AddComponent<KeyEstimator>();

        // --- Gameplay ---
        var keyboardRootGo = new GameObject("KeyboardRoot");
        keyboardRootGo.transform.position = Vector3.zero;

        var spawnGo = new GameObject("SpawnManager");
        var spawnMgr = spawnGo.AddComponent<SpawnManager>();
        SerializedObjectSet(spawnMgr, "keyboardRoot", keyboardRootGo.transform);

        var hitDetectorGo = new GameObject("KeyHitDetector");
        var hitDetector = hitDetectorGo.AddComponent<KeyHitDetector>();

        var trailGo = new GameObject("TrailRendererAR");
        var trailMgr = trailGo.AddComponent<TrailRendererAR>();
        SerializedObjectSet(trailMgr, "keyboardRoot", keyboardRootGo.transform);

        var scoreMgrGo = new GameObject("ScoreManager");
        var scoreMgr = scoreMgrGo.AddComponent<ScoreManager>();

        // --- GameplayController (liga tudo) ---
        var ctrlGo = new GameObject("GameplayController");
        var ctrl = ctrlGo.AddComponent<GameplayController>();
        SerializedObjectSet(ctrl, "arSessionManager", arSession);
        SerializedObjectSet(ctrl, "pianoDetector", detector);
        SerializedObjectSet(ctrl, "keyEstimator", estimator);
        SerializedObjectSet(ctrl, "webcamController", webcam);
        SerializedObjectSet(ctrl, "configService", configSvc);
        SerializedObjectSet(ctrl, "spawnManager", spawnMgr);
        SerializedObjectSet(ctrl, "keyHitDetector", hitDetector);
        SerializedObjectSet(ctrl, "trailRenderer", trailMgr);
        SerializedObjectSet(ctrl, "scoreManager", scoreMgr);

        // --- Canvas UI ---
        EnsureEventSystem();
        BuildGameplayCanvas(ctrl, cam);

        // --- Salvar ---
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.Refresh();
        Debug.Log($"[PianoAR] Cena Gameplay criada em {ScenePath}");
    }

    // -----------------------------------------------------------------------
    // UI
    // -----------------------------------------------------------------------

    private static void BuildGameplayCanvas(GameplayController ctrl, Camera cam)
    {
        var canvasGo = new GameObject("GameplayCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();

        // Score
        var scoreText  = CreateText(canvasGo.transform, "ScoreText",  "Score: 0", 36, TextAnchor.UpperLeft,
            new Vector2(20, -20), new Vector2(400, 60));

        // Combo
        var comboText  = CreateText(canvasGo.transform, "ComboText",  "", 30, TextAnchor.UpperCenter,
            new Vector2(0, -80), new Vector2(400, 50));

        // Feedback ("Perfect", "Good", "Miss")
        var feedbackText = CreateText(canvasGo.transform, "FeedbackText", "", 48, TextAnchor.MiddleCenter,
            new Vector2(0, 100), new Vector2(500, 80));
        feedbackText.color = new Color(1f, 0.9f, 0.2f);
        feedbackText.fontStyle = FontStyle.Bold;

        // Status
        var statusText = CreateText(canvasGo.transform, "StatusText", "Pronto", 22, TextAnchor.LowerLeft,
            new Vector2(20, 20), new Vector2(700, 50));
        statusText.color = new Color(0.7f, 0.9f, 1f);

        // Botão Start
        var startBtn = CreateButton(canvasGo.transform, "StartButton", "Iniciar", new Vector2(-160, -50));
        startBtn.onClick.AddListener(ctrl.StartGameplay);

        // Botão Stop
        var stopBtn = CreateButton(canvasGo.transform, "StopButton", "Parar", new Vector2(0, -50));
        stopBtn.onClick.AddListener(ctrl.StopGameplay);

        // Inject text refs
        SerializedObjectSet(ctrl, "scoreText",    scoreText);
        SerializedObjectSet(ctrl, "comboText",    comboText);
        SerializedObjectSet(ctrl, "feedbackText", feedbackText);
        SerializedObjectSet(ctrl, "statusText",   statusText);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void EnsureEventSystem()
    {
        if (Object.FindAnyObjectByType<EventSystem>() == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            esGo.AddComponent<InputSystemUIInputModule>();
#else
            esGo.AddComponent<StandaloneInputModule>();
#endif
        }
    }

    private static Text CreateText(Transform parent, string name, string defaultText,
        int fontSize, TextAnchor anchor, Vector2 anchoredPos, Vector2 sizeDelta)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = sizeDelta;
        var txt = go.AddComponent<Text>();
        txt.text = defaultText;
        txt.fontSize = fontSize;
        txt.alignment = anchor;
        txt.color = Color.white;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.resizeTextForBestFit = false;
        return txt;
    }

    private static Button CreateButton(Transform parent, string name, string label, Vector2 anchoredPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(160f, 55f);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.15f, 0.45f, 0.85f);
        var btn = go.AddComponent<Button>();

        var txtGo = new GameObject("Label");
        txtGo.transform.SetParent(go.transform, false);
        var txtRt = txtGo.AddComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = txtRt.offsetMax = Vector2.zero;
        var txt = txtGo.AddComponent<Text>();
        txt.text = label;
        txt.fontSize = 24;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color = Color.white;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        return btn;
    }

    /// <summary>
    /// Define um campo serializado usando SerializedObject para que a cena salve a referência.
    /// </summary>
    private static void SerializedObjectSet(Object target, string fieldName, Object value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(fieldName);
        if (prop != null)
        {
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
