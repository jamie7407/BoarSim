using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Network;

/// <summary>
/// Minimal in-game overlay for hosting or joining a network session.
/// Sits above the existing OptionsMenuController at sort-order 150.
/// Built entirely at runtime — no prefab required.
///
/// Controls:
///   Host  → starts a local server + connects as the first client.
///   Join  → connects as a client to the IP entered in the text field.
///   Disconnect → gracefully stops the connection.
///
/// The panel auto-hides once a connection is established and shows a
/// small status badge in the corner so players know they are online.
/// </summary>
public class NetworkLobbyUI : MonoBehaviour
{
    // ── Singleton ──────────────────────────────────────────────────────────────
    public static NetworkLobbyUI Instance { get; private set; }

    // ── Runtime references ─────────────────────────────────────────────────────
    private GameObject  _panel;
    private TMP_InputField _addressField;
    private TMP_Text    _statusLabel;
    private Button      _hostBtn;
    private Button      _joinBtn;
    private Button      _disconnectBtn;
    private GameObject  _badge;          // small "ONLINE" indicator shown in-game
    private TMP_Text    _badgeLabel;

    // ── Colours ────────────────────────────────────────────────────────────────
    private static readonly Color PanelBg   = new Color(0.08f, 0.08f, 0.10f, 0.95f);
    private static readonly Color Green     = new Color(0.20f, 0.65f, 0.25f);
    private static readonly Color Blue      = new Color(0.20f, 0.40f, 0.85f);
    private static readonly Color Red       = new Color(0.65f, 0.15f, 0.15f);

