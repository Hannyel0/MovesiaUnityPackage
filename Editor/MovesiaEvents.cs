#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Security.Cryptography;

// NOTE: Unity docs used:
// - AssetPostprocessor.OnPostprocessAllAssets (import/move/delete): https://docs.unity3d.com/ScriptReference/AssetPostprocessor.OnPostprocessAllAssets.html
// - AssetDatabase.AssetPathToGUID / GetDependencies / GetMainAssetTypeAtPath:
//   https://docs.unity3d.com/ScriptReference/AssetDatabase.AssetPathToGUID.html
//   https://docs.unity3d.com/ScriptReference/AssetDatabase.GetDependencies.html
//   https://docs.unity3d.com/ScriptReference/AssetDatabase.GetMainAssetTypeAtPath.html
// - EditorSceneManager.sceneSaved: https://docs.unity3d.com/ScriptReference/SceneManagement.EditorSceneManager-sceneSaved.html
// - AssetModificationProcessor.OnWillSaveAssets: https://docs.unity3d.com/ScriptReference/AssetModificationProcessor.OnWillSaveAssets.html
// - CompilationPipeline events: https://docs.unity3d.com/ScriptReference/Compilation.CompilationPipeline.html

[InitializeOnLoad]
public static class MovesiaEvents
{
    const int MaxBatchDeps = 64;

    static MovesiaEvents()
    {
        // Scenes
        EditorSceneManager.sceneSaved -= OnSceneSaved;
        EditorSceneManager.sceneSaved += OnSceneSaved;

        // Project-wide changes
        EditorApplication.projectChanged -= OnProjectChanged;
        EditorApplication.projectChanged += OnProjectChanged;

        // Compile signals
        CompilationPipeline.compilationStarted  -= OnCompilationStarted;
        CompilationPipeline.compilationStarted  += OnCompilationStarted;

        CompilationPipeline.compilationFinished -= OnCompilationFinished;
        CompilationPipeline.compilationFinished += OnCompilationFinished;
    }

