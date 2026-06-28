using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Util;

/// <summary>
/// Builds and displays a full-screen post-match stats overlay at runtime.
/// No prefab or scene setup required — call PostMatchStatsUI.Show() to open.
/// </summary>
public static class PostMatchStatsUI
{
    private static GameObject _root;

    // ── Controller navigation state ───────────────────────────────────────────
    private static Button[] _navButtons;
    private static Color[]  _navButtonColors;
    private static int      _selectedIndex;

    // ── Colours ───────────────────────────────────────────────────────────────
    private static readonly Color BlueAlliance  = new Color(0.25f, 0.55f, 1.00f);
    private static readonly Color RedAlliance   = new Color(1.00f, 0.30f, 0.30f);
    private static readonly Color HeaderBg      = new Color(0.12f, 0.12f, 0.16f, 1f);
    private static readonly Color RowAlt        = new Color(0.10f, 0.10f, 0.13f, 1f);
    private static readonly Color RowNormal     = new Color(0.14f, 0.14f, 0.18f, 1f);
    private static readonly Color TotalRowBg    = new Color(0.08f, 0.08f, 0.10f, 1f);
    private static readonly Color EPAColour     = new Color(1.00f, 0.85f, 0.25f);
    private static readonly Color PanelBg       = new Color(0.07f, 0.07f, 0.09f, 0.97f);
    private static readonly Color Overlay       = new Color(0f, 0f, 0f, 0.75f);

    public static void Show()
    {
        if (_root != null) Object.Destroy(_root);

        var loadMatch = Object.FindFirstObjectByType<LoadMatch>();
        var settings  = loadMatch != null ? loadMatch.GetSettingsCopy() : null;
        var playMode  = settings?.playMode ?? Util.PlayMode.OneVsZero;

        int blueCount = PostMatchStats.BlueRobotCount(playMode);
        int redCount  = PostMatchStats.RedRobotCount(playMode);

        // ── Canvas setup ─────────────────────────────────────────────────────
        _root = new GameObject("PostMatchStatsUI");
        Object.DontDestroyOnLoad(_root);

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        _root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _root.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920, 1080);
        _root.AddComponent<GraphicRaycaster>();

        // ── Dark overlay ─────────────────────────────────────────────────────
        var overlay = CreatePanel(_root.transform, "Overlay", Overlay);
        Stretch(overlay.GetComponent<RectTransform>());

        // ── Centred panel (880 × 640) ─────────────────────────────────────────
        var panel = CreatePanel(overlay.transform, "Panel", PanelBg);
        var panelRT = panel.GetComponent<RectTransform>();
        panelRT.anchorMin = panelRT.anchorMax = panelRT.pivot = new Vector2(0.5f, 0.5f);
        panelRT.anchoredPosition = Vector2.zero;
        panelRT.sizeDelta = new Vector2(920, 660);

        // Vertical layout for the whole panel
        var vLayout = panel.AddComponent<VerticalLayoutGroup>();
        vLayout.padding = new RectOffset(24, 24, 20, 20);
        vLayout.spacing = 8;
        vLayout.childForceExpandWidth  = true;
        vLayout.childForceExpandHeight = false;
        vLayout.childAlignment = TextAnchor.UpperCenter;
        panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // ── Title ─────────────────────────────────────────────────────────────
        AddTitle(panel.transform, "MATCH RESULTS", 28, Color.white);
        AddSpacer(panel.transform, 6);

        // ── Blue alliance section ─────────────────────────────────────────────
        string[] blueRobotNames = GetRobotNames(loadMatch, settings, true, blueCount);
        AddAllianceSection(panel.transform, "Blue Alliance", BlueAlliance,
                           blueRobotNames, true,
                           PostMatchStats.BlueAuto,
                           PostMatchStats.BlueTeleop,
                           PostMatchStats.BlueEndgame,
                           PostMatchStats.BlueTotal,
                           blueCount);

