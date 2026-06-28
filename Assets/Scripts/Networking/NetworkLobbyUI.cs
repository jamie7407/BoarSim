using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Util;

// Full multiplayer lobby UI.
// Attach to any GameObject; OptionsMenuController creates it at runtime.
// Provides a two-screen flow: pre-lobby (username + host/join) then lobby
// (slot overview, robot select, spawn select, mode select, start match).
public class NetworkLobbyUI : MonoBehaviour
{
    // ── External events ───────────────────────────────────────────────────────
    public event Action OnBackClicked;

    // ── NGO message names ─────────────────────────────────────────────────────
    const string MSG_PLAYER_STATE = "LobbyPlayer";   // client → host: my slot data
    const string MSG_BROADCAST    = "LobbyBcast";    // host → all: full lobby state
    const string MSG_ASSIGN_SLOT  = "LobbySlot";     // host → joining client: your slot number

    // ── Lobby data ────────────────────────────────────────────────────────────
    [Serializable]
    struct SlotData
    {
        public bool   connected;
        public string username;
        public int    robotIndex;
        public int    spawnIndex;
        public bool   isBlue;     // determined by slot + mode

        public static SlotData Empty => new() { username = "Waiting…" };
    }

    const int MaxSlots = 4;
    readonly SlotData[] _slots = new SlotData[MaxSlots];
    int _mySlot = 0;
    int _modeIdx = 0;           // index into _modes

    readonly (Util.PlayMode mode, string label, int players)[] _modes =
    {
        (Util.PlayMode.TwoVsZero, "2v0",  2),
        (Util.PlayMode.OneVsOne,  "1v1",  2),
        (Util.PlayMode.TwoVsTwo,  "2v2",  4),
    };

    // clientId → slot (host-only map)
    readonly Dictionary<ulong, int> _clientSlotMap = new();
    int _nextSlot = 1;

    // ── Canvas / target ───────────────────────────────────────────────────────
    private Canvas _targetCanvas;
    public void SetTargetCanvas(Canvas c) { _targetCanvas = c; }

    // ── Root panels ───────────────────────────────────────────────────────────
    GameObject _rootPanel;
    GameObject _preLobbyRoot;
    GameObject _lobbyRoot;

    // ── Pre-lobby refs ────────────────────────────────────────────────────────
    TMP_InputField _usernameInput;
    TMP_InputField _joinCodeInput;
    GameObject     _joinRow;
    TextMeshProUGUI _preLobbyStatus;

    // ── Lobby refs ────────────────────────────────────────────────────────────
    TextMeshProUGUI _modeLabel;
    Button          _modePrev, _modeNext;
    TextMeshProUGUI _joinCodeDisplay;
    Button          _startBtn;
    TextMeshProUGUI _lobbyStatus;

    // Per-slot row data
    struct SlotRowUI
    {
        public GameObject   root;
        public Image        dot;
        public TextMeshProUGUI nameLabel;
        public Image        robotIcon;
        public TextMeshProUGUI robotLabel;
        public Button       robotPrev, robotNext;
        public TextMeshProUGUI spawnLabel;
        public Button       spawnPrev, spawnNext;
        public TextMeshProUGUI teamLabel; // BLUE / RED badge
    }
    readonly SlotRowUI[] _rows = new SlotRowUI[MaxSlots];

    // ── LoadMatch cache ───────────────────────────────────────────────────────
    LoadMatch _loadMatch;
    LoadMatch LM => _loadMatch ??= FindFirstObjectByType<LoadMatch>();

    List<string> _robotNames  = new();
    List<string> _blueSpawns  = new();
    List<string> _redSpawns   = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Open()
    {
        if (_rootPanel == null)
        {
            var canvas = _targetCanvas != null ? _targetCanvas : FindFirstObjectByType<Canvas>();
            if (canvas == null) return;
            BuildAll(canvas);
        }
        _rootPanel.SetActive(true);
        LoadPlayerPrefs();
        RefreshNames();
        ShowPreLobby();
    }

    public void Close()
    {
        DeregisterMessages();
        UnregisterNetworkCallbacks();
        if (_rootPanel != null) _rootPanel.SetActive(false);
        // OpenMultiplayer() pauses time (timeScale=0) when opened from the options menu.
        // Restore it here so SetupInputsWhenReady (uses Time.time) can actually finish.
        Time.timeScale = 1f;
    }

    public bool IsOpen => _rootPanel != null && _rootPanel.activeSelf;

    void OnDestroy() { DeregisterMessages(); UnregisterNetworkCallbacks(); }

    // ── Screen transitions ────────────────────────────────────────────────────

    void ShowPreLobby()
    {
        _preLobbyRoot?.SetActive(true);
        _lobbyRoot?.SetActive(false);
        _joinRow?.SetActive(false);
        if (_preLobbyStatus != null) _preLobbyStatus.text = string.Empty;
    }

