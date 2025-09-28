#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Paps.UnityToolbarExtenderUIToolkit;

public enum MovesiaConnState { Connecting, Connected, Disconnected }

// Persist per-project
[FilePath("ProjectSettings/MovesiaEditorState.asset", FilePathAttribute.Location.ProjectFolder)]
public sealed class MovesiaEditorState : ScriptableSingleton<MovesiaEditorState>
{
    public MovesiaConnState State = MovesiaConnState.Disconnected;
    public event Action<MovesiaConnState> OnStateChanged;

    public void SetState(MovesiaConnState state)
    {
        if (State == state) return;
        State = state;
        Save(true); // now actually writes due to FilePathAttribute
        OnStateChanged?.Invoke(State);
    }
}

[MainToolbarElement(id: "Movesia/Status", ToolbarAlign.Left, order: 2)]
public sealed class MovesiaToolbarStatus : VisualElement
{
    const string BaseIconPath      = "Assets/Scripts/Movesia/Editor/EditorResources/icons/";
    const string IconConnected     = "movesia-connected-dark.png";
    const string IconConnecting    = "movesia-connecting-dark.png";
    const string IconDisconnected  = "movesia-disconnected-dark.png";

    // target icon size (smaller)
    const float IconPx = 10f;

    Image _img;
    static Texture2D _texConnected, _texConnecting, _texDisconnected;

    // Called by the toolbar extender after construction (per package docs)
    public void InitializeElement()
    {
        name = "MovesiaToolbarStatus";

        // Kill any default backgrounds/plates/borders on the container
        style.backgroundColor = Color.clear;
        style.borderBottomWidth = 0; style.borderTopWidth = 0; style.borderLeftWidth = 0; style.borderRightWidth = 0;
        style.borderBottomColor = Color.clear; style.borderTopColor = Color.clear; style.borderLeftColor = Color.clear; style.borderRightColor = Color.clear;

        // Tight spacing & center alignment so the icon sits cleanly in the bar
        style.marginLeft = 2; style.marginRight = 2;
        style.paddingLeft = 0; style.paddingRight = 0; style.paddingTop = 0; style.paddingBottom = 0;
        style.alignItems = Align.Center;
        style.justifyContent = Justify.Center;

        // Create icon image with no background tint
        _img = new Image
        {
            pickingMode = PickingMode.Ignore,
            scaleMode = ScaleMode.ScaleToFit
        };
        _img.style.width  = IconPx;
        _img.style.height = IconPx;
        _img.style.backgroundColor = Color.clear;              // ensure no plate behind PNG
        _img.style.unityBackgroundImageTintColor = Color.white; // draw texture as-is
        Add(_img);

        // Load icons once
        _texConnected    ??= LoadIconOrFallback(BaseIconPath + IconConnected);
        _texConnecting   ??= LoadIconOrFallback(BaseIconPath + IconConnecting);
        _texDisconnected ??= LoadIconOrFallback(BaseIconPath + IconDisconnected);

        // Initial state + tooltip
        ApplyState(MovesiaEditorState.instance.State);

        // Use a Clickable manipulator to open a menu (keeps background clean)
        var clickable = new Clickable(() => ShowMenu());
        this.AddManipulator(clickable);

        // React to future state changes
        RegisterCallback<AttachToPanelEvent>(_ =>
        {
            MovesiaEditorState.instance.OnStateChanged -= OnStateChanged;
            MovesiaEditorState.instance.OnStateChanged += OnStateChanged;
        });
        RegisterCallback<DetachFromPanelEvent>(_ =>
        {
            MovesiaEditorState.instance.OnStateChanged -= OnStateChanged;
        });
    }

    void OnStateChanged(MovesiaConnState st) => ApplyState(st);

    void ApplyState(MovesiaConnState st)
    {
        switch (st)
        {
            case MovesiaConnState.Connected:
                _img.image = _texConnected;    tooltip = "Movesia: Connected";    break;
            case MovesiaConnState.Connecting:
                _img.image = _texConnecting;   tooltip = "Movesia: Connecting…";  break;
            default:
                _img.image = _texDisconnected; tooltip = "Movesia: Disconnected"; break;
        }
    }

void ShowMenu()
{
    var menu = new GenericDropdownMenu();

    menu.AddItem("Reconnect", false, () =>
    {
        MovesiaEditorState.instance.SetState(MovesiaConnState.Connecting);
        // MovesiaBridge.ReconnectNow();
    });

    menu.AddItem("Disconnect", false, () =>
    {
        MovesiaEditorState.instance.SetState(MovesiaConnState.Disconnected);
        // MovesiaBridge.DisconnectNow();
    });

    // Prefer the image rect; fall back to this element's rect.
    Rect anchor = (_img != null && _img.worldBound.height > 0f)
        ? _img.worldBound
        : this.worldBound;

    // Use EditorGUIUtility.singleLineHeight for consistent spacing across scales.
    float gap = EditorGUIUtility.singleLineHeight * 0.6f; // ~10px on default scale

    // Anchor the menu just below the icon with the added gap.
    var below = new Rect(anchor.xMin, anchor.yMax + gap, anchor.width, anchor.height);

    // 4-arg overload: anchored=true (use our rect), fitToContentWidth=true.
    menu.DropDown(below, this, true, true);
}


    static Texture2D LoadIconOrFallback(string path)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex != null) return tex;
        var fallback = EditorGUIUtility.IconContent("d_console.infoicon").image as Texture2D;
        Debug.LogWarning($"[Movesia] Icon not found at: {path}. Using fallback.");
        return fallback;
    }
}
#endif
