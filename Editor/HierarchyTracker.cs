#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using System.Threading.Tasks;

[InitializeOnLoad]
public static class MovesiaHierarchyTracker
{
    // Track hierarchy state for change detection
    private static Dictionary<string, HierarchySnapshot> sceneSnapshots = new Dictionary<string, HierarchySnapshot>();
    private static bool hierarchyChangesPending = false;

    // FIX: Simple flag tracking
    private static bool initialHierarchySent = false; // per-connection gate

    static MovesiaHierarchyTracker()
    {
        Debug.Log("🏗️ [HierarchyTracker] Initializing...");

        // Subscribe to hierarchy change events
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;

        // Subscribe to more granular object change events for better delta tracking
        ObjectChangeEvents.changesPublished -= OnObjectChangesPublished;
        ObjectChangeEvents.changesPublished += OnObjectChangesPublished;

        // Scene events for full hierarchy capture
        EditorSceneManager.sceneOpened -= OnSceneOpened;
        EditorSceneManager.sceneOpened += OnSceneOpened;

        EditorSceneManager.sceneSaved -= OnSceneSaved;
        EditorSceneManager.sceneSaved += OnSceneSaved;

        // FIX: Subscribe to connection ready event with better error handling
        try
        {
            MovesiaConnection.OnConnectionReady -= OnConnectionReady;
            MovesiaConnection.OnConnectionReady += OnConnectionReady;
            Debug.Log("🏗️ [HierarchyTracker] Subscribed to connection ready event");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [HierarchyTracker] Failed to subscribe to connection events: {ex.Message}");
        }

        // FIX: If connection is already ready when this loads, send immediately
        if (MovesiaConnection.IsConnected)
        {
            Debug.Log("🏗️ [HierarchyTracker] Connection already ready, sending hierarchy immediately");
            EditorApplication.delayCall += () => {
                try {
                    SendInitialHierarchy();
                } catch (Exception ex) {
                    Debug.LogError($"❌ [HierarchyTracker] Failed to send initial hierarchy on startup: {ex.Message}");
                }
            };
        }

        Debug.Log("🏗️ [HierarchyTracker] Initialized successfully");
    }

    // FIX: Direct hierarchy sending without complex timing
    private static void OnConnectionReady()
    {
        Debug.Log("🚀 [HierarchyTracker] Connection ready notification received");
        
        // Always reset the flag on a **new** connection so a full snapshot is resent.
        initialHierarchySent = false;
        EditorApplication.delayCall += () => {
            try
            {
                Debug.Log("🏗️ [HierarchyTracker] Starting immediate hierarchy send after reconnect.");
                SendInitialHierarchy();
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ [HierarchyTracker] Failed to send hierarchy in OnConnectionReady: {ex.Message}");
            }
        };
    }

    /// <summary>
    /// Force a complete hierarchy resend (callable from Electron via WS message).
    /// </summary>
    public static void ForceResendFullHierarchy()
    {
        if (!MovesiaConnection.IsConnected)
        {
            Debug.LogWarning("🔌 [HierarchyTracker] Not connected; cannot resend hierarchy.");
            return;
        }
        initialHierarchySent = false;
        SendInitialHierarchy();
    }