    // ── Lifecycle ──────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        BuildUI();
        NetworkGameManager.OnHostStarted    += OnConnected;
        NetworkGameManager.OnClientConnected += OnConnected;
        NetworkGameManager.OnDisconnected   += OnConnectionLost;
    }

    private void OnDestroy()
    {
        NetworkGameManager.OnHostStarted    -= OnConnected;
        NetworkGameManager.OnClientConnected -= OnConnected;
        NetworkGameManager.OnDisconnected   -= OnConnectionLost;
    }

    // ── Public toggle ──────────────────────────────────────────────────────────
    public void Toggle() => _panel.SetActive(!_panel.activeSelf);
    public void Show()   => _panel.SetActive(true);
    public void Hide()   => _panel.SetActive(false);

    // ── Button handlers ────────────────────────────────────────────────────────
    private void OnHostClicked()
    {
        if (NetworkGameManager.Instance == null) return;
        SetStatus("Starting host…", Color.yellow);
        NetworkGameManager.Instance.StartHost();
    }

    private void OnJoinClicked()
    {
        if (NetworkGameManager.Instance == null) return;
        string addr = _addressField != null ? _addressField.text.Trim() : "localhost";
        if (string.IsNullOrEmpty(addr)) addr = "localhost";
        SetStatus($"Connecting to {addr}…", Color.yellow);
        NetworkGameManager.Instance.StartClient(addr);
    }

    private void OnDisconnectClicked()
    {
        NetworkGameManager.Instance?.Disconnect();
    }

    // ── Connection events ──────────────────────────────────────────────────────
    private void OnConnected()
    {
        bool isHost = NetworkGameManager.Instance != null && NetworkGameManager.Instance.IsHost;
        SetStatus(isHost ? "Hosting" : "Connected", Color.green);
        Hide();
        ShowBadge(isHost ? "HOST" : "CLIENT");
    }

    private void OnConnectionLost()
    {
        SetStatus("Disconnected", Color.red);
        HideBadge();
        Show();
    }

    private void SetStatus(string msg, Color col)
    {
        if (_statusLabel == null) return;
        _statusLabel.text  = msg;
        _statusLabel.color = col;
    }

    // ── Badge (small always-visible indicator) ─────────────────────────────────
    private void ShowBadge(string label)
    {
        if (_badge == null) return;
        _badge.SetActive(true);
        if (_badgeLabel != null) _badgeLabel.text = label;
    }

    private void HideBadge() => _badge?.SetActive(false);

    // ── UI construction ────────────────────────────────────────────────────────
    private void BuildUI()
    {
        var canvasGo = new GameObject("NetworkLobbyCanvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 150;
        canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>().uiScaleMode =
            UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        // ── Main panel ────────────────────────────────────────────────────────
        _panel = MakePanel(canvasGo.transform, "LobbyPanel", PanelBg);
        var rt = _panel.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(360, 220);

        var vl = _panel.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
        vl.padding  = new RectOffset(20, 20, 16, 16);
        vl.spacing  = 10;
        vl.childForceExpandWidth  = true;
        vl.childForceExpandHeight = false;

        AddLabel(_panel.transform, "MULTIPLAYER", 18, Color.white, bold: true);

        // IP address field
        _addressField = AddInputField(_panel.transform, "IP Address / hostname", "localhost");

        // Status line
        _statusLabel = AddLabel(_panel.transform, "Offline", 13, Color.gray);

        // Button row
        var row = new GameObject("Buttons");
        row.transform.SetParent(_panel.transform, false);
        var hLayout = row.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        hLayout.spacing = 8;
        hLayout.childForceExpandWidth  = false;
        hLayout.childForceExpandHeight = true;
        hLayout.childAlignment = TextAnchor.MiddleCenter;
        var rowLE = row.AddComponent<UnityEngine.UI.LayoutElement>();
        rowLE.preferredHeight = 36;

        _hostBtn       = MakeButton(row.transform, "Host",       Green, OnHostClicked,       110);
        _joinBtn        = MakeButton(row.transform, "Join",       Blue,  OnJoinClicked,        110);
        _disconnectBtn  = MakeButton(row.transform, "Disconnect", Red,   OnDisconnectClicked,  110);

        // ── Small badge (top-right, hidden until connected) ───────────────────
        _badge = MakePanel(canvasGo.transform, "Badge", new Color(0.1f, 0.5f, 0.1f, 0.9f));
        var brt = _badge.GetComponent<RectTransform>();
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 1f);
        brt.anchoredPosition = new Vector2(-10, -10);
        brt.sizeDelta = new Vector2(80, 28);
        _badgeLabel = AddLabel(_badge.transform, "HOST", 13, Color.white, bold: true);
        if (_badgeLabel != null) _badgeLabel.alignment = TMPro.TextAlignmentOptions.Center;
        _badge.SetActive(false);

        // Start hidden — player opens it manually or via OptionsMenu toggle
        _panel.SetActive(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private static GameObject MakePanel(Transform parent, string name, Color color)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<UnityEngine.UI.Image>().color = color;
        return go;
    }

    private static TMP_Text AddLabel(Transform parent, string text, float size,
        Color color, bool bold = false)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
        le.preferredHeight = size + 6;
        var tmp = go.AddComponent<TMPro.TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = size;
        tmp.color     = color;
        tmp.fontStyle = bold ? TMPro.FontStyles.Bold : TMPro.FontStyles.Normal;
        tmp.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        return tmp;
    }

    private static TMP_InputField AddInputField(Transform parent, string placeholder, string defaultText)
    {
        var go = new GameObject("InputField");
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
        le.preferredHeight = 34;

        var bg = go.AddComponent<UnityEngine.UI.Image>();
        bg.color = new Color(0.15f, 0.15f, 0.20f);

        var field = go.AddComponent<TMP_InputField>();

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var trt = textGo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(6, 2); trt.offsetMax = new Vector2(-6, -2);
        var tmp = textGo.AddComponent<TMPro.TextMeshProUGUI>();
        tmp.fontSize = 14;
        tmp.color    = Color.white;

        var phGo = new GameObject("Placeholder");
        phGo.transform.SetParent(go.transform, false);
        var prt = phGo.AddComponent<RectTransform>();
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(6, 2); prt.offsetMax = new Vector2(-6, -2);
        var ph = phGo.AddComponent<TMPro.TextMeshProUGUI>();
        ph.text      = placeholder;
        ph.fontSize  = 14;
        ph.color     = new Color(0.5f, 0.5f, 0.5f);
        ph.fontStyle = TMPro.FontStyles.Italic;

        field.textComponent   = tmp;
        field.placeholder     = ph;
        field.text            = defaultText;
        field.characterLimit  = 64;

        return field;
    }

    private static Button MakeButton(Transform parent, string label, Color bg,
        UnityEngine.Events.UnityAction onClick, float width = 100)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
        le.preferredWidth  = width;
        le.preferredHeight = 34;
        go.AddComponent<UnityEngine.UI.Image>().color = bg;
        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(onClick);

        var txtGo = new GameObject("Label");
        txtGo.transform.SetParent(go.transform, false);
        var rt = txtGo.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var tmp = txtGo.AddComponent<TMPro.TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 13;
        tmp.color     = Color.white;
        tmp.fontStyle = TMPro.FontStyles.Bold;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;

        return btn;
    }
}