    void ShowLobby(bool isHost)
    {
        _preLobbyRoot?.SetActive(false);
        _lobbyRoot?.SetActive(true);

        // Slot 0 is always the host
        _slots[0].connected = true;
        _slots[0].username  = MyUsername;
        _slots[0].robotIndex = LM != null ? Mathf.Clamp(LM.GetSettingsCopy().robotIndex1, 0, Mathf.Max(0, _robotNames.Count - 1)) : 0;
        _slots[0].spawnIndex = 0;
        _slots[0].isBlue     = true;

        if (!isHost)
        {
            // Clients set up their own slot once MSG_ASSIGN_SLOT arrives;
            // until then show a placeholder
            for (int i = 1; i < MaxSlots; i++) _slots[i] = SlotData.Empty;
        }
        else
        {
            for (int i = 1; i < MaxSlots; i++) _slots[i] = SlotData.Empty;
        }

        // Mode controls visible only to host
        bool modeControlsVisible = isHost;
        if (_modePrev != null) _modePrev.gameObject.SetActive(modeControlsVisible);
        if (_modeNext != null) _modeNext.gameObject.SetActive(modeControlsVisible);
        if (_startBtn  != null) _startBtn.gameObject.SetActive(isHost);
        if (_joinCodeDisplay != null)
            _joinCodeDisplay.gameObject.SetActive(isHost);

        RefreshLobbyView();
    }

    // ── Build UI ──────────────────────────────────────────────────────────────

    void BuildAll(Canvas canvas)
    {
        _rootPanel = MakePanel("LobbyRoot", canvas.transform, new Color(0.07f, 0.07f, 0.11f, 0.98f));
        StretchFull(_rootPanel.GetComponent<RectTransform>());

        BuildPreLobby(_rootPanel.transform);
        BuildLobby(_rootPanel.transform);
    }

    void BuildPreLobby(Transform parent)
    {
        _preLobbyRoot = new GameObject("PreLobby", typeof(RectTransform));
        _preLobbyRoot.transform.SetParent(parent, false);
        StretchFull(_preLobbyRoot.GetComponent<RectTransform>());

        MakeText(_preLobbyRoot.transform, "ONLINE PLAY", 36,
            TextAlignmentOptions.Center, 0.1f, 0.86f, 0.9f, 0.96f, Color.white);
        MakeDivider(_preLobbyRoot.transform, 0.84f);

        MakeText(_preLobbyRoot.transform, "YOUR NAME", 14,
            TextAlignmentOptions.Left, 0.1f, 0.76f, 0.5f, 0.82f, new Color(0.6f, 0.6f, 0.6f));
        _usernameInput = MakeInputField(_preLobbyRoot.transform, "Enter username…",
            0.1f, 0.68f, 0.9f, 0.76f);
        _usernameInput.characterLimit = 20;

        MakeDivider(_preLobbyRoot.transform, 0.66f);

        var hostBtn = MakeButton(_preLobbyRoot.transform, "HOST GAME",
            0.1f, 0.55f, 0.46f, 0.65f, new Color(0.14f, 0.47f, 0.82f));
        hostBtn.onClick.AddListener(OnHostClicked);

        var joinBtn = MakeButton(_preLobbyRoot.transform, "JOIN GAME",
            0.54f, 0.55f, 0.90f, 0.65f, new Color(0.18f, 0.60f, 0.32f));
        joinBtn.onClick.AddListener(OnJoinModeClicked);

        _joinRow = new GameObject("JoinRow", typeof(RectTransform));
        _joinRow.transform.SetParent(_preLobbyRoot.transform, false);
        var jr = _joinRow.GetComponent<RectTransform>();
        jr.anchorMin = new Vector2(0.1f, 0.43f);
        jr.anchorMax = new Vector2(0.9f, 0.54f);
        jr.offsetMin = jr.offsetMax = Vector2.zero;

        _joinCodeInput = MakeInputField(_joinRow.transform, "Enter 6-character code…", 0f, 0f, 0.65f, 1f);
        _joinCodeInput.characterLimit = 6;
        var connectBtn = MakeButton(_joinRow.transform, "CONNECT", 0.67f, 0f, 1f, 1f,
            new Color(0.18f, 0.60f, 0.32f));
        connectBtn.onClick.AddListener(OnConnectClicked);
        _joinRow.SetActive(false);

        MakeDivider(_preLobbyRoot.transform, 0.41f);

        _preLobbyStatus = MakeText(_preLobbyRoot.transform, string.Empty, 16,
            TextAlignmentOptions.Center, 0.1f, 0.32f, 0.9f, 0.41f,
            new Color(0.75f, 0.75f, 0.75f));

        MakeDivider(_preLobbyRoot.transform, 0.30f);

        var backBtn = MakeButton(_preLobbyRoot.transform, "← BACK",
            0.1f, 0.18f, 0.42f, 0.28f, new Color(0.30f, 0.30f, 0.36f));
        backBtn.onClick.AddListener(() => OnBackClicked?.Invoke());
    }