    // -------- Asset import/move/delete --------
    public class MovesiaImportHook : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported is { Length: > 0 })
                _ = EmitAssetBatch("assets_imported",
                    imported.Select(p => new AssetChange { path = p, change = "created" }).ToArray());

            if (deleted is { Length: > 0 })
                _ = EmitAssetBatch("assets_deleted",
                    deleted.Select(p => new AssetChange { path = p, change = "deleted" }).ToArray());

            if (moved is { Length: > 0 })
                _ = EmitAssetMoves(moved, movedFrom); // include from/to pair
        }
    }

    private static async Task EmitAssetBatch(string evtType, AssetChange[] items)
    {
        if (items == null || items.Length == 0) return;

        var payload = new
        {
            items = items.Select(it =>
            {
                var kind = AssetDatabase.GetMainAssetTypeAtPath(it.path)?.Name ?? "Unknown";
                var abs  = ToAbsolutePath(it.path);
                var isFolder = AssetDatabase.IsValidFolder(it.path);
                var fi = (!isFolder && File.Exists(abs)) ? new FileInfo(abs) : null;

                var includeDeps = kind == "Prefab" || kind == "Material" || kind == "AnimatorController";
                // Scenes: skip in batch; they will send full deps via scene_saved
                var depGuids = includeDeps ? DepGuidsNonRecursive(it.path) : Array.Empty<string>();

                return new
                {
                    guid   = AssetDatabase.AssetPathToGUID(it.path),
                    path   = it.path,
                    kind   = kind,
                    change = it.change,
                    deps   = depGuids,                            // <-- GUIDs only
                    // optional metadata (server can COALESCE on nulls)
                    mtime  = fi != null ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() : (long?)null,
                    size   = fi != null ? fi.Length : (long?)null,
                    sha256 = (fi != null && ShouldHash(kind, fi)) ? FileSha256(abs) : null
                };
            }).ToArray()
        };

        await MovesiaConnection.Send(evtType, payload);
    }

    private static async Task EmitAssetMoves(string[] moved, string[] movedFrom)
    {
        // Unity guarantees moved[i] corresponds to movedFrom[i].
        var items = moved.Select((to, i) =>
        {
            var from = (i < movedFrom.Length) ? movedFrom[i] : null;
            var kind = AssetDatabase.GetMainAssetTypeAtPath(to)?.Name ?? "Unknown";
            var abs  = ToAbsolutePath(to);
            var isFolder = AssetDatabase.IsValidFolder(to);
            var fi = (!isFolder && File.Exists(abs)) ? new FileInfo(abs) : null;

            var includeDeps = kind == "Prefab" || kind == "Material" || kind == "AnimatorController";
            var depGuids = includeDeps ? DepGuidsNonRecursive(to) : Array.Empty<string>();

            return new
            {
                guid   = AssetDatabase.AssetPathToGUID(to),
                path   = to,
                from   = from,             // << extra context for moves
                kind   = kind,
                change = "moved",
                deps   = depGuids,                       // <-- GUIDs only
                mtime  = fi != null ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() : (long?)null,
                size   = fi != null ? fi.Length : (long?)null,
                sha256 = (fi != null && ShouldHash(kind, fi)) ? FileSha256(abs) : null
            };
        }).ToArray();

        await MovesiaConnection.Send("assets_moved", new { items });
    }

    // -------- Scene saved --------
    private static async void OnSceneSaved(Scene scene)
    {
        try
        {
            var path = scene.path;                                        // e.g., "Assets/Scenes/Foo.unity"
            var guid = AssetDatabase.AssetPathToGUID(path);               // stable GUID for the .unity
            var abs  = ToAbsolutePath(path);
            var fi   = File.Exists(abs) ? new FileInfo(abs) : null;

            // deps: convert PATHS → GUIDs (drop self, dedupe)
            var depGuids = AssetDatabase
                .GetDependencies(new[] { path }, true)                    // returns PATHS
                .Where(p => p != path)                                    // recursive=true includes input; drop it
                .Select(p => AssetDatabase.AssetPathToGUID(p))            // convert to GUIDs
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct()
                .ToArray();

            var payload = new
            {
                guid,
                path,
                kind = "Scene",
                change = "modified",
                deps = depGuids,
                mtime  = fi != null ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() : (long?)null,
                size   = fi != null ? fi.Length : (long?)null,
                sha256 = (fi != null && ShouldHash("Scene", fi)) ? FileSha256(abs) : null
            };

            await MovesiaConnection.Send("scene_saved", payload);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Movesia] scene_saved emit failed: {e}");
        }
    }

    // -------- Project/compile signals --------
    private static async void OnProjectChanged()
        => await MovesiaConnection.Send("project_changed", new { });

    private static async void OnCompilationStarted(object _)
        => await MovesiaConnection.Send("compile_started", new { });

    private static async void OnCompilationFinished(object _)
        => await MovesiaConnection.Send("compile_finished", new { });

    // -------- WillSaveAssets (pre-save intent) --------
    public class MovesiaSaveHook : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            _ = MovesiaConnection.Send("will_save_assets", new { paths });
            return paths;
        }
    }

    // ===== Helpers =====
    private struct AssetChange { public string path; public string change; }

    static string[] DepGuidsNonRecursive(string path)
    {
        // Paths → GUIDs (non-recursive), drop self, dedupe, cap
        return AssetDatabase
            .GetDependencies(new[] { path }, false)     // ← non-recursive
            .Where(p => p != path)
            .Select(p => AssetDatabase.AssetPathToGUID(p))
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct()
            .Take(MaxBatchDeps)
            .ToArray();
    }

    private static string ProjectRoot =>
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static string ToAbsolutePath(string assetPath)
        => Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));

    private static bool ShouldHash(string kind, FileInfo fi)
    {
        if (fi.Length > 2 * 1024 * 1024) return false; // skip very large files
        // Hash common textual assets; scenes/prefabs are YAML when "Force Text" is enabled
        var ext = fi.Extension.ToLowerInvariant();
        if (ext == ".cs" || ext == ".shader" || ext == ".compute" || ext == ".cginc" || ext == ".asmdef" || ext == ".asmref" || ext == ".uss" || ext == ".uxml" || ext == ".json" || ext == ".yaml" || ext == ".yml")
            return true;
        if (kind == "TextAsset" || kind == "Shader" || kind == "Scene") return true;
        return false;
    }

    private static string FileSha256(string absPath)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(absPath);
        var bytes = sha.ComputeHash(s);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}

