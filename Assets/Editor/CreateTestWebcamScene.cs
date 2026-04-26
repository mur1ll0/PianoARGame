using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using PianoARGame.AR;
using PianoARGame.Services;

public static class CreateTestWebcamScene
{
    [MenuItem("PianoAR/Create Test Webcam Scene")]
    public static void CreateScene()
    {
        // Ensure Scenes folder exists
        var scenesFolder = "Assets/Scenes";
        if (!System.IO.Directory.Exists(scenesFolder))
            System.IO.Directory.CreateDirectory(scenesFolder);

        // Create new scene
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "Test_Editor_Webcam";

        // Camera
        var camGO = new GameObject("Main Camera");
        var cam = camGO.AddComponent<Camera>();
        cam.tag = "MainCamera";

        // ConfigService
        var configGO = new GameObject("ConfigService");
        var config = configGO.AddComponent<ConfigService>();

        // MidiService
        var midiGO = new GameObject("MidiService");
        var midi = midiGO.AddComponent<MidiService>();
        midi.configService = config;

        // PianoDetector
        var detectorGO = new GameObject("PianoDetector");
        var detector = detectorGO.AddComponent<PianoDetector>();

        // TestWebcamController
        var testerGO = new GameObject("TestWebcamController");
        var tester = testerGO.AddComponent<TestWebcamController>();
        tester.detector = detector;
        tester.configService = config;

        // ScoreManager
        var scoreGO = new GameObject("ScoreManager");
        scoreGO.AddComponent<PianoARGame.AR.ScoreManager>();

        // UIManager (empty placeholder)
        var uiGO = new GameObject("UIManager");
        uiGO.AddComponent<PianoARGame.AR.UIManager>();

        // Save scene
        var scenePath = "Assets/Scenes/Test_Editor_Webcam.unity";
        EditorSceneManager.SaveScene(scene, scenePath);

        Debug.Log("Test_Editor_Webcam scene created at " + scenePath + ". Open it from the Project window.");
    }
}