    // FIX: Direct hierarchy sending method
    private static void SendInitialHierarchy()
    {
        if (initialHierarchySent)
        {
            Debug.Log("🏗️ [HierarchyTracker] Initial hierarchy already sent, skipping");
            return;
        }

        if (!MovesiaConnection.IsConnected)
        {
            Debug.LogWarning("❌ [HierarchyTracker] Connection not ready, cannot send hierarchy");
            return;
        }

        try
        {
            Debug.Log("🏗️ [HierarchyTracker] Starting hierarchy capture...");
            
            var loadedScenes = new List<Scene>();
            
            // Get all loaded scenes
            Debug.Log($"🏗️ [HierarchyTracker] Getting loaded scenes (total scene count: {SceneManager.sceneCount})...");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                Debug.Log($"🏗️ [HierarchyTracker] Scene {i}: {scene.name} (loaded: {scene.isLoaded}, path: {scene.path})");
                if (scene.isLoaded)
                {
                    loadedScenes.Add(scene);
                }
            }

            if (loadedScenes.Count == 0)
            {
                Debug.LogWarning("❌ [HierarchyTracker] No loaded scenes found");
                return;
            }

            Debug.Log($"📤 [HierarchyTracker] Found {loadedScenes.Count} loaded scenes, capturing hierarchy...");

            // Send each scene's hierarchy
            foreach (var scene in loadedScenes)
            {
                Debug.Log($"📤 [HierarchyTracker] Capturing hierarchy for scene: {scene.name}");
                try 
                {
                    CaptureFullSceneHierarchySync(scene);
                    Debug.Log($"✅ [HierarchyTracker] Successfully captured hierarchy for scene: {scene.name}");
                }
                catch (Exception sceneEx)
                {
                    Debug.LogError($"❌ [HierarchyTracker] Failed to capture hierarchy for scene {scene.name}: {sceneEx.Message}");
                }
            }

            initialHierarchySent = true;
            Debug.Log("✅ [HierarchyTracker] Initial hierarchy sent for all loaded scenes");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [HierarchyTracker] SendInitialHierarchy failed: {ex.Message}");
            Debug.LogError($"❌ [HierarchyTracker] Stack trace: {ex.StackTrace}");
            initialHierarchySent = false;
        }
    }

    // FIX: Synchronous version to avoid async issues
    private static void CaptureFullSceneHierarchySync(Scene scene)
    {
        try
        {
            Debug.Log($"🏗️ [HierarchyTracker] Creating hierarchy snapshot for scene: {scene.name}");
            
            var snapshot = CreateHierarchySnapshot(scene);
            sceneSnapshots[scene.path] = snapshot;
            
            Debug.Log($"🏗️ [HierarchyTracker] Created snapshot with {snapshot.GameObjects.Count} GameObjects");

            Debug.Log($"🏗️ [HierarchyTracker] Sending hierarchy_full message for scene: {scene.name}");
            
            // Use fire-and-forget Send
            _ = MovesiaConnection.Send("hierarchy_full", new
            {
                scene_path = scene.path,
                scene_guid = AssetDatabase.AssetPathToGUID(scene.path),
                hierarchy = snapshot
            });

            Debug.Log($"📤 [HierarchyTracker] Sent full hierarchy for scene: {scene.name} ({snapshot.GameObjects.Count} GameObjects)");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [HierarchyTracker] CaptureFullSceneHierarchySync failed for {scene.name}: {ex.Message}");
            throw;
        }
    }

    private static void OnHierarchyChanged()
    {
        if (!hierarchyChangesPending)
        {
            hierarchyChangesPending = true;
            EditorApplication.delayCall += ProcessHierarchyChanges;
        }
    }

    private static void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
    {
        // FIX: Only process if connection is ready
        if (!MovesiaConnection.IsConnected) return;

        for (int i = 0; i < stream.length; i++)
        {
            var eventType = stream.GetEventType(i);
            switch (eventType)
            {
                case ObjectChangeKind.CreateGameObjectHierarchy:
                    stream.GetCreateGameObjectHierarchyEvent(i, out var createEvent);
                    var newGameObject = EditorUtility.InstanceIDToObject(createEvent.instanceId) as GameObject;
                    if (newGameObject != null)
                    {
                        _ = SendGameObjectCreated(newGameObject, createEvent.scene);
                    }
                    break;

                case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                    stream.GetChangeGameObjectStructureHierarchyEvent(i, out var changeEvent);
                    var changedGameObject = EditorUtility.InstanceIDToObject(changeEvent.instanceId) as GameObject;
                    if (changedGameObject != null)
                    {
                        _ = SendGameObjectStructureChanged(changedGameObject, changeEvent.scene);
                    }
                    break;

                case ObjectChangeKind.DestroyGameObjectHierarchy:
                    stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyEvent);
                    _ = SendGameObjectDestroyed(destroyEvent.instanceId, destroyEvent.scene);
                    break;

                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var propsEvent);
                    var propsGameObject = EditorUtility.InstanceIDToObject(propsEvent.instanceId) as GameObject;
                    if (propsGameObject != null)
                    {
                        _ = SendGameObjectPropertiesChanged(propsGameObject, propsEvent.scene);
                    }
                    break;

                case ObjectChangeKind.ChangeGameObjectParent:
                    stream.GetChangeGameObjectParentEvent(i, out var parentEvent);
                    var parentGameObject = EditorUtility.InstanceIDToObject(parentEvent.instanceId) as GameObject;
                    if (parentGameObject != null)
                    {
                        var parentScene = parentGameObject.scene;
                        _ = SendGameObjectStructureChanged(parentGameObject, parentScene);
                    }
                    break;

                case ObjectChangeKind.ChangeScene:
                    stream.GetChangeSceneEvent(i, out var sceneEvent);
                    Debug.Log($"[HierarchyTracker] Scene changed: {sceneEvent.scene.name}");
                    break;
            }
        }
    }

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        if (MovesiaConnection.IsConnected)
        {
            _ = CaptureFullSceneHierarchy(scene);
        }
    }

    private static void OnSceneSaved(Scene scene)
    {
        if (MovesiaConnection.IsConnected)
        {
            _ = CaptureFullSceneHierarchy(scene);
        }
    }

    private static void ProcessHierarchyChanges()
    {
        hierarchyChangesPending = false;
        
        if (!MovesiaConnection.IsConnected) return;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
            {
                _ = DetectAndSendHierarchyChanges(scene);
            }
        }
    }

    private static async Task CaptureFullSceneHierarchy(Scene scene)
    {
        try
        {
            if (!MovesiaConnection.IsConnected)
            {
                Debug.LogWarning($"🔌 [HierarchyTracker] Connection not ready, skipping hierarchy capture for {scene.name}");
                return;
            }

            var snapshot = CreateHierarchySnapshot(scene);
            sceneSnapshots[scene.path] = snapshot;

            await MovesiaConnection.Send("hierarchy_full", new
            {
                scene_path = scene.path,
                scene_guid = AssetDatabase.AssetPathToGUID(scene.path),
                hierarchy = snapshot
            });

            Debug.Log($"📤 [HierarchyTracker] Sent full hierarchy for scene: {scene.name} ({snapshot.GameObjects.Count} GameObjects)");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [HierarchyTracker] Failed to capture scene hierarchy for {scene.name}: {ex.Message}");
        }
    }

    private static async Task DetectAndSendHierarchyChanges(Scene scene)
    {
        try
        {
            if (!MovesiaConnection.IsConnected) return;

            var currentSnapshot = CreateHierarchySnapshot(scene);
            var scenePath = scene.path;

            if (sceneSnapshots.TryGetValue(scenePath, out var previousSnapshot))
            {
                var changes = CalculateHierarchyChanges(previousSnapshot, currentSnapshot);
                if (changes.HasChanges)
                {
                    await MovesiaConnection.Send("hierarchy_delta", new
                    {
                        scene_path = scenePath,
                        scene_guid = AssetDatabase.AssetPathToGUID(scenePath),
                        changes = changes
                    });

                    Debug.Log($"📤 [HierarchyTracker] Sent hierarchy delta for scene: {scene.name} (Added: {changes.Added.Count}, Removed: {changes.Removed.Count}, Modified: {changes.Modified.Count})");
                }
            }

            sceneSnapshots[scenePath] = currentSnapshot;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [HierarchyTracker] Failed to detect hierarchy changes for {scene.name}: {ex.Message}");
        }
    }

    private static HierarchySnapshot CreateHierarchySnapshot(Scene scene)
    {
        Debug.Log($"🏗️ [HierarchyTracker] Creating snapshot for scene: {scene.name}");
        
        var gameObjects = new List<GameObjectData>();
        var rootGameObjects = scene.GetRootGameObjects();
        
        Debug.Log($"🏗️ [HierarchyTracker] Scene has {rootGameObjects.Length} root GameObjects");

        foreach (var rootGO in rootGameObjects)
        {
            CollectGameObjectHierarchy(rootGO, gameObjects, null);
        }

        Debug.Log($"🏗️ [HierarchyTracker] Collected {gameObjects.Count} total GameObjects");

        return new HierarchySnapshot
        {
            ScenePath = scene.path,
            GameObjects = gameObjects,
            Timestamp = DateTime.UtcNow
        };
    }

    private static void CollectGameObjectHierarchy(GameObject go, List<GameObjectData> collection, string parentPath)
    {
        var goData = CreateGameObjectData(go, parentPath);
        collection.Add(goData);

        // Recursively collect children
        for (int i = 0; i < go.transform.childCount; i++)
        {
            var child = go.transform.GetChild(i).gameObject;
            CollectGameObjectHierarchy(child, collection, goData.HierarchyPath);
        }
    }

    private static GameObjectData CreateGameObjectData(GameObject go, string parentPath)
    {
        var hierarchyPath = string.IsNullOrEmpty(parentPath) ? go.name : $"{parentPath}/{go.name}";
        var components = new List<ComponentData>();

        // Get all components
        var allComponents = go.GetComponents<Component>();
        
        foreach (var component in allComponents)
        {
            if (component == null) continue; // Skip missing script components

            var componentData = CreateComponentData(component);
            if (componentData != null)
            {
                components.Add(componentData);
            }
        }

        return new GameObjectData
        {
            InstanceID = go.GetInstanceID(),
            Name = go.name,
            HierarchyPath = hierarchyPath,
            ParentPath = parentPath,
            Tag = go.tag,
            Layer = go.layer,
            ActiveSelf = go.activeSelf,
            ActiveInHierarchy = go.activeInHierarchy,
            IsStatic = go.isStatic,
            Position = new SerializableVector3 { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
            Rotation = new SerializableQuaternion { x = go.transform.rotation.x, y = go.transform.rotation.y, z = go.transform.rotation.z, w = go.transform.rotation.w },
            Scale = new SerializableVector3 { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z },
            SiblingIndex = go.transform.GetSiblingIndex(),
            Components = components
        };
    }

    private static ComponentData CreateComponentData(Component component)
    {
        if (component == null) return null;

        var componentType = component.GetType();
        var componentData = new ComponentData
        {
            TypeName = componentType.Name,
            FullTypeName = componentType.FullName,
            AssemblyName = componentType.Assembly.GetName().Name,
            Enabled = true // Default for non-Behaviour components
        };

        // Handle Behaviour components (which have enabled property)
        if (component is Behaviour behaviour)
        {
            componentData.Enabled = behaviour.enabled;
        }

        // Extract serialized properties for important component types
        try
        {
            var serializedProperties = ExtractSerializedProperties(component);
            if (serializedProperties != null && serializedProperties.Count > 0)
            {
                componentData.Properties = serializedProperties;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[HierarchyTracker] Failed to extract properties for {componentType.Name}: {ex.Message}");
        }

        return componentData;
    }

    private static Dictionary<string, object> ExtractSerializedProperties(Component component)
    {
        var properties = new Dictionary<string, object>();
        var serializedObject = new SerializedObject(component);
        var property = serializedObject.GetIterator();

        // Skip the script reference
        if (property.NextVisible(true))
        {
            while (property.NextVisible(false))
            {
                try
                {
                    var value = GetSerializedPropertyValue(property);
                    if (value != null)
                    {
                        properties[property.name] = value;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[HierarchyTracker] Failed to read property {property.name}: {ex.Message}");
                }
            }
        }

        return properties;
    }

    private static object GetSerializedPropertyValue(SerializedProperty property)
    {
        switch (property.propertyType)
        {
            case SerializedPropertyType.Integer:
                return property.intValue;
            case SerializedPropertyType.Float:
                return property.floatValue;
            case SerializedPropertyType.String:
                return property.stringValue;
            case SerializedPropertyType.Boolean:
                return property.boolValue;
            case SerializedPropertyType.Vector2:
                return new { x = property.vector2Value.x, y = property.vector2Value.y };
            case SerializedPropertyType.Vector3:
                return new { x = property.vector3Value.x, y = property.vector3Value.y, z = property.vector3Value.z };
            case SerializedPropertyType.Vector4:
                return new { x = property.vector4Value.x, y = property.vector4Value.y, z = property.vector4Value.z, w = property.vector4Value.w };
            case SerializedPropertyType.Color:
                var color = property.colorValue;
                return new { r = color.r, g = color.g, b = color.b, a = color.a };
            case SerializedPropertyType.ObjectReference:
                return property.objectReferenceValue?.name ?? null;
            case SerializedPropertyType.Enum:
                return property.enumDisplayNames[property.enumValueIndex];
            case SerializedPropertyType.Rect:
                var rect = property.rectValue;
                return new { x = rect.x, y = rect.y, width = rect.width, height = rect.height };
            case SerializedPropertyType.LayerMask:
                return property.intValue;
            default:
                return property.displayName; // Fallback to display name for complex types
        }
    }

    private static HierarchyChanges CalculateHierarchyChanges(HierarchySnapshot previous, HierarchySnapshot current)
    {
        var changes = new HierarchyChanges();

        var previousLookup = previous.GameObjects.ToDictionary(go => go.InstanceID);
        var currentLookup = current.GameObjects.ToDictionary(go => go.InstanceID);

        // Find added objects
        foreach (var currentGO in current.GameObjects)
        {
            if (!previousLookup.ContainsKey(currentGO.InstanceID))
            {
                changes.Added.Add(currentGO);
            }
        }

        // Find removed objects
        foreach (var previousGO in previous.GameObjects)
        {
            if (!currentLookup.ContainsKey(previousGO.InstanceID))
            {
                changes.Removed.Add(previousGO);
            }
        }

        // Find modified objects
        foreach (var currentGO in current.GameObjects)
        {
            if (previousLookup.TryGetValue(currentGO.InstanceID, out var previousGO))
            {
                if (!GameObjectsEqual(previousGO, currentGO))
                {
                    changes.Modified.Add(new GameObjectChange
                    {
                        Previous = previousGO,
                        Current = currentGO
                    });
                }
            }
        }

        return changes;
    }

    private static bool GameObjectsEqual(GameObjectData a, GameObjectData b)
    {
        return a.Name == b.Name &&
               a.HierarchyPath == b.HierarchyPath &&
               a.ParentPath == b.ParentPath &&
               a.Tag == b.Tag &&
               a.Layer == b.Layer &&
               a.ActiveSelf == b.ActiveSelf &&
               Vector3Equal(a.Position, b.Position) &&
               QuaternionEqual(a.Rotation, b.Rotation) &&
               Vector3Equal(a.Scale, b.Scale) &&
               a.SiblingIndex == b.SiblingIndex &&
               ComponentsEqual(a.Components, b.Components);
    }

    private static bool Vector3Equal(SerializableVector3 a, SerializableVector3 b)
    {
        return Mathf.Approximately(a.x, b.x) && 
               Mathf.Approximately(a.y, b.y) && 
               Mathf.Approximately(a.z, b.z);
    }

    private static bool QuaternionEqual(SerializableQuaternion a, SerializableQuaternion b)
    {
        return Mathf.Approximately(a.x, b.x) && 
               Mathf.Approximately(a.y, b.y) && 
               Mathf.Approximately(a.z, b.z) && 
               Mathf.Approximately(a.w, b.w);
    }

    private static bool ComponentsEqual(List<ComponentData> a, List<ComponentData> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (!ComponentDataEqual(a[i], b[i])) return false;
        }

        return true;
    }

    private static bool ComponentDataEqual(ComponentData a, ComponentData b)
    {
        return a.TypeName == b.TypeName &&
               a.Enabled == b.Enabled &&
               DictionariesEqual(a.Properties, b.Properties);
    }

    private static bool DictionariesEqual(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;

        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var bValue) || !Equals(kvp.Value, bValue))
                return false;
        }

        return true;
    }

    // Event handlers for granular changes
    private static async Task SendGameObjectCreated(GameObject go, Scene scene)
    {
        var goData = CreateGameObjectData(go, go.transform.parent?.name);
        await MovesiaConnection.Send("hierarchy_gameobject_created", new
        {
            scene_path = scene.path,
            scene_guid = AssetDatabase.AssetPathToGUID(scene.path),
            gameObject = goData
        });
    }

    private static async Task SendGameObjectStructureChanged(GameObject go, Scene scene)
    {
        var goData = CreateGameObjectData(go, go.transform.parent?.name);
        await MovesiaConnection.Send("hierarchy_gameobject_structure_changed", new
        {
            scene_path = scene.path,
            scene_guid = AssetDatabase.AssetPathToGUID(scene.path),
            gameObject = goData
        });
    }

    private static async Task SendGameObjectDestroyed(int instanceId, Scene scene)
    {
        await MovesiaConnection.Send("hierarchy_gameobject_destroyed", new
        {
            scene_path = scene.path,
            scene_guid = AssetDatabase.AssetPathToGUID(scene.path),
            instance_id = instanceId
        });
    }

    private static async Task SendGameObjectPropertiesChanged(GameObject go, Scene scene)
    {
        var goData = CreateGameObjectData(go, go.transform.parent?.name);
        await MovesiaConnection.Send("hierarchy_gameobject_properties_changed", new
        {
            scene_path = scene.path,
            scene_guid = AssetDatabase.AssetPathToGUID(scene.path),
            gameObject = goData
        });
    }

    [MenuItem("Movesia/Send Full Hierarchy")]
    private static void Menu_SendFullHierarchy()
    {
        Debug.Log("📤 [HierarchyTracker] Manual hierarchy send triggered");
        
        if (!MovesiaConnection.IsConnected)
        {
            Debug.LogWarning("🔌 Not connected to Movesia. Please ensure the connection is established first.");
            return;
        }

        try
        {
            ForceResendFullHierarchy();
            Debug.Log("✅ [HierarchyTracker] Manual hierarchy send completed");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [HierarchyTracker] Manual hierarchy send failed: {ex.Message}");
        }
    }
}

