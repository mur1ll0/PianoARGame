using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using PianoARGame.AR;
using PianoARGame.Services;
using PianoARGame.UI;

public static class CreateHmdBaseScene
{
    private const string ScenePath = "Assets/Scenes/HMD_Base.unity";

    [MenuItem("PianoAR/Create HMD Base Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var rig = new GameObject("HMD_Rig");
        var cameraGo = new GameObject("Main Camera");
        cameraGo.transform.SetParent(rig.transform, false);
        cameraGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        cameraGo.tag = "MainCamera";
        cameraGo.AddComponent<Camera>();
        cameraGo.AddComponent<AudioListener>();

        var configGo = new GameObject("ConfigService");
        var config = configGo.AddComponent<ConfigService>();

        var midiGo = new GameObject("MidiService");
        var midi = midiGo.AddComponent<MidiService>();
        midi.configService = config;

        var detectorGo = new GameObject("PianoDetector");
        var detector = detectorGo.AddComponent<PianoDetector>();

        var webcamGo = new GameObject("TestWebcamController");
        var webcam = webcamGo.AddComponent<TestWebcamController>();
        webcam.detector = detector;
        webcam.configService = config;

        var arSessionGo = new GameObject("ARSessionManager");
        var arSession = arSessionGo.AddComponent<ARSessionManager>();
        arSession.useWebcamInEditor = true;
        arSession.webcamController = webcam;

        var calibrationGo = new GameObject("CalibrationManager");
        var calibration = calibrationGo.AddComponent<CalibrationManager>();
        calibration.detector = detector;
        calibration.webcamController = webcam;
        calibration.configService = config;

        var trackerGo = new GameObject("KeyboardTracker");
        trackerGo.AddComponent<KeyboardTracker>();

        var uiManagerGo = new GameObject("UIManager");
        var uiManager = uiManagerGo.AddComponent<UIManager>();
        uiManager.calibrationManager = calibration;
        uiManager.arSessionManager = arSession;
        uiManager.webcamController = webcam;
        uiManager.configService = config;

        EnsureEventSystem();

        var canvasGo = new GameObject("HMD_UI");
        canvasGo.transform.position = new Vector3(0f, 1.45f, 1.25f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        var rectTransform = canvasGo.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(900f, 520f);
        rectTransform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);

        var panel = CreatePanel(canvasGo.transform, "Panel");
        var statusText = CreateText(panel.transform, "StatusText", "PianoARGame HMD Base", 26, TextAnchor.UpperLeft);

        CreateButton(panel.transform, "Detect Piano", uiManager.OnDetectPianoClicked);
        CreateButton(panel.transform, "Start Session", uiManager.OnStartSessionClicked);
        CreateButton(panel.transform, "Stop Session", uiManager.OnStopSessionClicked);
        CreateButton(panel.transform, "Toggle Camera View", uiManager.OnToggleFullscreenClicked);
        CreateButton(panel.transform, "Show Music Folder", uiManager.OnOpenMusicFolderClicked);

        var hud = panel.AddComponent<HmdHudController>();
        hud.uiManager = uiManager;
        hud.calibrationManager = calibration;
        hud.webcamController = webcam;
        hud.statusText = statusText;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("Created scene: " + ScenePath);
    }

    private static void EnsureEventSystem()
    {
        if (Object.FindAnyObjectByType<EventSystem>() != null)
        {
            return;
        }

        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
    #if ENABLE_INPUT_SYSTEM
        es.AddComponent<InputSystemUIInputModule>();
    #else
        es.AddComponent<StandaloneInputModule>();
    #endif
    }

    private static GameObject CreatePanel(Transform parent, string name)
    {
        var panel = new GameObject(name);
        panel.transform.SetParent(parent, false);

        var image = panel.AddComponent<Image>();
        image.color = new Color(0.08f, 0.10f, 0.14f, 0.88f);

        var rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(20f, 20f);
        rt.offsetMax = new Vector2(-20f, -20f);

        var layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 18, 18);
        layout.spacing = 10f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;

        var fitter = panel.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        return panel;
    }

    private static Text CreateText(Transform parent, string name, string text, int fontSize, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var textComponent = go.AddComponent<Text>();
        textComponent.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        textComponent.text = text;
        textComponent.fontSize = fontSize;
        textComponent.alignment = anchor;
        textComponent.color = new Color(0.95f, 0.98f, 1f, 1f);

        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 120f);
        return textComponent;
    }

    private static void CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction callback)
    {
        var go = new GameObject(label + "Button");
        go.transform.SetParent(parent, false);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.13f, 0.31f, 0.47f, 0.95f);

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        UnityEventTools.AddPersistentListener(button.onClick, callback);

        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 62f);

        var text = CreateText(go.transform, "Label", label, 24, TextAnchor.MiddleCenter);
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 16;
        text.resizeTextMaxSize = 26;

        var textRt = text.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;
    }
}