    void BuildLobby(Transform parent)
    {
        _lobbyRoot = new GameObject("Lobby", typeof(RectTransform));
        _lobbyRoot.transform.SetParent(parent, false);
        StretchFull(_lobbyRoot.GetComponent<RectTransform>());

        // Title + mode selector row
        MakeText(_lobbyRoot.transform, "LOBBY", 32,
            TextAlignmentOptions.Center, 0.1f, 0.88f, 0.55f, 0.97f, Color.white);

        _modePrev = MakeButton(_lobbyRoot.transform, "<", 0.57f, 0.89f, 0.63f, 0.96f,
            new Color(0.25f, 0.25f, 0.32f));
        _modePrev.onClick.AddListener(() => { _modeIdx = (_modeIdx - 1 + _modes.Length) % _modes.Length; SendBroadcast(); RefreshLobbyView(); });

        _modeLabel = MakeText(_lobbyRoot.transform, "1v1", 22,
            TextAlignmentOptions.Center, 0.64f, 0.89f, 0.81f, 0.96f,
            new Color(1f, 0.85f, 0.2f));

        _modeNext = MakeButton(_lobbyRoot.transform, ">", 0.82f, 0.89f, 0.88f, 0.96f,
            new Color(0.25f, 0.25f, 0.32f));
        _modeNext.onClick.AddListener(() => { _modeIdx = (_modeIdx + 1) % _modes.Length; SendBroadcast(); RefreshLobbyView(); });

        MakeDivider(_lobbyRoot.transform, 0.87f);

        // Four player slot rows
        float rowH = 0.155f;
        float[] rowTops = { 0.84f, 0.68f, 0.52f, 0.36f };
        string[] slotLabels = { "P1 (HOST)", "P2", "P3", "P4" };
        for (int i = 0; i < MaxSlots; i++)
        {
            _rows[i] = BuildSlotRow(_lobbyRoot.transform, i,
                slotLabels[i], rowTops[i] - rowH, rowTops[i]);
        }

        MakeDivider(_lobbyRoot.transform, 0.325f);

        _joinCodeDisplay = MakeText(_lobbyRoot.transform, string.Empty, 20,
            TextAlignmentOptions.Center, 0.1f, 0.24f, 0.9f, 0.32f,
            new Color(0.8f, 0.8f, 0.5f));

        _lobbyStatus = MakeText(_lobbyRoot.transform, string.Empty, 16,
            TextAlignmentOptions.Center, 0.1f, 0.15f, 0.9f, 0.23f,
            new Color(0.6f, 0.6f, 0.6f));

        _startBtn = MakeButton(_lobbyRoot.transform, "> START MATCH",
            0.1f, 0.03f, 0.58f, 0.13f, new Color(0.14f, 0.55f, 0.24f));
        _startBtn.onClick.AddListener(OnStartMatchClicked);

        var leaveBtn = MakeButton(_lobbyRoot.transform, "LEAVE",
            0.62f, 0.03f, 0.90f, 0.13f, new Color(0.40f, 0.15f, 0.15f));
        leaveBtn.onClick.AddListener(OnLeaveClicked);

        _lobbyRoot.SetActive(false);
    }

    SlotRowUI BuildSlotRow(Transform parent, int slot, string label, float yMin, float yMax)
    {
        var root = new GameObject($"Slot{slot}", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.02f, yMin);
        rt.anchorMax = new Vector2(0.98f, yMax);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Background tint
        var bg = root.AddComponent<Image>();
        bg.color = new Color(0.12f, 0.12f, 0.18f, 0.6f);

        // Connection dot
        var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        dotGo.transform.SetParent(root.transform, false);
        var drt = dotGo.GetComponent<RectTransform>();
        drt.anchorMin = new Vector2(0.01f, 0.3f);
        drt.anchorMax = new Vector2(0.04f, 0.7f);
        drt.offsetMin = drt.offsetMax = Vector2.zero;
        var dot = dotGo.GetComponent<Image>();
        dot.color = Color.gray;

        // Slot label + username
        var nameLabel = MakeText(root.transform, label, 16,
            TextAlignmentOptions.Left, 0.05f, 0f, 0.30f, 1f,
            Color.white);

        // Robot preview icon
        var iconGo = new GameObject("RobotIcon", typeof(RectTransform), typeof(Image));
        iconGo.transform.SetParent(root.transform, false);
        var irt = iconGo.GetComponent<RectTransform>();
        irt.anchorMin = new Vector2(0.31f, 0.05f);
        irt.anchorMax = new Vector2(0.39f, 0.95f);
        irt.offsetMin = irt.offsetMax = Vector2.zero;
        var robotIcon = iconGo.GetComponent<Image>();
        robotIcon.preserveAspect = true;
        robotIcon.color = Color.white;
        robotIcon.enabled = false;

        // Robot selector (right side, top half)
        var robotPrev = MakeButton(root.transform, "<", 0.40f, 0.5f, 0.47f, 1f,
            new Color(0.2f, 0.2f, 0.28f));
        robotPrev.onClick.AddListener(() => CycleRobot(slot, -1));

        var robotLabel = MakeText(root.transform, "—", 18,
            TextAlignmentOptions.Center, 0.48f, 0.5f, 0.76f, 1f,
            new Color(0.9f, 0.9f, 0.9f));

        var robotNext = MakeButton(root.transform, ">", 0.77f, 0.5f, 0.84f, 1f,
            new Color(0.2f, 0.2f, 0.28f));
        robotNext.onClick.AddListener(() => CycleRobot(slot, +1));

        // Spawn selector (right side, bottom half)
        var spawnPrev = MakeButton(root.transform, "<", 0.40f, 0f, 0.47f, 0.5f,
            new Color(0.2f, 0.2f, 0.28f));
        spawnPrev.onClick.AddListener(() => CycleSpawn(slot, -1));

        var spawnLabel = MakeText(root.transform, "—", 16,
            TextAlignmentOptions.Center, 0.48f, 0f, 0.76f, 0.5f,
            new Color(0.7f, 0.7f, 0.85f));

        var spawnNext = MakeButton(root.transform, ">", 0.77f, 0f, 0.84f, 0.5f,
            new Color(0.2f, 0.2f, 0.28f));
        spawnNext.onClick.AddListener(() => CycleSpawn(slot, +1));

        // Team badge (BLUE / RED) — right edge
        var teamLabel = MakeText(root.transform, "—", 14,
            TextAlignmentOptions.Center, 0.85f, 0f, 1.0f, 1f,
            new Color(0.6f, 0.6f, 0.7f));

        return new SlotRowUI
        {
            root = root, dot = dot, nameLabel = nameLabel, robotIcon = robotIcon,
            robotLabel = robotLabel, robotPrev = robotPrev, robotNext = robotNext,
            spawnLabel = spawnLabel, spawnPrev = spawnPrev, spawnNext = spawnNext,
            teamLabel = teamLabel,
        };
    }