        // ── Red alliance section (only when red robots exist) ─────────────────
        if (redCount > 0)
        {
            AddSpacer(panel.transform, 6);
            string[] redRobotNames = GetRobotNames(loadMatch, settings, false, redCount);
            AddAllianceSection(panel.transform, "Red Alliance", RedAlliance,
                               redRobotNames, false,
                               PostMatchStats.RedAuto,
                               PostMatchStats.RedTeleop,
                               PostMatchStats.RedEndgame,
                               PostMatchStats.RedTotal,
                               redCount);
        }

        // ── EPA section ───────────────────────────────────────────────────────
        AddSpacer(panel.transform, 8);
        AddDivider(panel.transform);
        AddSpacer(panel.transform, 4);
        AddEPASection(panel.transform, playMode, blueCount, redCount);

        // ── Buttons ───────────────────────────────────────────────────────────
        AddSpacer(panel.transform, 10);
        AddButtonRow(panel.transform);
    }

    public static bool IsOpen => _root != null;

    public static void Close()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
    }

    // ── Alliance section ──────────────────────────────────────────────────────

    private static void AddAllianceSection(Transform parent, string title, Color allianceColor,
        string[] robotNames, bool isBlue, int autoScore, int teleopScore, int endgameScore, int total, int count)
    {
        // Section header
        var header = CreatePanel(parent, "SectionHeader", new Color(allianceColor.r * 0.3f,
                                                                      allianceColor.g * 0.3f,
                                                                      allianceColor.b * 0.3f, 1f));
        FixedHeight(header.GetComponent<RectTransform>(), 28);
        AddText(header.transform, title, 15, allianceColor, bold: true)
            .GetComponent<RectTransform>().anchoredPosition = new Vector2(8, 0);

        // Column header row
        AddTableRow(parent, HeaderBg, allianceColor,
                    "Player", "Auto", "Teleop", "Endgame", "Total", bold: true);

        // Per-robot rows — actual tracked scores when available, equal split otherwise
        int baseSlot = isBlue ? 0 : 2;
        for (int i = 0; i < count; i++)
        {
            int slot = baseSlot + i;
            string name = robotNames != null && i < robotNames.Length ? robotNames[i] : $"Robot {i + 1}";

            int ta = PostMatchStats.GetSlotScore(slot, 0);
            int tt = PostMatchStats.GetSlotScore(slot, 1);
            int te = PostMatchStats.GetSlotScore(slot, 2);
            bool hasTracked = ta + tt + te > 0;

            string auto   = hasTracked ? ta.ToString()              : FormatSplit(autoScore,    count);
            string teleop = hasTracked ? tt.ToString()              : FormatSplit(teleopScore,  count);
            string end    = hasTracked ? te.ToString()              : FormatSplit(endgameScore, count);
            string tot    = hasTracked ? (ta+tt+te).ToString()      : FormatSplit(total,        count);
            AddTableRow(parent, i % 2 == 0 ? RowNormal : RowAlt,
                        Color.white, name, auto, teleop, end, tot);
        }

        // Alliance total row
        AddTableRow(parent, TotalRowBg, allianceColor,
                    "ALLIANCE TOTAL",
                    autoScore.ToString(), teleopScore.ToString(),
                    endgameScore.ToString(), total.ToString(), bold: true);
    }

    // ── EPA section ───────────────────────────────────────────────────────────

    private static void AddEPASection(Transform parent, Util.PlayMode mode, int blueCount, int redCount)
    {
        AddTitle(parent, "CAREER EPA  (avg pts / match)", 16, EPAColour);
        AddSpacer(parent, 2);
        AddTableRow(parent, HeaderBg, EPAColour,
                    "Slot", "Auto", "Teleop", "Endgame", "Total EPA", bold: true,
                    extraLabel: "Matches", extraColor: EPAColour);

        int totalSlots = blueCount + redCount;  // max 4
        for (int i = 0; i < 4; i++)
        {
            // Only show slots active in this play mode
            bool isBlueSlot = i < 2;
            if (isBlueSlot  && i >= blueCount) continue;
            if (!isBlueSlot && (i - 2) >= redCount) continue;

            var (a, t, e, tot, n) = PostMatchStats.GetSlotEPA(i);
            Color col = i < 2 ? BlueAlliance : RedAlliance;
            AddTableRow(parent, i % 2 == 0 ? RowNormal : RowAlt, col,
                        PostMatchStats.SlotLabels[i],
                        n > 0 ? $"{a:F1}" : "—",
                        n > 0 ? $"{t:F1}" : "—",
                        n > 0 ? $"{e:F1}" : "—",
                        n > 0 ? $"{tot:F1}" : "—",
                        extraLabel: n > 0 ? n.ToString() : "0");
        }
    }

    // ── Button row ────────────────────────────────────────────────────────────

    private static void AddButtonRow(Transform parent)
    {
        var row = new GameObject("Buttons");
        row.transform.SetParent(parent, false);
        FixedHeight(row.AddComponent<RectTransform>(), 44);

        var hLayout = row.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = 16;
        hLayout.childForceExpandWidth  = false;
        hLayout.childForceExpandHeight = true;
        hLayout.childAlignment = TextAnchor.MiddleCenter;

        var closeColor = new Color(0.2f, 0.6f, 0.2f);
        var clearColor = new Color(0.5f, 0.2f, 0.2f);

        var btnClose = CreateButton(row.transform, "  Close Stats  ", closeColor, Close);
        var btnClear = CreateButton(row.transform, "  Clear EPA  ",  clearColor, () =>
        {
            PostMatchStats.ClearAllEPA();
            Close();
            PostMatchStatsUI.Show();
        });

        _navButtons      = new[] { btnClose, btnClear };
        _navButtonColors = new[] { closeColor, clearColor };
        _selectedIndex   = 0;
        RefreshButtonHighlight();

        // Attach the per-frame input poller to the canvas root
        _root.AddComponent<StatsInputHandler>();
    }

    internal static void NavigateButtons(int delta)
    {
        if (_navButtons == null) return;
        _selectedIndex = (_selectedIndex + delta + _navButtons.Length) % _navButtons.Length;
        RefreshButtonHighlight();
    }

    internal static void ConfirmSelected()
    {
        if (_navButtons == null || _selectedIndex >= _navButtons.Length) return;
        _navButtons[_selectedIndex].onClick.Invoke();
    }

    private static void RefreshButtonHighlight()
    {
        if (_navButtons == null) return;
        for (int i = 0; i < _navButtons.Length; i++)
        {
            var img = _navButtons[i].GetComponent<Image>();
            if (i == _selectedIndex)
            {
                // Brighten + add white outline to show focus
                img.color = new Color(
                    Mathf.Min(_navButtonColors[i].r + 0.25f, 1f),
                    Mathf.Min(_navButtonColors[i].g + 0.25f, 1f),
                    Mathf.Min(_navButtonColors[i].b + 0.25f, 1f));
            }
            else
            {
                img.color = _navButtonColors[i];
            }
        }
    }

    // ── Table row helper ──────────────────────────────────────────────────────

    private static void AddTableRow(Transform parent, Color bg, Color textColor,
        string col0, string col1, string col2, string col3, string col4,
        bool bold = false, string extraLabel = null, Color extraColor = default)
    {
        var row = CreatePanel(parent, "Row", bg);
        FixedHeight(row.GetComponent<RectTransform>(), 26);

        var h = row.AddComponent<HorizontalLayoutGroup>();
        h.childForceExpandHeight = true;
        h.childForceExpandWidth  = false;
        h.padding = new RectOffset(8, 8, 0, 0);

        // col widths: name=220, auto=110, teleop=110, endgame=110, total=110, (optional extra=100)
        AddCell(row.transform, col0,  220, textColor, bold, TextAlignmentOptions.Left);
        AddCell(row.transform, col1,  110, textColor, bold);
        AddCell(row.transform, col2,  110, textColor, bold);
        AddCell(row.transform, col3,  110, textColor, bold);
        AddCell(row.transform, col4,  110, textColor, bold);

        if (extraLabel != null)
            AddCell(row.transform, extraLabel, 100,
                    extraColor == default ? textColor : extraColor, false);
    }

    private static void AddCell(Transform parent, string text, float width,
        Color color, bool bold = false, TextAlignmentOptions align = TextAlignmentOptions.Center)
    {
        var go = new GameObject("Cell");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, 0);

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth  = width;
        le.flexibleWidth   = 0;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = 13;
        tmp.color     = color;
        tmp.alignment = align;
        tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
    }

    // ── Generic helpers ───────────────────────────────────────────────────────

    private static void AddTitle(Transform parent, string text, float size, Color color)
    {
        var go = new GameObject("Title");
        go.transform.SetParent(parent, false);
        FixedHeight(go.AddComponent<RectTransform>(), size + 8);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = size;
        tmp.color     = color;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
    }

    private static void AddDivider(Transform parent)
    {
        var go = CreatePanel(parent, "Divider", new Color(0.3f, 0.3f, 0.3f, 1f));
        FixedHeight(go.GetComponent<RectTransform>(), 1);
    }

    private static void AddSpacer(Transform parent, float height)
    {
        var go = new GameObject("Spacer");
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight       = height;
    }

    private static GameObject CreatePanel(Transform parent, string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        return go;
    }

    private static GameObject AddText(Transform parent, string text, float size, Color color, bool bold = false)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = size;
        tmp.color     = color;
        tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        return go;
    }

    private static Button CreateButton(Transform parent, string label, Color bgColor,
        UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Button_" + label.Trim());
        go.transform.SetParent(parent, false);

        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth  = 160;
        le.preferredHeight = 40;

        var img = go.AddComponent<Image>();
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(bgColor.r + 0.15f, bgColor.g + 0.15f, bgColor.b + 0.15f);
        colors.pressedColor     = new Color(bgColor.r - 0.1f,  bgColor.g - 0.1f,  bgColor.b - 0.1f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        var txt = new GameObject("Label");
        txt.transform.SetParent(go.transform, false);
        var rt = txt.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var tmp = txt.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 14;
        tmp.color     = Color.white;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;

        return btn;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin  = Vector2.zero;
        rt.anchorMax  = Vector2.one;
        rt.offsetMin  = rt.offsetMax = Vector2.zero;
    }

    private static void FixedHeight(RectTransform rt, float h)
    {
        var le = rt.gameObject.GetComponent<LayoutElement>() ??
                 rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = h;
        le.minHeight       = h;
    }

    private static string FormatSplit(int allianceScore, int count)
        => count <= 0 ? "0" : $"{(float)allianceScore / count:F1}";

    private static string[] GetRobotNames(LoadMatch loadMatch, MatchSettings settings,
        bool blue, int count)
    {
        if (loadMatch == null || settings == null) return null;
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            int robotIndex = blue
                ? (i == 0 ? settings.robotIndex1 : settings.robotIndex2)
                : (i == 0 ? settings.robotIndex3 : settings.robotIndex4);
            names[i] = loadMatch.GetRobotNameAt(robotIndex);
        }
        // Disambiguate when two entries share the same robot name.
        if (count >= 2 && names[0] == names[1])
        {
            names[0] += " (P1)";
            names[1] += " (P2)";
        }
        return names;
    }
}

// ── Controller / keyboard input handler ──────────────────────────────────────
// Attached to the stats canvas root so it lives and dies with the UI.

public class StatsInputHandler : MonoBehaviour
{
    private void Update()
    {
        // D-pad up is handled globally by PostMatchStats — only cycle + confirm here
        bool down    = false;
        bool confirm = false;

        foreach (var gp in Gamepad.all)
        {
            down    |= gp.dpad.down.wasPressedThisFrame;
            confirm |= gp.buttonSouth.wasPressedThisFrame;
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            down    |= kb.downArrowKey.wasPressedThisFrame;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame;
        }

        if (down)    PostMatchStatsUI.NavigateButtons(+1);
        if (confirm) PostMatchStatsUI.ConfirmSelected();
    }
}
