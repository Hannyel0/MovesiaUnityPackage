#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using NativeWebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[InitializeOnLoad]
public static class MovesiaConnection
{
    // --- Socket state ---
    private static WebSocket ws;
    private static volatile bool isConnecting;
    private static volatile bool reconnecting;
    private static CancellationTokenSource cts;
    private static readonly System.Random rng = new System.Random();
    private static DateTime nextHbAt = DateTime.MinValue;

    // Connection sequence tracking (to avoid racing duplicates)
    private static int ConnSeq = 0;
    private static int CurrentSeq = 0;

    // FIX: Connection state tracking for hierarchy sending
    private static bool isConnectionReady = false;
    private static bool manifestSent = false;

    // Persist a session id across domain reloads (per Editor)
    private const string SessionKey = "Movesia.SessionId";
    public static string SessionId => EditorPrefs.GetString(SessionKey, EnsureSession());

    private static string EnsureSession()
    {
        var s = EditorPrefs.GetString(SessionKey, null);
        if (string.IsNullOrEmpty(s))
        {
            s = Guid.NewGuid().ToString("N").Substring(0, 8);
            EditorPrefs.SetString(SessionKey, s);
        }
        return s;
    }

    private const string Token = "REPLACE_ME";
    private static string WsUrl(int seq) => $"ws://127.0.0.1:8765?token={Token}&session={SessionId}&conn={seq}";

    // FIX: Public property to check connection state
    public static bool IsConnected => ws != null && ws.State == WebSocketState.Open && isConnectionReady;

    // FIX: Event for other classes to know when connection is fully ready
    public static event Action OnConnectionReady;

    // Auto-wire on domain load
    static MovesiaConnection()
    {
        // pump NativeWebSocket on editor tick
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;

        // clean close on reload/quit
        AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

        EditorApplication.quitting -= OnEditorQuitting;
        EditorApplication.quitting += OnEditorQuitting;

        CreateWebSocket();
        _ = ConnectWithRetry();
    }

    private static void OnBeforeAssemblyReload() => _ = CloseSocket("domain-reload");
    private static void OnEditorQuitting() => _ = CloseSocket("editor-quitting");

    private static void CreateWebSocket()
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();

        var mySeq = ++ConnSeq;         // monotonic connection sequence
        CurrentSeq = mySeq;

        // FIX: Reset connection ready state
        isConnectionReady = false;
        manifestSent = false;

        ws = new WebSocket(WsUrl(mySeq));

        ws.OnOpen += async () =>
        {
            Debug.Log($"✅ Movesia WS connected [{SessionId}] conn={mySeq}");
            isConnecting = false;
            reconnecting = false;
            nextHbAt = DateTime.UtcNow + TimeSpan.FromSeconds(5 + rng.Next(0, 5));
            MovesiaEditorState.instance.SetState(MovesiaConnState.Connected);
            
            // Send robust Unity handshake BEFORE any other events
            await SendRobustHandshake();

            // FIX: Use EditorApplication.delayCall to run connection ready workflow on main thread
            Debug.Log("🔄 Scheduling connection ready workflow...");
            EditorApplication.delayCall += OnConnectionReadyWorkflow;
        };

        ws.OnMessage += bytes =>
        {
            var msg = Encoding.UTF8.GetString(bytes);
            SafeReceiveFromMovesia(msg);
        };