    // Returns whether a given slot is on the Blue alliance for the specified mode.
    static bool GetIsBlueForSlot(int slot, Util.PlayMode mode) => mode switch
    {
        Util.PlayMode.TwoVsTwo => slot < 2,
        Util.PlayMode.OneVsOne => slot == 0,
        _                      => true   // TwoVsZero, OneVsZero: all Blue
    };

    // ── Names / PlayerPrefs ───────────────────────────────────────────────────

    string MyUsername => _usernameInput != null
        ? (_usernameInput.text.Trim().Length > 0 ? _usernameInput.text.Trim() : "Player")
        : "Player";

    void LoadPlayerPrefs()
    {
        if (_usernameInput == null) return;
        _usernameInput.text = PlayerPrefs.GetString("mp_username", string.Empty);
    }

    void SavePlayerPrefs()
    {
        PlayerPrefs.SetString("mp_username", MyUsername);
        PlayerPrefs.Save();
    }

    void RefreshNames()
    {
        if (LM == null) return;
        _robotNames = LM.GetAvailableRobotNames();
        _blueSpawns = LM.GetBlueSpawnNames();
        _redSpawns  = LM.GetRedSpawnNames();
    }

    // ── Slot selection helpers ────────────────────────────────────────────────

    void CycleRobot(int slot, int dir)
    {
        if (slot != _mySlot) return;
        if (_robotNames.Count == 0) return;
        _slots[slot].robotIndex = (_slots[slot].robotIndex + dir + _robotNames.Count) % _robotNames.Count;
        SendMyState();
        RefreshLobbyView();
    }

    void CycleSpawn(int slot, int dir)
    {
        if (slot != _mySlot) return;
        bool isBlue = GetIsBlueForSlot(slot, _modes[_modeIdx].mode);
        var list = isBlue ? _blueSpawns : _redSpawns;
        if (list.Count == 0) return;
        _slots[slot].spawnIndex = (_slots[slot].spawnIndex + dir + list.Count) % list.Count;
        SendMyState();
        RefreshLobbyView();
    }

    // ── Refresh lobby display ─────────────────────────────────────────────────

