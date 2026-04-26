using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class McpLogCleanup
{
    static McpLogCleanup()
    {
        // Delay call so Unity finishes loading assemblies
        EditorApplication.delayCall += TryCleanup;
    }

    private static void TryCleanup()
    {
        try
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var targetDir = Path.Combine(projectRoot, "Temp", "mcp-server");

            if (!Directory.Exists(targetDir))
            {
                Debug.Log("MCP log cleanup: directory not found: " + targetDir);
                return;
            }

            var files = Directory.GetFiles(targetDir, "ai-editor-logs*.txt");
            if (files.Length == 0)
            {
                Debug.Log("MCP log cleanup: no ai-editor log files to remove.");
                return;
            }

            foreach (var f in files)
            {
                try
                {
                    File.Delete(f);
                    Debug.Log("MCP log cleanup: deleted " + f);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"MCP log cleanup: failed to delete {f}: {ex.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("MCP log cleanup: unexpected error: " + e.Message);
        }
    }
}
