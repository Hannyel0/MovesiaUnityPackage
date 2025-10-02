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

    // ✅ REMOVED: manifestSent - not needed anymore

    // Configuration flags for initial data sending
    private const bool SEND_HIERARCHY_ON_CONNECT = true;

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

    // Public property to check if hierarchy should be sent on connect
    public static bool ShouldSendHierarchyOnConnect => SEND_HIERARCHY_ON_CONNECT;

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

        isConnectionReady = false;

        ws = new WebSocket(WsUrl(mySeq));

        ws.OnOpen += async () =>
        {
            try
            {
                Debug.Log($"✅ Movesia WS connected [{SessionId}] conn={mySeq}");
                isConnecting = false;
                reconnecting = false;
                nextHbAt = DateTime.UtcNow + TimeSpan.FromSeconds(5 + rng.Next(0, 5));
                
                if (MovesiaEditorState.instance != null)
                    MovesiaEditorState.instance.SetState(MovesiaConnState.Connected);
                
                // Send handshake immediately
                await SendRobustHandshake();
                
                // Set connection ready
                isConnectionReady = true;
                Debug.Log("🔌 Connection ready flag set to true");
                
                // Notify listeners
                try
                {
                    if (OnConnectionReady != null)
                    {
                        Debug.Log($"🔔 Invoking OnConnectionReady event (subscriber count: {OnConnectionReady.GetInvocationList().Length})");
                        OnConnectionReady.Invoke();
                        Debug.Log("✅ Successfully invoked OnConnectionReady event");
                    }
                    else
                    {
                        Debug.LogWarning("⚠️ OnConnectionReady event has no subscribers!");
                    }
                }
                catch (Exception eventEx)
                {
                    Debug.LogError($"❌ Failed to notify listeners: {eventEx.Message}");
                    Debug.LogError($"❌ Stack trace: {eventEx.StackTrace}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Error in OnOpen handler: {ex.Message}");
                isConnectionReady = false;
            }
        };

        ws.OnMessage += bytes =>
        {
            var msg = Encoding.UTF8.GetString(bytes);
            SafeReceiveFromMovesia(msg);
        };

        ws.OnError += async (err) =>
        {
            try
            {
                Debug.LogWarning($"WS error [{mySeq}]: {err}");
                isConnectionReady = false;
                if (mySeq != CurrentSeq) return;
                
                if (MovesiaEditorState.instance != null)
                    MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);
                    
                await ReconnectSoon();
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Error in OnError handler: {ex.Message}");
            }
        };

        ws.OnClose += async (code) =>
        {
            try
            {
                Debug.LogWarning($"WS closed [{mySeq}]: {code}");
                isConnectionReady = false;
                if (mySeq != CurrentSeq) return;

                int numeric = (int)code; // enum → int
                if (numeric == 4001)
                {
                    Debug.Log($"Connection [{mySeq}] superseded - not reconnecting");
                    if (MovesiaEditorState.instance != null)
                        MovesiaEditorState.instance.SetState(MovesiaConnState.Disconnected);
                    return;
                }

                if (MovesiaEditorState.instance != null)
                    MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);
                    
                await ReconnectSoon();
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Error in OnClose handler: {ex.Message}");
            }
        };
    }

    private static async Task ReconnectSoon()
    {
        if (reconnecting) return;
        reconnecting = true;
        isConnectionReady = false;
        await CloseSocket("reconnect");
        CreateWebSocket();
        await ConnectWithRetry();
    }

    private static async Task CloseSocket(string reason)
    {
        try
        {
            isConnectionReady = false;
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
            {
                if (MovesiaEditorState.instance != null)
                    MovesiaEditorState.instance.SetState(MovesiaConnState.Disconnected);
            }
        }
    }

    private static async Task ConnectWithRetry()
    {
        if (ws == null || isConnecting) return;
        isConnecting = true;
        
        if (MovesiaEditorState.instance != null)
            MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);

        try
        {
            int attempt = 0;
            const int maxDelay = 5_000; // ✅ Reduced from 30s to 5s max

            while (!cts.IsCancellationRequested && ws != null && ws.State != WebSocketState.Open)
            {
                try
                {
                    Debug.Log("→ Attempting Movesia WS connect…");
                    await ws.Connect();
                    if (ws.State == WebSocketState.Open)
                    {
                        isConnecting = false; 
                        reconnecting = false;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Connect failed: {ex.Message}");
                }

                attempt++;
                // ✅ More aggressive initial retry (faster connection on first attempts)
                int backoff = attempt == 1 ? 100 :  // First retry after 100ms
                             attempt == 2 ? 500 :  // Second retry after 500ms
                             Math.Min(1000 * (1 << Math.Min(attempt - 2, 3)), maxDelay); // Then exponential
                
                int jitter = rng.Next(50, 200); // ✅ Reduced jitter
                
                try 
                { 
                    await Task.Delay(backoff + jitter, cts.Token); 
                }
                catch (TaskCanceledException) 
                { 
                    break; 
                }
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
                
                Debug.Log($"🤝 Sent robust handshake [{SessionId}] productGUID={PlayerSettings.productGUID.ToString("N").Substring(0, 8)}...");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to send robust handshake: {ex.Message}");
        }
    }

    public static async Task ReconnectNow() => await ReconnectSoon();

    public static async Task DisconnectNow()
    {
        isConnectionReady = false;
        await CloseSocket("user-disconnect");
        if (MovesiaEditorState.instance != null)
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
                SendManifestSync();
                return;
            }

            if (type == "hierarchy:request_full")
            {
                Debug.Log("📨 Electron→Unity requested full hierarchy");
                try 
                { 
                    // ✅ FIX: Removed .instance - call static method directly
                    MovesiaHierarchyTracker.ForceResendFullHierarchy(); 
                }
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

    private static void SendManifestSync(int batchSize = 100)
    {
        Debug.Log("📦 SendManifestSync starting...");
        
        try
        {
            if (ws == null || ws.State != WebSocketState.Open)
            {
                Debug.LogError("❌ SendManifestSync: WebSocket not ready");
                return;
            }

            string[] all = AssetDatabase.GetAllAssetPaths()
                .Where(p => p.StartsWith("Assets/"))
                .ToArray();

            _ = Send("manifest_begin", new { total = all.Length });

            int total = all.Length;
            int index = 0;
            var batch = new List<object>(Math.Min(batchSize, total));

            foreach (var path in all)
            {
                try
                {
                    var isFolder = AssetDatabase.IsValidFolder(path);
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    
                    if (string.IsNullOrEmpty(guid))
                    {
                        index++;
                        continue;
                    }

                    string abs = Path.GetFullPath(Path.Combine(ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
                    FileInfo fi = (!isFolder && File.Exists(abs)) ? new FileInfo(abs) : null;

                    var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                    string kind = t != null ? t.Name : (isFolder ? "Folder" : "Unknown");

                    string depHash = null;
                    try { depHash = AssetDatabase.GetAssetDependencyHash(path).ToString(); } catch { }

                    var depGuids = AssetDatabase
                        .GetDependencies(new[] { path }, true)
                        .Where(p => p != path)
                        .Select(p => AssetDatabase.AssetPathToGUID(p))
                        .Where(g => !string.IsNullOrEmpty(g))
                        .Distinct()
                        .Take(50)
                        .ToArray();

                    batch.Add(new
                    {
                        guid,
                        path,
                        kind,
                        isFolder,
                        mtime = fi != null ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() : (long?)null,
                        size  = fi != null ? fi.Length : (long?)null,
                        hash  = depHash,
                        deps  = depGuids 
                    });

                    index++;

                    if (batch.Count >= batchSize)
                    {
                        _ = Send("manifest_batch", new { index = index, total = total, items = batch.ToArray() });
                        batch.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"❌ Error processing asset {path}: {ex.Message}");
                    index++;
                    continue;
                }
            }

            if (batch.Count > 0)
            {
                _ = Send("manifest_batch", new { index = index, total = total, items = batch.ToArray() });
            }

            _ = Send("manifest_end", new { total = total });
            Debug.Log($"📦 Sent manifest ({total} items).");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ SendManifestSync failed: {ex.Message}");
        }
    }

    /// <summary>Absolute project root from Application.dataPath (Editor: <project>/Assets).</summary>
    private static string ProjectRoot =>
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}
#endif