    void RefreshLobbyView()
    {
        if (_modeLabel != null)
            _modeLabel.text = _modes[_modeIdx].label;

        int activePlayers = _modes[_modeIdx].players;

        for (int i = 0; i < MaxSlots; i++)
        {
            bool visible = i < activePlayers;
            _rows[i].root?.SetActive(visible);
            if (!visible) continue;

            var s = _slots[i];
            bool isOwn = i == _mySlot;

            // Dot colour
            if (_rows[i].dot != null)
                _rows[i].dot.color = s.connected ? Color.green : Color.gray;

            // Name label: slot + username
            string slotTag = i == 0 ? "P1 (HOST)" : $"P{i + 1}";
            if (isOwn) slotTag += " (you)";
            string displayName = s.connected ? $"{slotTag}\n{s.username}" : $"{slotTag}\n—";
            if (_rows[i].nameLabel != null) _rows[i].nameLabel.text = displayName;

            // Robot
            string robotName = (_robotNames.Count > 0 && s.robotIndex < _robotNames.Count)
                ? _robotNames[s.robotIndex] : "—";
            if (_rows[i].robotLabel != null)
                _rows[i].robotLabel.text = robotName;
            // Show arrows only for own slot
            _rows[i].robotPrev?.gameObject.SetActive(isOwn);
            _rows[i].robotNext?.gameObject.SetActive(isOwn);

            // Robot icon
            if (_rows[i].robotIcon != null && LM != null && s.connected)
            {
                var spr = LM.GetRobotPreviewSpriteAt(s.robotIndex);
                _rows[i].robotIcon.sprite  = spr;
                _rows[i].robotIcon.enabled = spr != null;
            }
            else if (_rows[i].robotIcon != null && !s.connected)
            {
                _rows[i].robotIcon.enabled = false;
            }

            // Team badge
            var currentMode = _modes[_modeIdx].mode;
            bool isBlue = GetIsBlueForSlot(i, currentMode);
            if (_rows[i].teamLabel != null)
            {
                _rows[i].teamLabel.text  = isBlue ? "BLUE" : "RED";
                _rows[i].teamLabel.color = isBlue
                    ? new Color(0.3f, 0.6f, 1.0f)
                    : new Color(1.0f, 0.3f, 0.3f);
            }

            // Spawn
            var spawnList = isBlue ? _blueSpawns : _redSpawns;
            string spawnName = (spawnList.Count > 0 && s.spawnIndex < spawnList.Count)
                ? spawnList[s.spawnIndex] : "—";
            string side = isBlue ? "Blue" : "Red";
            if (_rows[i].spawnLabel != null)
                _rows[i].spawnLabel.text = $"{side}: {spawnName}";
            _rows[i].spawnPrev?.gameObject.SetActive(isOwn);
            _rows[i].spawnNext?.gameObject.SetActive(isOwn);
        }

        // Start button interactable only when at least one client is connected
        if (_startBtn != null)
        {
            bool anyClient = false;
            for (int i = 1; i < activePlayers; i++)
                if (_slots[i].connected) { anyClient = true; break; }
            _startBtn.interactable = anyClient;
        }

        // Status
        if (_lobbyStatus != null)
        {
            int needed = activePlayers;
            int connected = 0;
            for (int i = 0; i < needed; i++) if (_slots[i].connected) connected++;
            _lobbyStatus.text = $"{connected}/{needed} players connected";
        }
    }

    // ── Pre-lobby button handlers ─────────────────────────────────────────────