        ws.OnError += async (err) =>
        {
            Debug.LogWarning($"WS error [{mySeq}]: {err}");
            isConnectionReady = false; // FIX: Reset ready state on error
            manifestSent = false;
            if (mySeq != CurrentSeq) return; // only latest may reconnect
            MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);
            await ReconnectSoon();
        };

        ws.OnClose += async (code) =>
        {
            Debug.LogWarning($"WS closed [{mySeq}]: {code}");
            isConnectionReady = false; // FIX: Reset ready state on close
            manifestSent = false;
            if (mySeq != CurrentSeq) return; // stale socket

            int numeric = (int)code; // enum → int
            if (numeric == 4001)
            {
                Debug.Log($"Connection [{mySeq}] superseded - not reconnecting");
                MovesiaEditorState.instance.SetState(MovesiaConnState.Disconnected);
                return;
            }

            MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);
            await ReconnectSoon();
        };
    }

    // FIX: Connection ready workflow with enhanced debugging
    private static void OnConnectionReadyWorkflow()
    {
        Debug.Log("🚀 OnConnectionReadyWorkflow called");
        
        if (ws == null || ws.State != WebSocketState.Open)
        {
            Debug.LogWarning("❌ OnConnectionReadyWorkflow: WebSocket not ready");
            return;
        }

        try
        {
            Debug.Log("🚀 Starting connection ready workflow...");

            // Step 2: Mark connection as ready
            Debug.Log("📌 Marking connection as ready");
            isConnectionReady = true;
            
            // Step 3: Notify HierarchyTracker and other listeners
            Debug.Log("📌 Notifying connection ready listeners...");
            try
            {
                OnConnectionReady?.Invoke();
                Debug.Log("✅ Successfully notified connection ready listeners");
            }
            catch (Exception eventEx)
            {
                Debug.LogError($"❌ Failed to notify listeners: {eventEx.Message}");
            }
            
            Debug.Log("🎉 Connection fully ready, waiting for manifest request from Electron");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Connection ready workflow failed: {ex.Message}");
            Debug.LogError($"❌ Workflow stack trace: {ex.StackTrace}");
            isConnectionReady = false;
            manifestSent = false;
        }
    }

    private static async Task ReconnectSoon()
    {
        if (reconnecting) return;
        reconnecting = true;
        isConnectionReady = false; // FIX: Reset ready state on reconnection
        manifestSent = false;
        await CloseSocket("reconnect");
        CreateWebSocket();
        await ConnectWithRetry();
    }

    private static async Task CloseSocket(string reason)
    {
        try
        {
            isConnectionReady = false; // FIX: Reset ready state
            manifestSent = false;
            cts?.Cancel();
            if (ws != null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.Connecting))
                await ws.Close();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"CloseSocket({reason}) error: {ex.Message}");
        }
        finally
        {
            ws = null;
            isConnecting = false;

            if (reason == "domain-reload" || reason == "editor-quitting")
                MovesiaEditorState.instance.SetState(MovesiaConnState.Disconnected);
        }
    }

    private static async Task ConnectWithRetry()
    {
        if (ws == null || isConnecting) return;
        isConnecting = true;
        MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);

        try
        {
            int attempt = 0;
            const int maxDelay = 30_000;

            while (!cts.IsCancellationRequested && ws != null && ws.State != WebSocketState.Open)
            {
                try
                {
                    Debug.Log("→ Attempting Movesia WS connect…");
                    await ws.Connect();
                    if (ws.State == WebSocketState.Open)
                    {
                        isConnecting = false; reconnecting = false;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Connect failed: {ex.Message}");
                }

                attempt++;
                int backoff = Math.Min(1000 * (1 << Math.Min(attempt, 5)), maxDelay);
                int jitter = rng.Next(250, 1000);
                try { await Task.Delay(backoff + jitter, cts.Token); }
                catch (TaskCanceledException) { break; }
            }
        }
        finally
        {
            isConnecting = false;
            if (ws == null || ws.State != WebSocketState.Open) reconnecting = false;
        }
    }

    // Pump socket + send heartbeats on the editor tick
    private static void OnEditorUpdate()
    {
        ws?.DispatchMessageQueue(); // Editor tick (not Play Update)

        if (ws != null && ws.State == WebSocketState.Open)
        {
            if (DateTime.UtcNow >= nextHbAt)
            {
                _ = Send("hb", new { ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                nextHbAt = DateTime.UtcNow + TimeSpan.FromSeconds(25 + rng.Next(0, 12));
            }
        }
    }

    // --- Public API ---

    public static async Task Send(string type, object body)
    {
        try
        {
            if (ws != null && ws.State == WebSocketState.Open)
            {
                var envelope = new
                {
                    v = 1,
                    source = "unity",
                    type,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    id = Guid.NewGuid().ToString("N"),
                    body,
                    session = SessionId
                };
                string json = JsonConvert.SerializeObject(envelope);
                await ws.SendText(json);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Send failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Sends a robust handshake message immediately on WebSocket open.
    /// This ensures proper project identification and session consistency.
    /// </summary>
    private static async Task SendRobustHandshake()
    {
        try
        {
            if (ws != null && ws.State == WebSocketState.Open)
            {
                // Create robust handshake following the specified format
                var hello = new
                {
                    v = 1,
                    source = "unity",
                    type = "hello",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    id = Guid.NewGuid().ToString("N"),
                    session = SessionId, // Consistent session ID for this Editor instance
                    body = new
                    {
                        productGUID = PlayerSettings.productGUID.ToString("N"), // Unique project identifier
                        cloudProjectId = Application.cloudProjectId,           // Optional Unity Cloud project ID
                        unityVersion = Application.unityVersion,               // Unity version (e.g., 6000.1.0f1)
                        dataPath = Application.dataPath                        // Fallback: <project>/Assets for exact root derivation
                    }
                };

                string json = JsonConvert.SerializeObject(hello);
                await ws.SendText(json);
                
                Debug.Log($"🤝 Sent robust handshake [{SessionId}] productGUID={PlayerSettings.productGUID.ToString("N")[..8]}...");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to send robust handshake: {ex.Message}");
        }
    }

    public static async void ReconnectNow() => await ReconnectSoon();

    public static async void DisconnectNow()
    {
        isConnectionReady = false; // FIX: Reset ready state
        manifestSent = false;
        await CloseSocket("user-disconnect");
        MovesiaEditorState.instance.SetState(MovesiaConnState.Disconnected);
    }

    // --- Receive (server→client) ---
    private static void SafeReceiveFromMovesia(string json)
    {
        try
        {
            var obj = JObject.Parse(json);
            var type = (string)obj["type"];
            if (type == "ack" || type == "welcome") return;

            if (type == "request_manifest" || type == "manifest:request" || type == "resync")
            {
                Debug.Log("📨 Received manifest request from Electron");
                
                // ✅ FIX: Reset manifestSent flag to allow sending manifest again
                manifestSent = false;
                
                // Send manifest synchronously
                SendManifestSync();
                return;
            }

            if (type == "hierarchy:request_full")
            {
                Debug.Log("📨 Electron→Unity requested full hierarchy");
                try { MovesiaHierarchyTracker.ForceResendFullHierarchy(); }
                catch (Exception ex) { Debug.LogError($"❌ Failed to resend hierarchy: {ex.Message}"); }
                return;
            }

            Debug.Log($"📨 Electron→Unity [{type}]: {json}");
        }
        catch { /* ignore parse errors */ }
    }

    // --- Manifest functionality ---

    [MenuItem("Movesia/Send Full Manifest")]
    private static void Menu_SendFullManifest() => SendManifestSync();

    /// <summary>
    /// FIX: Synchronous version with enhanced debugging to avoid threading issues with Unity APIs
    /// Build & send the full project manifest in batches.
    /// </summary>
    private static void SendManifestSync(int batchSize = 100) // Reduced batch size for debugging
    {
        Debug.Log("📦 SendManifestSync starting...");
        
        try
        {
            if (ws == null)
            {
                Debug.LogError("❌ SendManifestSync: WebSocket is null");
                return;
            }
            
            if (ws.State != WebSocketState.Open)
            {
                Debug.LogError($"❌ SendManifestSync: WebSocket state is {ws.State}");
                return;
            }

            Debug.Log("📦 Getting all asset paths...");
            
            // FIX: Add try-catch around each Unity API call
            string[] all;
            try
            {
                all = AssetDatabase.GetAllAssetPaths();
                Debug.Log($"📦 Found {all.Length} total asset paths");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ AssetDatabase.GetAllAssetPaths failed: {ex.Message}");
                throw;
            }

            // Filter to Assets/ folder
            try
            {
                all = all.Where(p => p.StartsWith("Assets/")).ToArray();
                Debug.Log($"📦 Filtered to {all.Length} asset paths in Assets/");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Filtering asset paths failed: {ex.Message}");
                throw;
            }

            // Tell Electron we're starting
            Debug.Log("📦 Sending manifest_begin...");
            try
            {
                _ = Send("manifest_begin", new { total = all.Length });
                Debug.Log($"📦 Sent manifest_begin with total={all.Length}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Sending manifest_begin failed: {ex.Message}");
                throw;
            }

            int total = all.Length;
            int index = 0;
            var batch = new List<object>(Math.Min(batchSize, total));

            Debug.Log($"📦 Starting to process {total} assets in batches of {batchSize}...");

            foreach (var path in all)
            {
                try
                {
                    var isFolder = AssetDatabase.IsValidFolder(path);
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    
                    // Null/empty GUIDs can occur for some transient or deleted items within the same Editor session.
                    if (string.IsNullOrEmpty(guid))
                    {
                        index++;
                        continue;
                    }

                    string abs = Path.GetFullPath(Path.Combine(ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
                    FileInfo fi = (!isFolder && File.Exists(abs)) ? new FileInfo(abs) : null;

                    // Kind: main asset type name (MonoScript, TextAsset, Scene, etc.)
                    var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                    string kind = t != null ? t.Name : (isFolder ? "Folder" : "Unknown");

                    // Unity's dependency hash flips when the source, .meta, import settings, or dependencies change.
                    // (More robust than just file-bytes.) 
                    string depHash = null;
                    try { depHash = AssetDatabase.GetAssetDependencyHash(path).ToString(); } catch { /* some asset types may throw */ }

                    // Compute dependency GUIDs: get deps, filter self, convert paths to GUIDs, dedupe
                    var depGuids = AssetDatabase
                        .GetDependencies(new[] { path }, true)               // returns dependency *paths*
                        .Where(p => p != path)                                // drop self; recursive=true includes self
                        .Select(p => AssetDatabase.AssetPathToGUID(p))        // convert to GUID
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .Take(50) // Limit dependencies to avoid huge payloads
                        .ToArray();

                    batch.Add(new
                    {
                        guid,
                        path,
                        kind,
                        isFolder,
                        mtime = fi != null ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() : (long?)null,
                        size  = fi != null ? fi.Length : (long?)null,
                        hash  = depHash, // preferred signal of "meaningful change"
                        deps  = depGuids 
                    });

                    index++;

                    if (batch.Count >= batchSize)
                    {
                        Debug.Log($"📦 Sending batch {index}/{total}...");
                        
                        // FIX: Use fire-and-forget Send (which is async) but don't await it
                        _ = Send("manifest_batch", new { index = index, total = total, items = batch.ToArray() });
                        batch.Clear();
                        
                        // FIX: Simple yield instead of QueuePlayerLoopUpdate
                        if (index % (batchSize * 5) == 0) // Every 5 batches, yield briefly
                        {
                            Debug.Log($"📦 Yielding at {index}/{total}...");
                            // Just let other operations run by returning to the call stack briefly
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"❌ Error processing asset {path}: {ex.Message}");
                    index++;
                    continue; // Skip this asset and continue
                }
            }

            if (batch.Count > 0)
            {
                Debug.Log($"📦 Sending final batch {index}/{total}...");
                _ = Send("manifest_batch", new { index = index, total = total, items = batch.ToArray() });
                batch.Clear();
            }

            Debug.Log("📦 Sending manifest_end...");
            _ = Send("manifest_end", new { total = total });
            Debug.Log($"📦 Sent manifest ({total} items).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ SendManifestSync failed: {ex.Message}");
            Debug.LogError($"❌ SendManifestSync stack trace: {ex.StackTrace}");
            throw; // Re-throw so caller can handle
        }
    }

    /// <summary>Absolute project root from Application.dataPath (Editor: <project>/Assets).</summary>
    private static string ProjectRoot =>
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}
#endif