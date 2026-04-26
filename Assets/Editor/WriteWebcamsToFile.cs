using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class WriteWebcamsToFile
{
    static WriteWebcamsToFile()
    {
        EditorApplication.delayCall += WriteList;
    }

    private static void WriteList()
    {
        try
        {
            var devices = WebCamTexture.devices;
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var outDir = Path.Combine(projectRoot, "Temp");
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "webcams.txt");

            using (var sw = new StreamWriter(outPath, false))
            {
                sw.WriteLine("Detected WebCam devices:");
                for (int i = 0; i < devices.Length; i++)
                {
                    sw.WriteLine($"{i}: {devices[i].name}");
                }
            }

            Debug.Log($"Wrote webcams list to: {outPath}");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("WriteWebcamsToFile failed: " + ex.Message);
        }
    }
}