    async void OnHostClicked()
    {
        if (NetworkBootstrapper.Instance == null) { SetPreStatus("NetworkBootstrapper not found."); return; }
        SavePlayerPrefs();
        // Ensure any previous session is fully torn down before starting a new one
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
            await System.Threading.Tasks.Task.Delay(150);
        }
        SetPreStatus("Creating relay…");
        try
        {
            string code = await NetworkBootstrapper.Instance.StartHostAsync(3);
            RegisterMessages(true);
            RegisterNetworkCallbacks();
            _mySlot = 0;
            _clientSlotMap[NetworkManager.Singleton.LocalClientId] = 0;
            _nextSlot = 1;
            ShowLobby(true);
            if (_joinCodeDisplay != null)
                _joinCodeDisplay.text = $"Join Code:  {code}";
        }
        catch (Exception e) { SetPreStatus($"Host failed: {e.Message}"); }
    }

    void OnJoinModeClicked()
    {
        _joinRow?.SetActive(true);
        SetPreStatus("Enter the 6-character code your host shared.");
    }

    async void OnConnectClicked()
    {
        var code = _joinCodeInput != null ? _joinCodeInput.text.Trim().ToUpper() : string.Empty;
        if (string.IsNullOrEmpty(code)) { SetPreStatus("Enter a join code first."); return; }
        if (NetworkBootstrapper.Instance == null) { SetPreStatus("NetworkBootstrapper not found."); return; }
        SavePlayerPrefs();
        // Ensure any previous session is fully torn down before joining a new one
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
            await System.Threading.Tasks.Task.Delay(150);
        }
        SetPreStatus("Joining relay…");
        try
        {
            await NetworkBootstrapper.Instance.StartClientAsync(code);
            RegisterMessages(false);
            // Slot assigned by host via MSG_ASSIGN_SLOT — wait for it
            ShowLobby(false);
            if (_lobbyStatus != null) _lobbyStatus.text = "Waiting for host…";
        }
        catch (Exception e) { SetPreStatus($"Join failed: {e.Message}"); }
    }

    // ── Lobby button handlers ─────────────────────────────────────────────────

    void OnStartMatchClicked()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost) return;
        var settings = BuildMatchSettings();
        var gnm = GameNetworkManager.Instance;
        if (gnm == null) return;

        // Reset the host's field FIRST so FMS.MatchTimer and ScoreHolder are at their start
        // values before BroadcastMatchStart pre-flushes them into NetworkVariables.
        // Without this ordering, the client receives stale end-of-match values (timer=-3,
        // score=old) and re-triggers MatchEndPause at the start of the second match.
        if (LM != null) LM.ApplySettings(settings);
        if (LM != null) LM.ResetField();
        // Block start sound until countdown ends (FMS.Restart sets enabled; override here).
        if (NetworkManager.Singleton?.ConnectedClientsList.Count > 1)
            FMS.RobotState = RobotState.disabled;

        gnm.BroadcastMatchStart(settings);
        Close();
    }

    void OnLeaveClicked()
    {
        // Disconnect and return to pre-lobby
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.IsHost)
                NetworkManager.Singleton.Shutdown();
            else
                NetworkManager.Singleton.Shutdown();
        }
        DeregisterMessages();
        UnregisterNetworkCallbacks();
        ResetLobbyState();
        ShowPreLobby();
    }

    MatchSettings BuildMatchSettings()
    {
        var s = LM != null ? LM.GetSettingsCopy() : new MatchSettings();
        var mode = _modes[_modeIdx].mode;
        s.playMode    = mode;
        s.robotIndex1 = _slots[0].robotIndex;
        s.robotIndex2 = _slots[1].robotIndex;
        s.robotIndex3 = _slots[2].robotIndex;
        s.robotIndex4 = _slots[3].robotIndex;

        if (mode == Util.PlayMode.TwoVsTwo)
        {
            // Slot layout: 0,1 = Blue alliance; 2,3 = Red alliance (matches LoadMatch).
            s.blueSpawnIndex1 = _slots[0].spawnIndex;
            s.blueSpawnIndex2 = _slots[1].spawnIndex;
            s.redSpawnIndex1  = _slots[2].spawnIndex;
            s.redSpawnIndex2  = _slots[3].spawnIndex;
        }
        else
        {
            // 1v1 / 2v0: slot 0 = Blue, slot 1 = Red
            s.blueSpawnIndex1 = _slots[0].spawnIndex;
            s.blueSpawnIndex2 = 0;
            s.redSpawnIndex1  = _slots[1].spawnIndex;
            s.redSpawnIndex2  = 0;
        }

        // Build active-slot bitmask so LoadMatch only spawns robots for connected players.
        byte mask = 0;
        int playerCount = _modes[_modeIdx].players;
        for (int i = 0; i < playerCount; i++)
            if (_slots[i].connected) mask |= (byte)(1 << i);
        s.activeSlotMask = mask;

        return s;
    }

    void ResetLobbyState()
    {
        _clientSlotMap.Clear();
        _nextSlot = 1;
        _mySlot = 0;
        for (int i = 0; i < MaxSlots; i++) _slots[i] = SlotData.Empty;
        var gnm = GameNetworkManager.Instance;
        if (gnm != null) gnm.LocalClientSlot = -1;
    }

    void SetPreStatus(string msg) { if (_preLobbyStatus != null) _preLobbyStatus.text = msg; }

    // ── Network callbacks ─────────────────────────────────────────────────────

    void RegisterNetworkCallbacks()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientConnectedCallback    += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback   += OnClientDisconnected;
    }

    void UnregisterNetworkCallbacks()
    {
        if (NetworkManager.Singleton == null) return;
        NetworkManager.Singleton.OnClientConnectedCallback    -= OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback   -= OnClientDisconnected;
    }

    void OnClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost) return;
        if (clientId == NetworkManager.Singleton.LocalClientId) return;

        // Assign next free slot
        int slot = _nextSlot++;
        if (slot >= MaxSlots) { slot = MaxSlots - 1; }
        _clientSlotMap[clientId] = slot;

        // Tell the client their slot
        AssignSlotToClient(clientId, slot);

        // Mark slot connected with placeholder username
        var assignedMode = _modes[_modeIdx].mode;
        _slots[slot].connected  = true;
        _slots[slot].username   = $"Player {slot + 1}";
        _slots[slot].robotIndex = 0;
        _slots[slot].spawnIndex = 0;
        _slots[slot].isBlue     = GetIsBlueForSlot(slot, assignedMode);

        SendBroadcast();
        RefreshLobbyView();
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost)
        {
            if (_clientSlotMap.TryGetValue(clientId, out int slot))
            {
                _clientSlotMap.Remove(clientId);
                _slots[slot] = SlotData.Empty;
                SendBroadcast();
                RefreshLobbyView();
            }
        }
        else
        {
            // Client was disconnected from host
            ResetLobbyState();
            ShowPreLobby();
            SetPreStatus("Disconnected from host.");
        }
    }

    // ── NGO messages ──────────────────────────────────────────────────────────

    void RegisterMessages(bool isHost)
    {
        var cmm = NetworkManager.Singleton?.CustomMessagingManager;
        if (cmm == null) return;
        if (isHost)
        {
            cmm.RegisterNamedMessageHandler(MSG_PLAYER_STATE, OnPlayerStateReceived);
        }
        else
        {
            cmm.RegisterNamedMessageHandler(MSG_BROADCAST,   OnBroadcastReceived);
            cmm.RegisterNamedMessageHandler(MSG_ASSIGN_SLOT, OnAssignSlotReceived);
        }
    }

    void DeregisterMessages()
    {
        var cmm = NetworkManager.Singleton?.CustomMessagingManager;
        if (cmm == null) return;
        cmm.UnregisterNamedMessageHandler(MSG_PLAYER_STATE);
        cmm.UnregisterNamedMessageHandler(MSG_BROADCAST);
        cmm.UnregisterNamedMessageHandler(MSG_ASSIGN_SLOT);
    }

    // Host → specific client: tell them their slot number and team
    void AssignSlotToClient(ulong clientId, int slot)
    {
        bool isBlue = GetIsBlueForSlot(slot, _modes[_modeIdx].mode);
        using var w = new FastBufferWriter(5, Allocator.Temp);
        w.WriteValueSafe((byte)slot);
        w.WriteValueSafe((byte)_modeIdx);
        w.WriteValueSafe((byte)_slots[0].robotIndex); // host robot index for display
        w.WriteValueSafe((byte)(isBlue ? 1 : 0));
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
            MSG_ASSIGN_SLOT, clientId, w, NetworkDelivery.Reliable);
    }

    // Client receives their slot assignment
    void OnAssignSlotReceived(ulong _, FastBufferReader r)
    {
        r.ReadValueSafe(out byte slot);
        r.ReadValueSafe(out byte modeIdx);
        r.ReadValueSafe(out byte hostRobot);
        r.ReadValueSafe(out byte isBlueB);
        _mySlot  = slot;
        _modeIdx = modeIdx;
        var mode = _modes[modeIdx].mode;
        _slots[0].connected  = true;
        _slots[0].username   = "Host";
        _slots[0].robotIndex = hostRobot;
        _slots[0].isBlue     = GetIsBlueForSlot(0, mode);
        _slots[slot].connected  = true;
        _slots[slot].username   = MyUsername;
        _slots[slot].robotIndex = 0;
        _slots[slot].isBlue     = isBlueB != 0;

        // Tell GameNetworkManager which slot this machine drives
        var gnm = GameNetworkManager.Instance;
        if (gnm != null) gnm.LocalClientSlot = slot;

        SendMyState();
        RefreshLobbyView();
    }

    // Client → host: send my username, robot, spawn, team
    void SendMyState()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsHost) return;
        var cmm = NetworkManager.Singleton.CustomMessagingManager;
        if (cmm == null) return;

        using var w = new FastBufferWriter(32, Allocator.Temp);
        w.WriteValueSafe((byte)_mySlot);
        w.WriteValueSafe((byte)_slots[_mySlot].robotIndex);
        w.WriteValueSafe((byte)_slots[_mySlot].spawnIndex);
        w.WriteValueSafe((byte)(_slots[_mySlot].isBlue ? 1 : 0));
        WriteString(w, MyUsername);
        cmm.SendNamedMessage(MSG_PLAYER_STATE, NetworkManager.ServerClientId, w,
            NetworkDelivery.Reliable);
    }

    // Host receives a client's state update
    void OnPlayerStateReceived(ulong senderId, FastBufferReader r)
    {
        r.ReadValueSafe(out byte slot);
        r.ReadValueSafe(out byte robotIdx);
        r.ReadValueSafe(out byte spawnIdx);
        r.ReadValueSafe(out byte isBlueB);
        string username = ReadString(r);

        if (slot >= MaxSlots) return;
        _slots[slot].robotIndex = robotIdx;
        _slots[slot].spawnIndex = spawnIdx;
        _slots[slot].isBlue     = isBlueB != 0;
        _slots[slot].username   = username;

        SendBroadcast();
        RefreshLobbyView();
    }

    // Host → all clients: full lobby state
    void SendBroadcast()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost) return;
        var cmm = NetworkManager.Singleton.CustomMessagingManager;
        if (cmm == null) return;

        using var w = new FastBufferWriter(256, Allocator.Temp);
        w.WriteValueSafe((byte)_modeIdx);
        var broadcastMode = _modes[_modeIdx].mode;
        for (int i = 0; i < MaxSlots; i++)
        {
            w.WriteValueSafe(_slots[i].connected ? (byte)1 : (byte)0);
            w.WriteValueSafe((byte)_slots[i].robotIndex);
            w.WriteValueSafe((byte)_slots[i].spawnIndex);
            w.WriteValueSafe((byte)(GetIsBlueForSlot(i, broadcastMode) ? 1 : 0));
            WriteString(w, _slots[i].username);
        }
        // Broadcast to all connected clients (exclude host)
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            if (client.ClientId == NetworkManager.Singleton.LocalClientId) continue;
            cmm.SendNamedMessage(MSG_BROADCAST, client.ClientId, w, NetworkDelivery.Reliable);
        }
    }

    // Client receives full lobby state
    void OnBroadcastReceived(ulong _, FastBufferReader r)
    {
        r.ReadValueSafe(out byte modeIdx);
        _modeIdx = modeIdx;
        var broadcastMode = _modes[modeIdx].mode;
        for (int i = 0; i < MaxSlots; i++)
        {
            r.ReadValueSafe(out byte conn);
            r.ReadValueSafe(out byte robotIdx);
            r.ReadValueSafe(out byte spawnIdx);
            r.ReadValueSafe(out byte isBlueB);
            string username = ReadString(r);
            _slots[i].connected = conn != 0;
            // Don't overwrite own selections — the client is authoritative for its
            // own slot; a stale broadcast can arrive before the host reflects our
            // latest SendMyState, which would bounce the index back.
            if (i != _mySlot)
            {
                _slots[i].robotIndex = robotIdx;
                _slots[i].spawnIndex = spawnIdx;
                _slots[i].isBlue     = isBlueB != 0;
                _slots[i].username   = username;
            }
        }
        RefreshLobbyView();
    }

    // ── String helpers ────────────────────────────────────────────────────────

    static void WriteString(FastBufferWriter w, string s)
    {
        if (s == null) s = string.Empty;
        if (s.Length > 20) s = s.Substring(0, 20);
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        w.WriteValueSafe((byte)bytes.Length);
        foreach (byte b in bytes) w.WriteValueSafe(b);
    }

    static string ReadString(FastBufferReader r)
    {
        r.ReadValueSafe(out byte len);
        var bytes = new byte[len];
        for (int i = 0; i < len; i++) r.ReadValueSafe(out bytes[i]);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // ── UI factory helpers ────────────────────────────────────────────────────

    static GameObject MakePanel(string name, Transform parent, Color col)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = col;
        return go;
    }

    static TextMeshProUGUI MakeText(Transform parent, string text, float size,
        TextAlignmentOptions align, float ax0, float ay0, float ax1, float ay1, Color col)
    {
        var go = new GameObject("Txt", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(ax0, ay0);
        rt.anchorMax = new Vector2(ax1, ay1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.fontSize = size; tmp.alignment = align; tmp.color = col;
        return tmp;
    }

    static Button MakeButton(Transform parent, string label,
        float ax0, float ay0, float ax1, float ay1, Color bgCol)
    {
        var bg = MakePanel("Btn_" + label, parent, bgCol);
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(ax0, ay0); rt.anchorMax = new Vector2(ax1, ay1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var btn = bg.AddComponent<Button>();
        btn.targetGraphic = bg.GetComponent<Image>();
        var lgo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        lgo.transform.SetParent(bg.transform, false);
        StretchFull(lgo.GetComponent<RectTransform>());
        var tmp = lgo.GetComponent<TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = 18; tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white; tmp.raycastTarget = false;
        var cols = btn.colors;
        cols.highlightedColor = new Color(Mathf.Min(bgCol.r + 0.15f, 1f), Mathf.Min(bgCol.g + 0.15f, 1f), Mathf.Min(bgCol.b + 0.15f, 1f));
        cols.pressedColor     = new Color(Mathf.Max(bgCol.r - 0.1f,  0f), Mathf.Max(bgCol.g - 0.1f,  0f), Mathf.Max(bgCol.b - 0.1f,  0f));
        btn.colors = cols;
        return btn;
    }

    static TMP_InputField MakeInputField(Transform parent, string placeholder,
        float ax0, float ay0, float ax1, float ay1)
    {
        var bg = MakePanel("Input", parent, new Color(0.14f, 0.14f, 0.20f));
        var rt = bg.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(ax0, ay0); rt.anchorMax = new Vector2(ax1, ay1);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(bg.transform, false);
        var vrt = viewport.GetComponent<RectTransform>();
        vrt.anchorMin = new Vector2(0.04f, 0f); vrt.anchorMax = new Vector2(0.96f, 1f);
        vrt.offsetMin = vrt.offsetMax = Vector2.zero;

        var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
        phGo.transform.SetParent(viewport.transform, false);
        StretchFull(phGo.GetComponent<RectTransform>());
        var ph = phGo.GetComponent<TextMeshProUGUI>();
        ph.text = placeholder; ph.fontSize = 18; ph.color = new Color(0.45f, 0.45f, 0.45f);
        ph.alignment = TextAlignmentOptions.MidlineLeft; ph.fontStyle = FontStyles.Italic;

        var inGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        inGo.transform.SetParent(viewport.transform, false);
        StretchFull(inGo.GetComponent<RectTransform>());
        var inTmp = inGo.GetComponent<TextMeshProUGUI>();
        inTmp.text = string.Empty; inTmp.fontSize = 18; inTmp.color = Color.white;
        inTmp.alignment = TextAlignmentOptions.MidlineLeft;

        var field = bg.AddComponent<TMP_InputField>();
        field.textViewport = viewport.GetComponent<RectTransform>();
        field.textComponent = inTmp; field.placeholder = ph;
        return field;
    }

    static void MakeDivider(Transform parent, float anchorY)
    {
        var go = MakePanel("Div", parent, new Color(1f, 1f, 1f, 0.10f));
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, anchorY); rt.anchorMax = new Vector2(0.95f, anchorY);
        rt.sizeDelta = new Vector2(0f, 1f);
        go.GetComponent<Image>().raycastTarget = false;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
