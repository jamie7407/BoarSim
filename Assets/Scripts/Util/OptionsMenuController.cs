using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Util
{
    public class OptionsMenuController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private LoadMatch loadMatch;
        [SerializeField] private GameObject menuRoot;
        [SerializeField] private ScreenFader screenFader;

        [Header("Top Controls")]
        [SerializeField] private TMP_Dropdown gameModeDropdown;
        [SerializeField] private TMP_Dropdown cameraDropdown;
        [SerializeField] private TMP_Dropdown frameRateDropdown;
        [SerializeField] private TMP_Dropdown windowModeDropdown;
        [SerializeField] private Button allianceButton;
        [SerializeField] private TMP_Text allianceButtonText;

        [Header("Human Player")]
        [SerializeField] private TMP_Dropdown humanPlayerDropdown;

        [SerializeField] private GameObject blueBucket;
        [SerializeField] private GameObject blueDumper;
        [SerializeField] private GameObject redBucket;
        [SerializeField] private GameObject redDumper;

        [Header("Robot Panels")]
        [SerializeField] private RobotPanelUI robotPanel1;
        [SerializeField] private RobotPanelUI robotPanel2;
        // NEW: red-alliance panels, shown only in TwoVsTwo
        [SerializeField] private RobotPanelUI robotPanel3;
        [SerializeField] private RobotPanelUI robotPanel4;

        [Header("Bottom Buttons")]
        [SerializeField] private Button applyButton;
        [SerializeField] private Button quitButton;

        [Header("Credits")]
        [SerializeField] private GameObject creditsRoot;
        [SerializeField] private Button creditsButton;
        [SerializeField] private Button creditsBackButton;

        [Header("Controls")]
        [SerializeField] private GameObject controlsRoot;
        [SerializeField] private Button controlsButton;
        [SerializeField] private Button controlsBackButton;

        [Header("Multiplayer (Online)")]
        [SerializeField] private Button multiplayerButton;
        [SerializeField] private NetworkLobbyUI multiplayerPanel;

        [Header("Input System")]
        [SerializeField] private InputActionReference toggleMenuAction;
        [SerializeField] private InputActionAsset fallbackActions;
        [SerializeField] private string fallbackToggleActionName = "ToggleMenu";

        [Header("Behavior")]
        [SerializeField] private float startMenuBlackHoldTime = 0.35f;
        [SerializeField] private float startMenuFadeDuration = 3.5f;
        [SerializeField] private bool unlockCursorWhenOpen = true;
        [SerializeField] private bool relockCursorOnClose;

        private const string FrameRatePrefKey = "FrameRateMode";
        private const string WindowModePrefKey = "WindowMode";

        private readonly List<(Utils.FrameRateMode value, string label)> _frameRateModes = new()
        {
            (Utils.FrameRateMode.FPS30, "30 FPS"),
            (Utils.FrameRateMode.FPS60, "60 FPS"),
            (Utils.FrameRateMode.FPS120, "120 FPS"),
            (Utils.FrameRateMode.VSync, "VSync")
        };

        private readonly List<(Utils.WindowMode value, string label)> _windowModes = new()
        {
            (Utils.WindowMode.Windowed, "Windowed"),
            (Utils.WindowMode.BorderlessFullscreen, "Borderless"),
            (Utils.WindowMode.ExclusiveFullscreen, "Fullscreen")
        };

        // NEW: TwoVsTwo added to the dropdown list
        private readonly List<(PlayMode value, string label)> _gameModes = new()
        {
            (PlayMode.OneVsZero, "Singleplayer"),
            (PlayMode.TwoVsZero, "Multiplayer: 2v0"),
            (PlayMode.OneVsOne, "Multiplayer: 1v1"),
            (PlayMode.TwoVsTwo, "Multiplayer: 2v2")   // NEW
        };

        private readonly List<(HumanPlayerType value, string label)> _humanPlayerModes = new()
        {
            (HumanPlayerType.Bucket, "Certified Bucket"),
            (HumanPlayerType.Dumper, "Certified Dumper")
        };

        private readonly List<(Cameras value, string label)> _cameraModes = new()
        {
            (Cameras.ThirdPerson, "Third Person"),
            (Cameras.ReversedThirdPerson, "Reverse Third Person"),
            (Cameras.FirstPerson, "First Person"),
            (Cameras.FirstPersonReversed, "Reverse First Person"),
            (Cameras.DriverStation, "Driver Station")
        };

        private bool _isOpen;
        private bool _isTransitioning;
        private bool _isRefreshingUi;

        private MatchSettings _workingSettings;
        private InputAction _resolvedToggleAction;

        private List<string> _blueSpawnNames = new();
        private List<string> _redSpawnNames = new();

        private HumanPlayerType _workingHumanPlayer = HumanPlayerType.Bucket;

        private OutpostRelease[] _cachedOutpostReleases;

        private void Awake()
        {
            if (loadMatch == null)
                loadMatch = FindFirstObjectByType<LoadMatch>();

            CacheOutpostReleases();

            if (menuRoot != null) menuRoot.SetActive(false);
            if (creditsRoot != null) creditsRoot.SetActive(false);
            if (controlsRoot != null) controlsRoot.SetActive(false);

            WireButtons();
            WirePanels();
            PopulateStaticDropdowns();
            ResolveToggleAction();

            ApplySavedFrameRate();
            ApplySavedWindowMode();
        }

        private void OnEnable()
        {
            ResolveToggleAction();

            if (_resolvedToggleAction != null)
            {
                _resolvedToggleAction.Enable();
                _resolvedToggleAction.performed += OnToggleMenuPerformed;
            }

            GameNetworkManager.OnNetworkMatchStart += HandleNetworkMatchStart;
        }

        private void OnDisable()
        {
            if (_resolvedToggleAction != null)
            {
                _resolvedToggleAction.performed -= OnToggleMenuPerformed;
                _resolvedToggleAction.Disable();
            }

            GameNetworkManager.OnNetworkMatchStart -= HandleNetworkMatchStart;
        }

        private void Start()
        {
            if (loadMatch == null || menuRoot == null)
            {
                enabled = false;
                return;
            }

            StartCoroutine(OpenMenuOnStartWithFadeRoutine());
        }

        private void Update()
        {
            if (_resolvedToggleAction == null &&
                Keyboard.current != null &&
                Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                ToggleMenu();
            }

            if (_isOpen && _workingSettings != null)
            {
                PollGamepadRobotCycle();

                // Any gamepad's Start button applies settings and launches the match
                foreach (var gp in Gamepad.all)
                {
                    if (gp.startButton.wasPressedThisFrame)
                    {
                        ApplyAndClose();
                        break;
                    }
                }
            }
        }

        private void PollGamepadRobotCycle()
        {
            int panelCount = _workingSettings.playMode switch
            {
                PlayMode.TwoVsTwo  => 4,
                PlayMode.OneVsZero => 1,
                _                  => 2
            };

            var gamepads = Gamepad.all;
            for (int i = 0; i < gamepads.Count && i < panelCount; i++)
            {
                var gp = gamepads[i];
                if (gp.dpad.left.wasPressedThisFrame)  CycleRobotIndex(i, -1);
                if (gp.dpad.right.wasPressedThisFrame) CycleRobotIndex(i, 1);
            }
        }

        private void CacheOutpostReleases()
        {
            _cachedOutpostReleases = FindObjectsByType<OutpostRelease>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );
        }

        private void ResolveToggleAction()
        {
            if (toggleMenuAction != null && toggleMenuAction.action != null)
            {
                _resolvedToggleAction = toggleMenuAction.action;
                return;
            }

            if (fallbackActions != null && !string.IsNullOrWhiteSpace(fallbackToggleActionName))
            {
                _resolvedToggleAction = fallbackActions.FindAction(fallbackToggleActionName);
                if (_resolvedToggleAction != null)
                    return;
            }

            _resolvedToggleAction = null;
        }

        private void OnToggleMenuPerformed(InputAction.CallbackContext context) => ToggleMenu();

        private void ToggleMenu()
        {
            if (_isTransitioning) return;

            // If the multiplayer lobby is visible, Escape backs out to the menu
            // rather than closing the whole options screen (which would trigger ResetField).
            if (multiplayerPanel != null && multiplayerPanel.IsOpen)
            {
                CloseMultiplayer();
                return;
            }

            if (_isOpen) CloseMenuWithoutApply();
            else OpenMenu();
        }

        private void WireButtons()
        {
            if (applyButton != null) applyButton.onClick.AddListener(ApplyAndClose);
            if (quitButton != null) quitButton.onClick.AddListener(Application.Quit);
            if (allianceButton != null) allianceButton.onClick.AddListener(ToggleAlliance);
            if (creditsButton != null) creditsButton.onClick.AddListener(OpenCredits);
            if (creditsBackButton != null) creditsBackButton.onClick.AddListener(CloseCredits);
            if (controlsButton != null) controlsButton.onClick.AddListener(OpenControls);
            if (controlsBackButton != null) controlsBackButton.onClick.AddListener(CloseControls);

            // Auto-create the Online button by cloning an existing one if not wired in the Inspector
            if (multiplayerButton == null)
                multiplayerButton = CloneMenuButton(creditsButton ?? controlsButton, "Online");
            if (multiplayerButton != null)
                multiplayerButton.onClick.AddListener(OpenMultiplayer);

            // Auto-create the lobby panel if not wired in the Inspector
            if (multiplayerPanel == null)
            {
                var go = new GameObject("[NetworkLobbyUI]");
                go.transform.SetParent(transform);
                multiplayerPanel = go.AddComponent<NetworkLobbyUI>();
            }
            if (multiplayerPanel != null)
                multiplayerPanel.OnBackClicked += CloseMultiplayer;

            if (gameModeDropdown != null)
                gameModeDropdown.onValueChanged.AddListener(OnGameModeChanged);
            if (cameraDropdown != null)
                cameraDropdown.onValueChanged.AddListener(OnCameraChanged);
            if (frameRateDropdown != null)
                frameRateDropdown.onValueChanged.AddListener(OnFrameRateChanged);
            if (windowModeDropdown != null)
                windowModeDropdown.onValueChanged.AddListener(OnWindowModeChanged);
            if (humanPlayerDropdown != null)
                humanPlayerDropdown.onValueChanged.AddListener(OnHumanPlayerChanged);
        }

        // NEW: wires P3 and P4 panels identically to P1/P2
        private void WirePanels()
        {
            if (robotPanel1 != null)
            {
                robotPanel1.OnPreviousRobot += () => CycleRobotIndex(0, -1);
                robotPanel1.OnNextRobot += () => CycleRobotIndex(0, 1);
                robotPanel1.OnSpawnChanged += value => SetSpawnIndexForPanel(0, value);
                robotPanel1.OnBumperNumberChanged += v => _workingSettings.bumperNumber1 = v;
            }

            if (robotPanel2 != null)
            {
                robotPanel2.OnPreviousRobot += () => CycleRobotIndex(1, -1);
                robotPanel2.OnNextRobot += () => CycleRobotIndex(1, 1);
                robotPanel2.OnSpawnChanged += value => SetSpawnIndexForPanel(1, value);
                robotPanel2.OnBumperNumberChanged += v => _workingSettings.bumperNumber2 = v;
            }

            if (robotPanel3 != null)
            {
                robotPanel3.OnPreviousRobot += () => CycleRobotIndex(2, -1);
                robotPanel3.OnNextRobot += () => CycleRobotIndex(2, 1);
                robotPanel3.OnSpawnChanged += value => SetSpawnIndexForPanel(2, value);
                robotPanel3.OnBumperNumberChanged += v => _workingSettings.bumperNumber3 = v;
            }

            if (robotPanel4 != null)
            {
                robotPanel4.OnPreviousRobot += () => CycleRobotIndex(3, -1);
                robotPanel4.OnNextRobot += () => CycleRobotIndex(3, 1);
                robotPanel4.OnSpawnChanged += value => SetSpawnIndexForPanel(3, value);
                robotPanel4.OnBumperNumberChanged += v => _workingSettings.bumperNumber4 = v;
            }
        }

        private void PopulateStaticDropdowns()
        {
            if (gameModeDropdown != null)
            {
                gameModeDropdown.ClearOptions();
                gameModeDropdown.AddOptions(_gameModes.ConvertAll(x => x.label));
            }

            if (cameraDropdown != null)
            {
                cameraDropdown.ClearOptions();
                cameraDropdown.AddOptions(_cameraModes.ConvertAll(x => x.label));
            }

            if (frameRateDropdown != null)
            {
                frameRateDropdown.ClearOptions();
                frameRateDropdown.AddOptions(_frameRateModes.ConvertAll(x => x.label));

                int savedIndex = PlayerPrefs.GetInt(FrameRatePrefKey, FindFrameRateIndex(Utils.FrameRateMode.VSync));
                savedIndex = Mathf.Clamp(savedIndex, 0, _frameRateModes.Count - 1);
                frameRateDropdown.SetValueWithoutNotify(savedIndex);
                frameRateDropdown.RefreshShownValue();
            }

            if (windowModeDropdown != null)
            {
                windowModeDropdown.ClearOptions();
                windowModeDropdown.AddOptions(_windowModes.ConvertAll(x => x.label));

                int savedIndex = PlayerPrefs.GetInt(WindowModePrefKey, FindWindowModeIndex(Utils.WindowMode.Windowed));
                savedIndex = Mathf.Clamp(savedIndex, 0, _windowModes.Count - 1);
                windowModeDropdown.SetValueWithoutNotify(savedIndex);
                windowModeDropdown.RefreshShownValue();
            }

            if (humanPlayerDropdown != null)
            {
                humanPlayerDropdown.ClearOptions();
                humanPlayerDropdown.AddOptions(_humanPlayerModes.ConvertAll(x => x.label));
            }
        }

        private void LoadDynamicData()
        {
            _blueSpawnNames = loadMatch.GetBlueSpawnNames();
            _redSpawnNames = loadMatch.GetRedSpawnNames();
        }

        private IEnumerator OpenMenuOnStartWithFadeRoutine()
        {
            if (screenFader == null)
            {
                OpenMenuImmediate();
                yield break;
            }

            _isTransitioning = true;
            screenFader.SetBlackImmediate(true);
            OpenMenuImmediate();

            yield return null;

            if (startMenuBlackHoldTime > 0f)
                yield return new WaitForSecondsRealtime(startMenuBlackHoldTime);

            screenFader.FadeFromBlack(startMenuFadeDuration, () => { _isTransitioning = false; });
        }

        private void OpenMenu()
        {
            if (_isTransitioning || loadMatch == null || menuRoot == null)
                return;

            _isTransitioning = true;

            void ShowMenu()
            {
                LoadDynamicData();
                _workingSettings = loadMatch.GetSettingsCopy();
                ApplySettingsToUI(true);

                _isOpen = true;
                menuRoot.SetActive(true);
                Time.timeScale = 0f;

                if (unlockCursorWhenOpen)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                SetRobotInputsEnabled(false);
            }

            void Done() { _isTransitioning = false; }

            if (screenFader != null) screenFader.FadeToBlackThen(ShowMenu, true, Done);
            else { ShowMenu(); Done(); }
        }

        private void OpenMenuImmediate()
        {
            LoadDynamicData();
            _workingSettings = loadMatch.GetSettingsCopy();
            ApplySettingsToUI(true);

            _isOpen = true;
            menuRoot.SetActive(true);
            Time.timeScale = 0f;

            if (unlockCursorWhenOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            SetRobotInputsEnabled(false);
        }

        private void ApplyAndClose()
        {
            if (_isTransitioning || loadMatch == null)
                return;

            _isTransitioning = true;

            void ApplyAndReset()
            {
                loadMatch.ApplySettings(_workingSettings);

                // If hosting, push these settings to all clients before resetting locally
                // so both machines start the same match at roughly the same time.
                var gnm = GameNetworkManager.Instance;
                if (gnm != null && gnm.IsHost)
                    gnm.BroadcastMatchStart(_workingSettings);

                ResumeRuntimeState();

                multiplayerPanel?.Close();
                if (controlsRoot != null) controlsRoot.SetActive(false);
                if (creditsRoot != null) creditsRoot.SetActive(false);
                if (menuRoot != null) menuRoot.SetActive(false);

                _isOpen = false;
                loadMatch.ResetField();
            }

            void Done() { _isTransitioning = false; }

            if (screenFader != null) screenFader.FadeToBlackThen(ApplyAndReset, true, Done);
            else { ApplyAndReset(); Done(); }
        }

        private void CloseMenuWithoutApply()
        {
            if (_isTransitioning) return;

            _isTransitioning = true;

            void CloseAction()
            {
                ResumeRuntimeState();

                multiplayerPanel?.Close();
                if (controlsRoot != null) controlsRoot.SetActive(false);
                if (creditsRoot != null) creditsRoot.SetActive(false);
                if (menuRoot != null) menuRoot.SetActive(false);

                _isOpen = false;

                if (loadMatch != null) loadMatch.ResetField();
                else SetRobotInputsEnabled(true);
            }

            void Done() { _isTransitioning = false; }

            if (screenFader != null) screenFader.FadeToBlackThen(CloseAction, true, Done);
            else { CloseAction(); Done(); }
        }

        private void OpenCredits()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            void ShowCredits()
            {
                if (menuRoot != null) menuRoot.SetActive(false);
                if (creditsRoot != null) creditsRoot.SetActive(true);
                if (controlsRoot != null) controlsRoot.SetActive(false);
            }

            void Done() { _isTransitioning = false; }
            if (screenFader != null) screenFader.FadeToBlackThen(ShowCredits, true, Done);
            else { ShowCredits(); Done(); }
        }

        private void CloseCredits()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            void ShowMenu()
            {
                if (creditsRoot != null) creditsRoot.SetActive(false);
                if (menuRoot != null) menuRoot.SetActive(true);
                if (controlsRoot != null) controlsRoot.SetActive(false);
                RefreshVisibleState(false);
            }

            void Done() { _isTransitioning = false; }
            if (screenFader != null) screenFader.FadeToBlackThen(ShowMenu, true, Done);
            else { ShowMenu(); Done(); }
        }

        private void OpenControls()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            void ShowControls()
            {
                if (menuRoot != null) menuRoot.SetActive(false);
                if (creditsRoot != null) creditsRoot.SetActive(false);
                if (controlsRoot != null) controlsRoot.SetActive(true);
            }

            void Done() { _isTransitioning = false; }
            if (screenFader != null) screenFader.FadeToBlackThen(ShowControls, true, Done);
            else { ShowControls(); Done(); }
        }

        private void CloseControls()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            void ShowMenu()
            {
                if (controlsRoot != null) controlsRoot.SetActive(false);
                if (creditsRoot != null) creditsRoot.SetActive(false);
                if (menuRoot != null) menuRoot.SetActive(true);
                RefreshVisibleState(false);
            }

            void Done() { _isTransitioning = false; }
            if (screenFader != null) screenFader.FadeToBlackThen(ShowMenu, true, Done);
            else { ShowMenu(); Done(); }
        }

        private void OpenMultiplayer()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            // Hand the lobby panel the correct Canvas (the one that owns menuRoot)
            // before first open, so it never has to search for one itself.
            if (multiplayerPanel != null && menuRoot != null)
            {
                var canvas = menuRoot.GetComponentInParent<Canvas>();
                if (canvas != null) multiplayerPanel.SetTargetCanvas(canvas);
            }

            void Show()
            {
                if (menuRoot     != null) menuRoot.SetActive(false);
                if (creditsRoot  != null) creditsRoot.SetActive(false);
                if (controlsRoot != null) controlsRoot.SetActive(false);
                multiplayerPanel?.Open();
            }

            void Done() { _isTransitioning = false; }
            if (screenFader != null) screenFader.FadeToBlackThen(Show, true, Done);
            else { Show(); Done(); }
        }

        private void CloseMultiplayer()
        {
            if (_isTransitioning) return;
            _isTransitioning = true;

            void Show()
            {
                multiplayerPanel?.Close();
                if (menuRoot != null) menuRoot.SetActive(true);
                RefreshVisibleState(false);
            }

            void Done() { _isTransitioning = false; }
            if (screenFader != null) screenFader.FadeToBlackThen(Show, true, Done);
            else { Show(); Done(); }
        }

        private void ResumeRuntimeState()
        {
            Time.timeScale = 1f;

            if (relockCursorOnClose)
            {
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        private void ApplySettingsToUI(bool configureOwnership)
        {
            if (gameModeDropdown != null)
            {
                gameModeDropdown.SetValueWithoutNotify(FindGameModeIndex(_workingSettings.playMode));
                gameModeDropdown.RefreshShownValue();
            }

            if (cameraDropdown != null)
            {
                cameraDropdown.SetValueWithoutNotify(FindCameraModeIndex(_workingSettings.view));
                cameraDropdown.RefreshShownValue();
            }

            if (frameRateDropdown != null)
            {
                int savedIndex = PlayerPrefs.GetInt(FrameRatePrefKey, FindFrameRateIndex(Utils.FrameRateMode.VSync));
                savedIndex = Mathf.Clamp(savedIndex, 0, _frameRateModes.Count - 1);
                frameRateDropdown.SetValueWithoutNotify(savedIndex);
                frameRateDropdown.RefreshShownValue();
            }

            if (windowModeDropdown != null)
            {
                int savedIndex = PlayerPrefs.GetInt(WindowModePrefKey, FindWindowModeIndex(Utils.WindowMode.BorderlessFullscreen));
                savedIndex = Mathf.Clamp(savedIndex, 0, _windowModes.Count - 1);
                windowModeDropdown.SetValueWithoutNotify(savedIndex);
                windowModeDropdown.RefreshShownValue();
            }

            if (humanPlayerDropdown != null)
            {
                humanPlayerDropdown.SetValueWithoutNotify(FindHumanPlayerIndex(_workingHumanPlayer));
                humanPlayerDropdown.RefreshShownValue();
            }

            RefreshVisibleState(configureOwnership);
        }

        // NEW: TwoVsTwo shows all four panels; alliance button locked (always blue+red)
        private void RefreshVisibleState(bool configureOwnership)
        {
            if (_isRefreshingUi) return;
            _isRefreshingUi = true;

            try
            {
                bool isTwoVsTwo = _workingSettings.playMode == PlayMode.TwoVsTwo;
                bool isOneVsOne = _workingSettings.playMode == PlayMode.OneVsOne;
                bool secondVisible = _workingSettings.playMode != PlayMode.OneVsZero;

                // Panel visibility
                if (robotPanel1 != null) robotPanel1.SetVisible(true);
                if (robotPanel2 != null) robotPanel2.SetVisible(secondVisible);
                if (robotPanel3 != null) robotPanel3.SetVisible(isTwoVsTwo);
                if (robotPanel4 != null) robotPanel4.SetVisible(isTwoVsTwo);

                // Alliance button — locked for 1v1 and 2v2 (both sides always present)
                bool allianceLocked = isOneVsOne || isTwoVsTwo;
                if (allianceButton != null)
                    allianceButton.interactable = !allianceLocked;

                if (allianceButtonText != null)
                {
                    allianceButtonText.text = allianceLocked
                        ? "Alliance Locked"
                        : (_workingSettings.useBlueAlliance ? "Blue Alliance" : "Red Alliance");
                }

                // Refresh whichever panels are visible
                RefreshPanel(0);
                if (secondVisible) RefreshPanel(1);
                if (isTwoVsTwo)
                {
                    RefreshPanel(2);
                    RefreshPanel(3);
                }

                RefreshHumanPlayerObjects(configureOwnership);
            }
            finally
            {
                _isRefreshingUi = false;
            }
        }

        // NEW: panels 2 and 3 (indices 2/3) map to red alliance in TwoVsTwo
        private void RefreshPanel(int panelIndex)
        {
            RobotPanelUI panel = panelIndex switch
            {
                0 => robotPanel1,
                1 => robotPanel2,
                2 => robotPanel3,
                3 => robotPanel4,
                _ => null
            };

            if (panel == null) return;

            string sideLabel;
            List<string> spawnNames;
            int selectedSpawnIndex;
            int robotIndex;

            if (_workingSettings.playMode == PlayMode.TwoVsTwo)
            {
                // Panels 0/1 = blue alliance, panels 2/3 = red alliance
                bool isRedPanel = panelIndex >= 2;

                sideLabel = isRedPanel ? "Red Alliance" : "Blue Alliance";
                spawnNames = isRedPanel ? _redSpawnNames : _blueSpawnNames;

                selectedSpawnIndex = panelIndex switch
                {
                    0 => _workingSettings.blueSpawnIndex1,
                    1 => _workingSettings.blueSpawnIndex2,
                    2 => _workingSettings.redSpawnIndex1,
                    3 => _workingSettings.redSpawnIndex2,
                    _ => 0
                };

                robotIndex = panelIndex switch
                {
                    0 => _workingSettings.robotIndex1,
                    1 => _workingSettings.robotIndex2,
                    2 => _workingSettings.robotIndex3,
                    3 => _workingSettings.robotIndex4,
                    _ => 0
                };
            }
            else if (_workingSettings.playMode == PlayMode.OneVsOne)
            {
                if (panelIndex == 0)
                {
                    sideLabel = "Blue Alliance";
                    spawnNames = _blueSpawnNames;
                    selectedSpawnIndex = _workingSettings.blueSpawnIndex1;
                }
                else
                {
                    sideLabel = "Red Alliance";
                    spawnNames = _redSpawnNames;
                    selectedSpawnIndex = _workingSettings.redSpawnIndex1;
                }

                robotIndex = panelIndex == 0 ? _workingSettings.robotIndex1 : _workingSettings.robotIndex2;
            }
            else
            {
                bool useBlue = _workingSettings.useBlueAlliance;
                sideLabel = useBlue ? "Blue Alliance" : "Red Alliance";

                if (useBlue)
                {
                    spawnNames = _blueSpawnNames;
                    selectedSpawnIndex = panelIndex == 0
                        ? _workingSettings.blueSpawnIndex1
                        : _workingSettings.blueSpawnIndex2;
                }
                else
                {
                    spawnNames = _redSpawnNames;
                    selectedSpawnIndex = panelIndex == 0
                        ? _workingSettings.redSpawnIndex1
                        : _workingSettings.redSpawnIndex2;
                }

                robotIndex = panelIndex == 0 ? _workingSettings.robotIndex1 : _workingSettings.robotIndex2;
            }

            panel.SetSideLabel(sideLabel);
            panel.SetRobotName(loadMatch.GetRobotNameAt(robotIndex));
            panel.SetRobotPreview(loadMatch.GetRobotPreviewSpriteAt(robotIndex));
            panel.SetSpawnOptions(spawnNames, selectedSpawnIndex);

            int bumperNum = panelIndex switch
            {
                0 => _workingSettings.bumperNumber1,
                1 => _workingSettings.bumperNumber2,
                2 => _workingSettings.bumperNumber3,
                3 => _workingSettings.bumperNumber4,
                _ => 0
            };
            panel.SetBumperNumber(bumperNum);
        }

        private void ToggleAlliance()
        {
            if (_workingSettings.playMode == PlayMode.OneVsOne ||
                _workingSettings.playMode == PlayMode.TwoVsTwo)
                return;

            _workingSettings.useBlueAlliance = !_workingSettings.useBlueAlliance;
            RefreshVisibleState(true);
        }

        private void OnGameModeChanged(int dropdownIndex)
        {
            if (_isRefreshingUi) return;
            _workingSettings.playMode = _gameModes[Mathf.Clamp(dropdownIndex, 0, _gameModes.Count - 1)].value;
            RefreshVisibleState(true);
        }

        private void OnCameraChanged(int dropdownIndex)
        {
            if (_isRefreshingUi) return;
            _workingSettings.view = _cameraModes[Mathf.Clamp(dropdownIndex, 0, _cameraModes.Count - 1)].value;
            RefreshVisibleState(false);
        }

        private void OnFrameRateChanged(int dropdownIndex)
        {
            dropdownIndex = Mathf.Clamp(dropdownIndex, 0, _frameRateModes.Count - 1);
            PlayerPrefs.SetInt(FrameRatePrefKey, dropdownIndex);
            PlayerPrefs.Save();
            ApplyFrameRate(_frameRateModes[dropdownIndex].value);
        }

        private void OnWindowModeChanged(int dropdownIndex)
        {
            dropdownIndex = Mathf.Clamp(dropdownIndex, 0, _windowModes.Count - 1);
            PlayerPrefs.SetInt(WindowModePrefKey, dropdownIndex);
            PlayerPrefs.Save();
            ApplyWindowMode(_windowModes[dropdownIndex].value);
        }

        private void ApplySavedWindowMode()
        {
            int savedIndex = PlayerPrefs.GetInt(WindowModePrefKey, FindWindowModeIndex(Utils.WindowMode.BorderlessFullscreen));
            savedIndex = Mathf.Clamp(savedIndex, 0, _windowModes.Count - 1);
            ApplyWindowMode(_windowModes[savedIndex].value);
        }

        private void ApplyWindowMode(Utils.WindowMode mode)
        {
            switch (mode)
            {
                case Utils.WindowMode.Windowed: ApplyWindowedMode(); break;
                case Utils.WindowMode.BorderlessFullscreen: ApplyBorderlessFullscreenMode(); break;
                case Utils.WindowMode.ExclusiveFullscreen: ApplyExclusiveFullscreenMode(); break;
            }
        }

        private void ApplyWindowedMode()
        {
            int width = Screen.width;
            int height = Screen.height;

            if (Screen.fullScreenMode != FullScreenMode.Windowed)
            {
                width = Mathf.Min(1600, Screen.currentResolution.width);
                height = Mathf.Min(900, Screen.currentResolution.height);
            }

            Screen.SetResolution(width, height, FullScreenMode.Windowed);
        }

        private void ApplyBorderlessFullscreenMode()
        {
            Resolution r = Screen.currentResolution;
            Screen.SetResolution(r.width, r.height, FullScreenMode.FullScreenWindow);
        }

        private void ApplyExclusiveFullscreenMode()
        {
            Resolution r = Screen.currentResolution;
#if UNITY_STANDALONE_WIN
            Screen.SetResolution(r.width, r.height, FullScreenMode.ExclusiveFullScreen);
#else
            Screen.SetResolution(r.width, r.height, FullScreenMode.FullScreenWindow);
#endif
        }

        private int FindWindowModeIndex(Utils.WindowMode value)
        {
            for (int i = 0; i < _windowModes.Count; i++)
                if (_windowModes[i].value == value) return i;
            return 0;
        }

        private void ApplySavedFrameRate()
        {
            int savedIndex = PlayerPrefs.GetInt(FrameRatePrefKey, FindFrameRateIndex(Utils.FrameRateMode.VSync));
            savedIndex = Mathf.Clamp(savedIndex, 0, _frameRateModes.Count - 1);
            ApplyFrameRate(_frameRateModes[savedIndex].value);
        }

        private void ApplyFrameRate(Utils.FrameRateMode mode)
        {
            switch (mode)
            {
                case Utils.FrameRateMode.FPS30: SetManualFrameRate(30); break;
                case Utils.FrameRateMode.FPS60: SetManualFrameRate(60); break;
                case Utils.FrameRateMode.FPS120: SetManualFrameRate(120); break;
                case Utils.FrameRateMode.VSync: SetVSync(); break;
            }
        }

        private void SetManualFrameRate(int fps)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = fps;
        }

        private void SetVSync()
        {
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = -1;
        }

        private void OnHumanPlayerChanged(int dropdownIndex)
        {
            if (_isRefreshingUi) return;
            _workingHumanPlayer = _humanPlayerModes[Mathf.Clamp(dropdownIndex, 0, _humanPlayerModes.Count - 1)].value;
            RefreshHumanPlayerObjects(true);
        }

        // NEW: panels 2 and 3 cycle robotIndex3/4
        private void CycleRobotIndex(int panelIndex, int delta)
        {
            int count = loadMatch.GetAvailableRobotCount();
            if (count <= 0) return;

            switch (panelIndex)
            {
                case 0: _workingSettings.robotIndex1 = WrapIndex(_workingSettings.robotIndex1 + delta, count); break;
                case 1: _workingSettings.robotIndex2 = WrapIndex(_workingSettings.robotIndex2 + delta, count); break;
                case 2: _workingSettings.robotIndex3 = WrapIndex(_workingSettings.robotIndex3 + delta, count); break;
                case 3: _workingSettings.robotIndex4 = WrapIndex(_workingSettings.robotIndex4 + delta, count); break;
            }

            RefreshPanel(panelIndex);
        }

        private int WrapIndex(int value, int count)
        {
            if (count <= 0) return 0;
            value %= count;
            if (value < 0) value += count;
            return value;
        }

        // NEW: panels 2/3 set redSpawnIndex1/2
        private void SetSpawnIndexForPanel(int panelIndex, int value)
        {
            if (_isRefreshingUi) return;

            if (_workingSettings.playMode == PlayMode.TwoVsTwo)
            {
                switch (panelIndex)
                {
                    case 0:
                        _workingSettings.blueSpawnIndex1 = value;
                        if (_workingSettings.blueSpawnIndex1 == _workingSettings.blueSpawnIndex2)
                            _workingSettings.blueSpawnIndex2 = FindDifferentIndex(value, _blueSpawnNames.Count);
                        break;
                    case 1:
                        _workingSettings.blueSpawnIndex2 = value;
                        if (_workingSettings.blueSpawnIndex2 == _workingSettings.blueSpawnIndex1)
                            _workingSettings.blueSpawnIndex1 = FindDifferentIndex(value, _blueSpawnNames.Count);
                        break;
                    case 2:
                        _workingSettings.redSpawnIndex1 = value;
                        if (_workingSettings.redSpawnIndex1 == _workingSettings.redSpawnIndex2)
                            _workingSettings.redSpawnIndex2 = FindDifferentIndex(value, _redSpawnNames.Count);
                        break;
                    case 3:
                        _workingSettings.redSpawnIndex2 = value;
                        if (_workingSettings.redSpawnIndex2 == _workingSettings.redSpawnIndex1)
                            _workingSettings.redSpawnIndex1 = FindDifferentIndex(value, _redSpawnNames.Count);
                        break;
                }

                RefreshVisibleState(false);
                return;
            }

            if (_workingSettings.playMode == PlayMode.OneVsOne)
            {
                if (panelIndex == 0) _workingSettings.blueSpawnIndex1 = value;
                else _workingSettings.redSpawnIndex1 = value;
                RefreshVisibleState(false);
                return;
            }

            if (_workingSettings.playMode == PlayMode.OneVsZero)
            {
                if (_workingSettings.useBlueAlliance) _workingSettings.blueSpawnIndex1 = value;
                else _workingSettings.redSpawnIndex1 = value;
                RefreshVisibleState(false);
                return;
            }

            // TwoVsZero
            if (_workingSettings.useBlueAlliance)
            {
                if (panelIndex == 0)
                {
                    _workingSettings.blueSpawnIndex1 = value;
                    if (_workingSettings.blueSpawnIndex1 == _workingSettings.blueSpawnIndex2)
                        _workingSettings.blueSpawnIndex2 = FindDifferentIndex(value, _blueSpawnNames.Count);
                }
                else
                {
                    _workingSettings.blueSpawnIndex2 = value;
                    if (_workingSettings.blueSpawnIndex2 == _workingSettings.blueSpawnIndex1)
                        _workingSettings.blueSpawnIndex1 = FindDifferentIndex(value, _blueSpawnNames.Count);
                }
            }
            else
            {
                if (panelIndex == 0)
                {
                    _workingSettings.redSpawnIndex1 = value;
                    if (_workingSettings.redSpawnIndex1 == _workingSettings.redSpawnIndex2)
                        _workingSettings.redSpawnIndex2 = FindDifferentIndex(value, _redSpawnNames.Count);
                }
                else
                {
                    _workingSettings.redSpawnIndex2 = value;
                    if (_workingSettings.redSpawnIndex2 == _workingSettings.redSpawnIndex1)
                        _workingSettings.redSpawnIndex1 = FindDifferentIndex(value, _redSpawnNames.Count);
                }
            }

            RefreshVisibleState(false);
        }

        private int FindDifferentIndex(int currentIndex, int count)
        {
            if (count <= 1) return currentIndex;
            for (int i = 0; i < count; i++)
                if (i != currentIndex) return i;
            return currentIndex;
        }

        private int FindGameModeIndex(PlayMode value)
        {
            for (int i = 0; i < _gameModes.Count; i++)
                if (_gameModes[i].value == value) return i;
            return 0;
        }

        private int FindCameraModeIndex(Cameras value)
        {
            for (int i = 0; i < _cameraModes.Count; i++)
                if (_cameraModes[i].value == value) return i;
            return 0;
        }

        private int FindFrameRateIndex(Utils.FrameRateMode value)
        {
            for (int i = 0; i < _frameRateModes.Count; i++)
                if (_frameRateModes[i].value == value) return i;
            return 0;
        }

        private int FindHumanPlayerIndex(HumanPlayerType value)
        {
            for (int i = 0; i < _humanPlayerModes.Count; i++)
                if (_humanPlayerModes[i].value == value) return i;
            return 0;
        }

        private void SetRobotInputsEnabled(bool enabledBool)
        {
            if (loadMatch == null) return;

            var robots = loadMatch.GetLoadedRobots();
            if (robots == null) return;

            foreach (var robot in robots)
            {
                if (robot == null) continue;
                var playerInput = robot.GetComponent<PlayerInput>();
                if (playerInput == null) continue;

                if (enabledBool) playerInput.ActivateInput();
                else playerInput.DeactivateInput();
            }
        }

        public bool IsOpen() => _isOpen;

        // Called on the client when the host clicks Apply — starts the same match here
        // without requiring the client to touch the options menu at all.
        private void HandleNetworkMatchStart(MatchSettings settings)
        {
            if (loadMatch == null) return;

            if (_isOpen)
            {
                // Menu is open: borrow the host's settings and close as if we clicked Apply.
                _workingSettings = settings;
                ApplyAndClose();
            }
            else
            {
                // Menu already closed (match was already running); just reset with new settings.
                // Restore timeScale in case the player opened the options menu before joining —
                // OpenMultiplayer() doesn't call ResumeRuntimeState(), leaving timeScale=0.
                ResumeRuntimeState();
                loadMatch.ApplySettings(settings);
                loadMatch.ResetField();
            }
        }

        // Clones an existing menu button so the new one automatically matches the UI style.
        // Replaces onClick entirely so neither persistent (Inspector) nor runtime listeners carry over.
        private static Button CloneMenuButton(Button source, string label)
        {
            if (source == null) return null;
            var clone = Instantiate(source.gameObject, source.transform.parent);
            clone.name = $"Btn_{label}";
            var tmp = clone.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (tmp != null) tmp.text = label;
            var btn = clone.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            return btn;
        }

        private void RefreshHumanPlayerObjects(bool configureOwnership)
        {
            bool blueAllianceUsed = IsBlueAllianceUsed();
            bool redAllianceUsed = IsRedAllianceUsed();

            bool bucketSelected = _workingHumanPlayer == HumanPlayerType.Bucket;
            bool dumperSelected = _workingHumanPlayer == HumanPlayerType.Dumper;

            SetActiveSafe(blueBucket, blueAllianceUsed && bucketSelected);
            SetActiveSafe(blueDumper, blueAllianceUsed && dumperSelected);
            SetActiveSafe(redBucket, redAllianceUsed && bucketSelected);
            SetActiveSafe(redDumper, redAllianceUsed && dumperSelected);

            HumanPlayerRuntimeState.SetState(_workingHumanPlayer, blueAllianceUsed, redAllianceUsed);

            if (configureOwnership)
                ConfigureAllDumperOwnership();
        }

        private void ConfigureAllDumperOwnership()
        {
            if (loadMatch == null || _cachedOutpostReleases == null) return;

            foreach (var release in _cachedOutpostReleases)
            {
                if (release == null) continue;
                bool releaseIsBlue = release.IsBlue();
                int ownerSlot = loadMatch.GetHumanPlayerOwnerSlotForAlliance(releaseIsBlue);
                release.ConfigureOwnership(ownerSlot);
            }
        }

        // NEW: TwoVsTwo always uses both alliances
        private bool IsBlueAllianceUsed()
        {
            return _workingSettings.playMode == PlayMode.OneVsOne ||
                   _workingSettings.playMode == PlayMode.TwoVsTwo ||
                   _workingSettings.useBlueAlliance;
        }

        private bool IsRedAllianceUsed()
        {
            return _workingSettings.playMode == PlayMode.OneVsOne ||
                   _workingSettings.playMode == PlayMode.TwoVsTwo ||
                   !_workingSettings.useBlueAlliance;
        }

        private void SetActiveSafe(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
                target.SetActive(active);
        }
    }
}