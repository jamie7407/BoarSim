using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Attach this to any empty GameObject in your scene.
// Then drag that object into OptionsMenuController's "Multiplayer Panel" field.
//
// This script creates its entire panel hierarchy at runtime — no prefab or
// child-object setup is required in the scene. It finds the first Canvas in the
// scene and parents itself there, matching the existing menu's overlay behaviour.
//
// Requires NetworkBootstrapper to be present in the scene (on the LoadMatch object).

public class NetworkLobbyUI : MonoBehaviour
{
    // Fires when the player clicks "Back", so OptionsMenuController can restore the main menu.
    public event Action OnBackClicked;

    private GameObject _panelRoot;
    private TextMeshProUGUI _statusText;
    private TextMeshProUGUI _joinCodeDisplay;
    private TMP_InputField _joinCodeInput;
    private GameObject _joinSection;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        BuildPanel();
        _panelRoot?.SetActive(false);
    }

    public void Open()
    {
        if (_panelRoot == null) return;
        _panelRoot.SetActive(true);
        _joinSection?.SetActive(false);
        if (_joinCodeDisplay != null) _joinCodeDisplay.text = string.Empty;
        SetStatus("Choose Host or Join.");
    }

    public void Close()
    {
        _panelRoot?.SetActive(false);
    }

    // ── Build all UI in code ──────────────────────────────────────────────────

    private void BuildPanel()
    {
        var canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) { Debug.LogError("[Net] NetworkLobbyUI: no Canvas found."); return; }

        // Full-screen dark background
        _panelRoot = MakePanel("NetLobbyPanel", canvas.transform, new Color(0.08f, 0.08f, 0.12f, 0.97f));
        StretchFull(_panelRoot.GetComponent<RectTransform>());

        // Title
        MakeText(_panelRoot.transform, "Online Play", 38,
            TextAlignmentOptions.Center, 0.1f, 0.85f, 0.9f, 0.96f, Color.white);

        // Thin horizontal rule below title
        MakeDivider(_panelRoot.transform, 0.83f);

        // ── Host button ──────────────────────────────────────────────────────
        var hostBtn = MakeButton(_panelRoot.transform, "HOST GAME",
            0.1f, 0.70f, 0.46f, 0.80f, new Color(0.14f, 0.47f, 0.82f));
        hostBtn.onClick.AddListener(OnHostClicked);

        // ── Join button ──────────────────────────────────────────────────────
        var joinBtn = MakeButton(_panelRoot.transform, "JOIN GAME",
            0.54f, 0.70f, 0.90f, 0.80f, new Color(0.18f, 0.60f, 0.32f));
        joinBtn.onClick.AddListener(OnJoinModeClicked);

        // ── Join-code display (shown after hosting) ──────────────────────────
        _joinCodeDisplay = MakeText(_panelRoot.transform, string.Empty, 30,
            TextAlignmentOptions.Center, 0.1f, 0.58f, 0.9f, 0.68f,
            new Color(1f, 0.85f, 0.2f));

        // ── Join section (code input + connect — hidden until Join is clicked) ─
        _joinSection = new GameObject("JoinSection", typeof(RectTransform));
        _joinSection.transform.SetParent(_panelRoot.transform, false);
        var jrt = _joinSection.GetComponent<RectTransform>();
        jrt.anchorMin = new Vector2(0.1f, 0.45f);
        jrt.anchorMax = new Vector2(0.9f, 0.66f);
        jrt.offsetMin = jrt.offsetMax = Vector2.zero;

        _joinCodeInput = MakeInputField(_joinSection.transform, "Enter join code…",
            0f, 0f, 0.62f, 1f);

        var connectBtn = MakeButton(_joinSection.transform, "CONNECT",
            0.65f, 0f, 1f, 1f, new Color(0.18f, 0.60f, 0.32f));
        connectBtn.onClick.AddListener(OnConnectClicked);
        _joinSection.SetActive(false);

        // ── Status line ──────────────────────────────────────────────────────
        MakeDivider(_panelRoot.transform, 0.42f);

        _statusText = MakeText(_panelRoot.transform, "Not connected.", 18,
            TextAlignmentOptions.Center, 0.1f, 0.31f, 0.9f, 0.41f,
            new Color(0.72f, 0.72f, 0.72f));

        // ── Back button ───────────────────────────────────────────────────────
        MakeDivider(_panelRoot.transform, 0.28f);

        var backBtn = MakeButton(_panelRoot.transform, "← BACK TO MENU",
            0.1f, 0.16f, 0.45f, 0.26f, new Color(0.32f, 0.32f, 0.38f));
        backBtn.onClick.AddListener(() => OnBackClicked?.Invoke());
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private async void OnHostClicked()
    {
        if (NetworkBootstrapper.Instance == null)
        { SetStatus("NetworkBootstrapper not found — add it to the scene."); return; }

        SetStatus("Creating relay allocation…");

        try
        {
            var code = await NetworkBootstrapper.Instance.StartHostAsync(3);
            if (_joinCodeDisplay != null)
                _joinCodeDisplay.text = $"Join Code:   {code}";
            SetStatus("Hosting! Share the code above with your opponent.");
            _joinSection?.SetActive(false);
        }
        catch (Exception e)
        {
            SetStatus($"Host failed: {e.Message}");
        }
    }

    private void OnJoinModeClicked()
    {
        _joinSection?.SetActive(true);
        if (_joinCodeDisplay != null) _joinCodeDisplay.text = string.Empty;
        SetStatus("Enter the 6-character code your host shared.");
    }

    private async void OnConnectClicked()
    {
        var code = _joinCodeInput != null ? _joinCodeInput.text.Trim().ToUpper() : string.Empty;
        if (string.IsNullOrEmpty(code)) { SetStatus("Enter a join code first."); return; }
        if (NetworkBootstrapper.Instance == null)
        { SetStatus("NetworkBootstrapper not found."); return; }

        SetStatus("Joining relay…");

        try
        {
            await NetworkBootstrapper.Instance.StartClientAsync(code);
            SetStatus("Connected!");
        }
        catch (Exception e)
        {
            SetStatus($"Join failed: {e.Message}");
        }
    }

    private void SetStatus(string msg)
    {
        if (_statusText != null) _statusText.text = msg;
        Debug.Log($"[Net UI] {msg}");
    }

    // ── UI factory helpers ────────────────────────────────────────────────────

    private static GameObject MakePanel(string name, Transform parent, Color col)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = col;
        return go;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string text, float size,
        TextAlignmentOptions align,
        float ax0, float ay0, float ax1, float ay1, Color col)
    {
        var go = new GameObject("Txt", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(ax0, ay0);
        rt.anchorMax = new Vector2(ax1, ay1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.color = col;
        return tmp;
    }

    private static Button MakeButton(Transform parent, string label,
        float ax0, float ay0, float ax1, float ay1, Color bgCol)
    {
        var bg = MakePanel("Btn_" + label, parent, bgCol);
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(ax0, ay0);
        rt.anchorMax = new Vector2(ax1, ay1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var btn = bg.AddComponent<Button>();
        btn.targetGraphic = bg.GetComponent<Image>();

        var lgo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        lgo.transform.SetParent(bg.transform, false);
        StretchFull(lgo.GetComponent<RectTransform>());
        var tmp = lgo.GetComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 20;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;

        var cols = btn.colors;
        cols.highlightedColor = new Color(
            Mathf.Min(bgCol.r + 0.15f, 1f),
            Mathf.Min(bgCol.g + 0.15f, 1f),
            Mathf.Min(bgCol.b + 0.15f, 1f));
        cols.pressedColor = new Color(
            Mathf.Max(bgCol.r - 0.1f, 0f),
            Mathf.Max(bgCol.g - 0.1f, 0f),
            Mathf.Max(bgCol.b - 0.1f, 0f));
        btn.colors = cols;

        return btn;
    }

    private static TMP_InputField MakeInputField(Transform parent, string placeholder,
        float ax0, float ay0, float ax1, float ay1)
    {
        var bg = MakePanel("InputField", parent, new Color(0.14f, 0.14f, 0.20f));
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(ax0, ay0);
        rt.anchorMax = new Vector2(ax1, ay1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Text viewport with masking
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(bg.transform, false);
        var vrt = viewport.GetComponent<RectTransform>();
        vrt.anchorMin = new Vector2(0.04f, 0f);
        vrt.anchorMax = new Vector2(0.96f, 1f);
        vrt.offsetMin = vrt.offsetMax = Vector2.zero;

        // Placeholder
        var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
        phGo.transform.SetParent(viewport.transform, false);
        StretchFull(phGo.GetComponent<RectTransform>());
        var ph = phGo.GetComponent<TextMeshProUGUI>();
        ph.text = placeholder;
        ph.fontSize = 20;
        ph.color = new Color(0.45f, 0.45f, 0.45f);
        ph.alignment = TextAlignmentOptions.MidlineLeft;
        ph.fontStyle = FontStyles.Italic;

        // Input text
        var inGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        inGo.transform.SetParent(viewport.transform, false);
        StretchFull(inGo.GetComponent<RectTransform>());
        var inTmp = inGo.GetComponent<TextMeshProUGUI>();
        inTmp.text = string.Empty;
        inTmp.fontSize = 20;
        inTmp.color = Color.white;
        inTmp.alignment = TextAlignmentOptions.MidlineLeft;

        var field = bg.AddComponent<TMP_InputField>();
        field.textViewport  = viewport.GetComponent<RectTransform>();
        field.textComponent = inTmp;
        field.placeholder   = ph;
        field.characterLimit = 10;

        return field;
    }

    private static void MakeDivider(Transform parent, float anchorY)
    {
        var go = MakePanel("Divider", parent, new Color(1f, 1f, 1f, 0.10f));
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, anchorY);
        rt.anchorMax = new Vector2(0.95f, anchorY);
        rt.sizeDelta = new Vector2(0f, 1f);
        go.GetComponent<Image>().raycastTarget = false;
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
