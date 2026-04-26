using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PianoARGame.AR;
using PianoARGame.Services;

public static class EnsureTestSceneWired
{
    private const string scenePath = "Assets/Scenes/Test_Editor_Webcam.unity";

    [MenuItem("PianoAR/Wire Test Scene and Save")]
    public static void WireAndSave()
    {
        // If scene doesn't exist, create it using the existing creator
        if (!System.IO.File.Exists(scenePath))
        {
            CreateTestWebcamScene.CreateScene();
        }

        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        // Ensure main camera
        var camGO = GameObject.Find("Main Camera");
        if (camGO == null) camGO = new GameObject("Main Camera");
        if (camGO.GetComponent<Camera>() == null) camGO.AddComponent<Camera>();

        // ConfigService
        var configGO = GameObject.Find("ConfigService");
        if (configGO == null) configGO = new GameObject("ConfigService");
        var config = configGO.GetComponent<ConfigService>() ?? configGO.AddComponent<ConfigService>();

        // MidiService
        var midiGO = GameObject.Find("MidiService");
        if (midiGO == null) midiGO = new GameObject("MidiService");
        var midi = midiGO.GetComponent<MidiService>() ?? midiGO.AddComponent<MidiService>();
        midi.configService = config;

        // PianoDetector
        var detectorGO = GameObject.Find("PianoDetector");
        if (detectorGO == null) detectorGO = new GameObject("PianoDetector");
        var detector = detectorGO.GetComponent<PianoDetector>() ?? detectorGO.AddComponent<PianoDetector>();

        // TestWebcamController
        var testerGO = GameObject.Find("TestWebcamController");
        if (testerGO == null) testerGO = new GameObject("TestWebcamController");
        var tester = testerGO.GetComponent<TestWebcamController>() ?? testerGO.AddComponent<TestWebcamController>();
        tester.detector = detector;
        tester.configService = config;
        tester.webcamName = "Iriun";

        // ScoreManager
        var scoreGO = GameObject.Find("ScoreManager");
        if (scoreGO == null) scoreGO = new GameObject("ScoreManager");
        var score = scoreGO.GetComponent<ScoreManager>() ?? scoreGO.AddComponent<ScoreManager>();

        // UIManager
        var uiGO = GameObject.Find("UIManager");
        if (uiGO == null) uiGO = new GameObject("UIManager");
        var ui = uiGO.GetComponent<UIManager>() ?? uiGO.AddComponent<UIManager>();

        // Mark scene dirty and save
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("Wired Test_Editor_Webcam scene and saved.");
    }
}
