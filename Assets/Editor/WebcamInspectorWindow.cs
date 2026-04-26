using UnityEditor;
using UnityEngine;
using PianoARGame.AR;

public class WebcamInspectorWindow : EditorWindow
{
    private int selectedIndex = 0;
    private string[] deviceNames = new string[0];

    [MenuItem("Window/PianoAR/Webcam Inspector")]
    public static void ShowWindow()
    {
        GetWindow<WebcamInspectorWindow>("Webcam Inspector");
    }

    void OnEnable()
    {
        RefreshDevices();
    }

    void OnGUI()
    {
        if (GUILayout.Button("Refresh devices")) RefreshDevices();

        if (deviceNames.Length == 0)
        {
            EditorGUILayout.HelpBox("No webcam devices found.", MessageType.Warning);
            return;
        }

        selectedIndex = EditorGUILayout.Popup("Device", selectedIndex, deviceNames);
        EditorGUILayout.LabelField("Selected:", deviceNames[selectedIndex]);

        EditorGUILayout.Space();

        if (GUILayout.Button("Apply to selected TestWebcamController"))
        {
            ApplyToSelectedController();
        }

        if (GUILayout.Button("Set as default (webcamName field) in prefab/scene)"))
        {
            ApplyToSelectedController();
        }

        if (GUILayout.Button("Open GameObject with TestWebcamController"))
        {
            var controller = FindAnyObjectByType<TestWebcamController>();
            if (controller != null)
            {
                Selection.activeGameObject = controller.gameObject;
                EditorGUIUtility.PingObject(controller.gameObject);
            }
            else
            {
                EditorUtility.DisplayDialog("Not found", "No TestWebcamController instance found in the current scene.", "OK");
            }
        }
    }

    private void ApplyToSelectedController()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("Select GameObject", "Select a GameObject that has a TestWebcamController.", "OK");
            return;
        }

        var controller = go.GetComponent<TestWebcamController>();
        if (controller == null)
        {
            EditorUtility.DisplayDialog("Missing Component", "Selected GameObject does not have a TestWebcamController.", "OK");
            return;
        }

        Undo.RecordObject(controller, "Set webcamName");
        controller.SetWebcamByName(deviceNames[selectedIndex]);
        EditorUtility.SetDirty(controller);
        EditorUtility.DisplayDialog("Applied", "webcamName set on TestWebcamController. Enter Play mode to test.", "OK");
    }

    private void RefreshDevices()
    {
        var devices = WebCamTexture.devices;
        deviceNames = new string[devices.Length];
        for (int i = 0; i < devices.Length; i++) deviceNames[i] = devices[i].name;
        selectedIndex = Mathf.Clamp(selectedIndex, 0, deviceNames.Length - 1);
    }
}