[InitializeOnLoad]
public static class MovesiaSelectionTracker
{
    private static string lastSelectedPath = null;
    private static string lastSelectedGuid = null;

    static MovesiaSelectionTracker()
    {
        Debug.Log("🎯 [SelectionTracker] Initializing...");
        
        // Subscribe to selection changes
        Selection.selectionChanged -= OnSelectionChanged;
        Selection.selectionChanged += OnSelectionChanged;
        
        Debug.Log("✅ [SelectionTracker] Initialized successfully");
    }

    private static void OnSelectionChanged()
    {
        try
        {
            if (!MovesiaConnection.IsConnected)
            {
                return; // Don't process if not connected
            }

            // Get the currently selected object
            var selectedObject = Selection.activeObject;
            
            if (selectedObject == null)
            {
                // Nothing selected - send null selection
                if (lastSelectedPath != null) // Only send if we had something selected before
                {
                    SendSelectionUpdate(null, null, null, null);
                    lastSelectedPath = null;
                    lastSelectedGuid = null;
                }
                return;
            }

            // Get the asset path (works for both project assets and scene objects)
            var assetPath = AssetDatabase.GetAssetPath(selectedObject);
            
            // Only process if it's a valid asset path (not scene objects without asset representation)
            if (string.IsNullOrEmpty(assetPath))
            {
                // This is likely a scene object without an asset file
                // We can still show the GameObject name
                if (selectedObject is GameObject gameObject)
                {
                    SendSelectionUpdate(null, selectedObject.name, "GameObject", null);
                }
                else
                {
                    SendSelectionUpdate(null, selectedObject.name, selectedObject.GetType().Name, null);
                }
                lastSelectedPath = null;
                lastSelectedGuid = null;
                return;
            }

            // Check if this is the same selection as before (avoid spam)
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (guid == lastSelectedGuid)
            {
                return; // Same selection, don't send duplicate
            }

            // Determine the asset type
            var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            var typeName = assetType != null ? assetType.Name : "Unknown";
            
            // Check if it's a folder
            if (AssetDatabase.IsValidFolder(assetPath))
            {
                typeName = "Folder";
            }

            // Send the selection update
            SendSelectionUpdate(assetPath, selectedObject.name, typeName, guid);
            
            // Cache the selection to avoid duplicates
            lastSelectedPath = assetPath;
            lastSelectedGuid = guid;
            
            Debug.Log($"🎯 [SelectionTracker] Selected: {selectedObject.name} ({typeName}) at {assetPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [SelectionTracker] OnSelectionChanged failed: {ex.Message}");
        }
    }

    private static void SendSelectionUpdate(string path, string name, string type, string guid)
    {
        try
        {
            _ = MovesiaConnection.Send("selection_changed", new
            {
                path = path,
                name = name,
                type = type,
                guid = guid,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [SelectionTracker] Failed to send selection update: {ex.Message}");
        }
    }

    /// <summary>
    /// Force send current selection (useful for when connection is re-established)
    /// </summary>
    public static void SendCurrentSelection()
    {
        try
        {
            OnSelectionChanged();
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [SelectionTracker] Failed to send current selection: {ex.Message}");
        }
    }

    [MenuItem("Movesia/Send Current Selection")]
    private static void Menu_SendCurrentSelection()
    {
        Debug.Log("🎯 [SelectionTracker] Manual selection send triggered");
        
        if (!MovesiaConnection.IsConnected)
        {
            Debug.LogWarning("🔌 Not connected to Movesia. Please ensure the connection is established first.");
            return;
        }

        try
        {
            SendCurrentSelection();
            Debug.Log("✅ [SelectionTracker] Manual selection send completed");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [SelectionTracker] Manual selection send failed: {ex.Message}");
        }
    }
}

#endif