// Data structures for hierarchy tracking - Updated with serializable types
[Serializable]
public struct SerializableVector3
{
    public float x, y, z;
}

[Serializable]
public struct SerializableQuaternion
{
    public float x, y, z, w;
}

[Serializable]
public class HierarchySnapshot
{
    public string ScenePath;
    public List<GameObjectData> GameObjects = new List<GameObjectData>();
    public DateTime Timestamp;
}

[Serializable]
public class GameObjectData
{
    public int InstanceID;
    public string Name;
    public string HierarchyPath;
    public string ParentPath;
    public string Tag;
    public int Layer;
    public bool ActiveSelf;
    public bool ActiveInHierarchy;
    public bool IsStatic;
    public SerializableVector3 Position;
    public SerializableQuaternion Rotation;
    public SerializableVector3 Scale;
    public int SiblingIndex;
    public List<ComponentData> Components = new List<ComponentData>();
}

[Serializable]
public class ComponentData
{
    public string TypeName;
    public string FullTypeName;
    public string AssemblyName;
    public bool Enabled;
    public Dictionary<string, object> Properties;
}

[Serializable]
public class HierarchyChanges
{
    public List<GameObjectData> Added = new List<GameObjectData>();
    public List<GameObjectData> Removed = new List<GameObjectData>();
    public List<GameObjectChange> Modified = new List<GameObjectChange>();

    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
}

[Serializable]
public class GameObjectChange
{
    public GameObjectData Previous;
    public GameObjectData Current;
}

#endif