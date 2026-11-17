using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Magnetic.SteamLobby.Samples
{
    /// <summary>
    /// <see cref="ServerManager"/> 를 사용하는 uGUI 로비 화면 예제.
    /// UI 는 메뉴 [Tools > Magnetic Steam Lobby > Build Lobby Scene] 으로 생성되며 참조도 자동 연결된다.
    /// 모든 참조는 비워도 되므로 UI 를 직접 고치거나 지워도 동작은 유지된다.
    /// </summary>
    public class SessionLobbyController : MonoBehaviour
    {
        [Header("프리셋 (비우면 ServerManager 기본 프리셋)")]
        [SerializeField] private SessionPreset[] presets;

        [Header("로비 밖 - 만들기")]
        [SerializeField] private GameObject browserRoot;
        [SerializeField] private InputField lobbyNameInput;
        [SerializeField] private InputField createPasswordInput;
        [SerializeField] private Dropdown presetDropdown;
        [SerializeField] private Toggle unlistedToggle;
        [SerializeField] private Toggle friendsOnlyToggle;
        [SerializeField] private Button createButton;

        [Header("로비 밖 - 참가")]
        [Tooltip("참가 코드(6자리) 또는 로비 ID(숫자)")]
        [SerializeField] private InputField joinCodeInput;
        [SerializeField] private InputField joinPasswordInput;
        [SerializeField] private Button joinCodeButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Text refreshButtonText;
        [SerializeField] private Transform lobbyListContent;
        [Tooltip("자식에 \"Name\", \"Info\" Text 가 있는 Button 프리팹")]
        [SerializeField] private Button lobbyListItemPrefab;
        [SerializeField] private Text emptyListText;
        [Tooltip("로비 목록 자동 새로고침 간격(초). 0 이면 끔")]
        [SerializeField] private float autoRefreshInterval = 10f;

        [Header("로비 안")]
        [SerializeField] private GameObject roomRoot;
        [SerializeField] private Text roomTitleText;
        [SerializeField] private Text roomInfoText;
        [SerializeField] private Text roomRuleText;
        [SerializeField] private Button copyCodeButton;
        [SerializeField] private Transform memberListContent;
        [Tooltip("자식에 \"Name\", \"Status\" Text 가 있는 프리팹")]
        [SerializeField] private GameObject memberItemPrefab;
        [SerializeField] private Button readyButton;
        [SerializeField] private Text readyButtonText;
        [SerializeField] private Button startButton;
        [SerializeField] private Button endGameButton;
        [SerializeField] private Button inviteButton;
        [SerializeField] private Button leaveButton;
        [SerializeField] private Text leaveButtonText;
        [SerializeField] private Text startHintText;
        [SerializeField] private InputField chatInput;
        [SerializeField] private Button chatSendButton;
        [SerializeField] private Text chatLogText;
        [SerializeField] private ScrollRect chatScroll;

        [Header("난입 승인 (호스트)")]
        [SerializeField] private GameObject joinRequestPanel;
        [SerializeField] private Transform joinRequestContent;
        [Tooltip("자식에 \"Name\" Text, \"Approve\"·\"Reject\" Button 이 있는 프리팹")]
        [SerializeField] private GameObject joinRequestItemPrefab;

        [Header("오버레이")]
        [SerializeField] private GameObject passwordPanel;
        [SerializeField] private InputField passwordPanelInput;
        [SerializeField] private Button passwordSubmitButton;
        [SerializeField] private Button passwordCancelButton;
        [SerializeField] private GameObject waitingPanel;
        [SerializeField] private Text waitingText;
        [SerializeField] private Button waitingCancelButton;

        [Header("공통")]
        [SerializeField] private Text statusText;
        [SerializeField] private Text playerNameText;
        [SerializeField] private Button backButton;
        [SerializeField] private string backSceneName = "";

        [Header("색상")]
        [SerializeField] private Color readyColor = new Color(0.55f, 0.85f, 0.45f);
        [SerializeField] private Color waitingColor = new Color(0.75f, 0.72f, 0.62f);
        [SerializeField] private Color emptySlotColor = new Color(1f, 1f, 1f, 0.3f);
        [SerializeField] private Color statusNormalColor = new Color(0.85f, 0.83f, 0.78f);
        [SerializeField] private Color statusErrorColor = new Color(0.95f, 0.5f, 0.45f);

        private readonly List<GameObject> spawnedLobbyItems = new List<GameObject>();
        private readonly List<GameObject> spawnedMemberItems = new List<GameObject>();
        private readonly List<GameObject> spawnedRequestItems = new List<GameObject>();
        private readonly List<string> chatLines = new List<string>();
        private ServerManager sm;
        private SteamLobbyService lobby;
        private float nextAutoRefresh;
        private float nextHintRefresh;

        private void Start()
        {
            sm = ServerManager.Instance;
            lobby = sm != null ? sm.Lobby : null;

            if (createButton) createButton.onClick.AddListener(CreateLobby);
            if (joinCodeButton) joinCodeButton.onClick.AddListener(JoinByCodeOrId);
            if (refreshButton) refreshButton.onClick.AddListener(Refresh);
            if (readyButton) readyButton.onClick.AddListener(() => sm?.ToggleReady());
            if (startButton) startButton.onClick.AddListener(() => sm?.StartGame());
            if (endGameButton) endGameButton.onClick.AddListener(() => sm?.EndGame());
            if (inviteButton) inviteButton.onClick.AddListener(() => sm?.OpenInviteDialog());
            if (leaveButton) leaveButton.onClick.AddListener(() => sm?.LeaveSession());
            if (copyCodeButton) copyCodeButton.onClick.AddListener(CopyCode);
            if (chatSendButton) chatSendButton.onClick.AddListener(SendChat);
            if (chatInput) chatInput.onEndEdit.AddListener(OnChatEndEdit);
            if (passwordSubmitButton) passwordSubmitButton.onClick.AddListener(SubmitPassword);
            if (passwordCancelButton) passwordCancelButton.onClick.AddListener(() => sm?.LeaveSession());
            if (waitingCancelButton) waitingCancelButton.onClick.AddListener(() => sm?.LeaveSession());
            if (backButton) backButton.onClick.AddListener(GoBack);
            if (chatLogText) chatLogText.text = "";
            SetupPresetDropdown();

            if (sm == null || lobby == null || !lobby.IsAvailable)
            {
                if (playerNameText) playerNameText.text = "오프라인";
                SetStatus("Steam 연결 안 됨: " + SteamManager.LastInitError, true);
                SetInteractable(false);
                RefreshView();
                HandleSessionList(new List<LobbySummary>());
                return;
            }

            sm.OnStateChanged += HandleStateChanged;
            sm.OnSessionError += HandleError;
            sm.OnSessionListUpdated += HandleSessionList;
            sm.OnPasswordRequired += RefreshView;
            sm.OnJoinRequestsChanged += HandleJoinRequests;
            sm.OnGameStarted += HandleGameStarted;
            sm.OnGameEnded += HandleGameEnded;
            lobby.OnLobbyEntered += HandleEntered;
            lobby.OnMembersChanged += HandleMembers;
            lobby.OnLobbyDataChanged += RefreshView;
            lobby.OnChatMessage += HandleChat;

            if (playerNameText) playerNameText.text = SteamManager.Instance.LocalPersonaName;
            SetStatus("Steam 연결됨");
            RefreshView();
            HandleSessionList(sm.Sessions);
            HandleJoinRequests(sm.PendingJoinRequests);
            if (!sm.InSession) Refresh();
        }

        private void OnDestroy()
        {
            if (sm == null) return;
            sm.OnStateChanged -= HandleStateChanged;
            sm.OnSessionError -= HandleError;
            sm.OnSessionListUpdated -= HandleSessionList;
            sm.OnPasswordRequired -= RefreshView;
            sm.OnJoinRequestsChanged -= HandleJoinRequests;
            sm.OnGameStarted -= HandleGameStarted;
            sm.OnGameEnded -= HandleGameEnded;
            if (lobby == null) return;
            lobby.OnLobbyEntered -= HandleEntered;
            lobby.OnMembersChanged -= HandleMembers;
            lobby.OnLobbyDataChanged -= RefreshView;
            lobby.OnChatMessage -= HandleChat;
        }

        private void Update()
        {
            if (sm == null || lobby == null || !lobby.IsAvailable) return;

            bool busy = lobby.IsBusy;
            if (refreshButton) refreshButton.interactable = !busy;
            if (refreshButtonText) refreshButtonText.text = busy ? "검색 중..." : "새로고침";
            if (createButton) createButton.interactable = !busy;
            if (joinCodeButton) joinCodeButton.interactable = !busy;

            if (!sm.InSession && autoRefreshInterval > 0f && !busy && Time.unscaledTime >= nextAutoRefresh)
                Refresh();

            // 시작 가능 여부는 준비 상태 등 여러 곳에서 바뀌므로 주기적으로 갱신
            if (sm.InSession && Time.unscaledTime >= nextHintRefresh)
            {
                nextHintRefresh = Time.unscaledTime + 0.25f;
                RefreshStartButton();
            }
        }

        // ---------------------------------------------------------------
        // 동작

        private void SetupPresetDropdown()
        {
            if (!presetDropdown) return;
            presetDropdown.ClearOptions();
            var list = GetPresets();
            var options = new List<string>();
            foreach (var p in list) options.Add($"{p.DisplayNameOrDefault} ({p.minPlayers}~{p.maxPlayers}명)");
            if (options.Count == 0) options.Add("(프리셋 없음)");
            presetDropdown.AddOptions(options);
            presetDropdown.value = 0;
            presetDropdown.gameObject.SetActive(list.Count > 1);
        }

        private List<SessionPreset> GetPresets()
        {
            var list = new List<SessionPreset>();
            if (presets != null) foreach (var p in presets) if (p != null) list.Add(p);
            if (list.Count == 0 && ServerManager.Instance != null && ServerManager.Instance.DefaultPreset != null)
                list.Add(ServerManager.Instance.DefaultPreset);
            return list;
        }

        private void CreateLobby()
        {
            if (sm == null) return;
            var list = GetPresets();
            SessionPreset preset = list.Count > 0 ? list[Mathf.Clamp(presetDropdown ? presetDropdown.value : 0, 0, list.Count - 1)] : null;

            var options = new SessionCreateOptions
            {
                lobbyName = lobbyNameInput ? lobbyNameInput.text.Trim() : "",
                password = createPasswordInput ? createPasswordInput.text : "",
                unlisted = unlistedToggle && unlistedToggle.isOn,
            };
            if (friendsOnlyToggle && friendsOnlyToggle.isOn) options.visibility = LobbyVisibility.FriendsOnly;

            SetStatus("로비를 만드는 중...");
            sm.CreateSession(preset, options);
        }

        private void JoinByCodeOrId()
        {
            if (sm == null || joinCodeInput == null) return;
            string text = joinCodeInput.text.Trim();
            string password = joinPasswordInput ? joinPasswordInput.text : null;
            if (text.Length == 0) { SetStatus("참가 코드 또는 로비 ID 를 입력하세요.", true); return; }

            // 로비 ID 는 17자리 이상의 숫자, 참가 코드는 6자리
            if (text.Length > 10 && ulong.TryParse(text, out ulong id))
            {
                SetStatus($"로비 {id} 에 참가하는 중...");
                sm.JoinSession(id, password);
            }
            else
            {
                SetStatus($"코드 {text.ToUpperInvariant()} 로 참가하는 중...");
                sm.JoinByCode(text, password);
            }
        }

        private void Refresh()
        {
            nextAutoRefresh = Time.unscaledTime + autoRefreshInterval;
            sm?.SearchSessions();
        }

        private void CopyCode()
        {
            if (sm == null || !sm.InSession) return;
            GUIUtility.systemCopyBuffer = sm.JoinCode;
            SetStatus($"참가 코드 {sm.JoinCode} 를 클립보드에 복사했습니다.");
        }

        private void SubmitPassword()
        {
            if (sm == null || passwordPanelInput == null) return;
            sm.SubmitPassword(passwordPanelInput.text);
            passwordPanelInput.text = "";
            RefreshView();
        }

        private void OnChatEndEdit(string _)
        {
            // onEndEdit 는 포커스 해제 시에도 호출되므로 Enter 키일 때만 전송
            if (EnterPressed()) SendChat();
        }

        private static bool EnterPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
#else
            return false;
#endif
        }

        private void SendChat()
        {
            if (sm == null || chatInput == null || string.IsNullOrWhiteSpace(chatInput.text)) return;
            sm.SendChat(chatInput.text);
            chatInput.text = "";
            chatInput.ActivateInputField();
        }

        private void GoBack()
        {
            sm?.LeaveSession();
            if (!string.IsNullOrEmpty(backSceneName)) SceneManager.LoadScene(backSceneName);
        }

        // ---------------------------------------------------------------
        // 이벤트

        private void HandleStateChanged(SessionState state)
        {
            switch (state)
            {
                case SessionState.Idle: SetStatus("로비 밖입니다."); Refresh(); break;
                case SessionState.WaitingApproval: SetStatus("게임이 진행 중입니다. 호스트 승인을 기다립니다."); break;
                case SessionState.Starting: SetStatus(sm.IsHost ? "리슨 서버를 여는 중..." : "호스트에 접속하는 중..."); break;
            }
            RefreshView();
        }

        private void HandleSessionList(IReadOnlyList<LobbySummary> list)
        {
            foreach (var go in spawnedLobbyItems) Destroy(go);
            spawnedLobbyItems.Clear();

            if (emptyListText)
            {
                emptyListText.gameObject.SetActive(list.Count == 0);
                emptyListText.text = lobby != null && lobby.IsAvailable
                    ? "열린 로비가 없습니다.\n왼쪽에서 새 로비를 만들어 보세요."
                    : "Steam 에 연결되지 않았습니다.";
            }

            if (lobbyListContent == null || lobbyListItemPrefab == null) return;

            foreach (var s in list)
            {
                Button item = Instantiate(lobbyListItemPrefab, lobbyListContent);
                item.gameObject.SetActive(true);
                string name = string.IsNullOrEmpty(s.Name) ? "(이름 없음)" : s.Name;
                SetChildText(item.transform, "Name", (s.HasPassword() ? "[비밀번호] " : "") + name);

                var info = new StringBuilder($"{s.memberCount} / {s.maxMembers}명   호스트 {s.HostName}");
                if (s.IsInGame()) info.Append("   진행 중 (승인 후 참가)");
                SetChildText(item.transform, "Info", info.ToString());

                item.interactable = !s.IsFull;
                ulong id = s.lobbyId;
                item.onClick.AddListener(() =>
                {
                    SetStatus($"'{name}' 에 참가하는 중...");
                    sm.JoinSession(id, joinPasswordInput ? joinPasswordInput.text : null);
                });
                spawnedLobbyItems.Add(item.gameObject);
            }
        }

        private void HandleEntered(ulong lobbyId)
        {
            chatLines.Clear();
            if (chatLogText) chatLogText.text = "";
            SetStatus(sm.IsHost
                ? "로비를 만들었습니다. 참가 코드를 공유하거나 친구를 초대하세요."
                : "로비에 참가했습니다.");
            RefreshView();
        }

        private void HandleMembers(IReadOnlyList<LobbyMember> _) => RefreshView();

        private void HandleChat(LobbyMember who, string text)
        {
            chatLines.Add($"<color=#C9B98A>{who.personaName}</color>  {text}");
            if (chatLines.Count > 100) chatLines.RemoveAt(0);
            if (chatLogText) chatLogText.text = string.Join("\n", chatLines);
            if (chatScroll)
            {
                Canvas.ForceUpdateCanvases();
                chatScroll.verticalNormalizedPosition = 0f;
            }
        }

        private void HandleJoinRequests(IReadOnlyList<JoinRequest> requests)
        {
            foreach (var go in spawnedRequestItems) Destroy(go);
            spawnedRequestItems.Clear();

            bool show = sm != null && sm.IsHost && sm.State == SessionState.InGame;
            if (joinRequestPanel) joinRequestPanel.SetActive(show);
            if (!show || joinRequestContent == null || joinRequestItemPrefab == null) return;

            foreach (var r in requests)
            {
                GameObject row = Instantiate(joinRequestItemPrefab, joinRequestContent);
                row.SetActive(true);
                SetChildText(row.transform, "Name", r.personaName);
                ulong id = r.steamId;
                Button approve = row.transform.Find("Approve")?.GetComponent<Button>();
                Button reject = row.transform.Find("Reject")?.GetComponent<Button>();
                if (approve) approve.onClick.AddListener(() => sm.ApproveJoin(id));
                if (reject) reject.onClick.AddListener(() => sm.RejectJoin(id));
                spawnedRequestItems.Add(row);
            }
        }

        private void HandleGameStarted(bool isHost)
        {
            SetStatus(isHost ? "게임 시작: 내 PC 가 리슨 서버입니다." : "게임 시작: 호스트에 접속했습니다.");
            RefreshView();
        }

        private void HandleGameEnded(GameEndReason reason, string message)
        {
            bool isError = reason == GameEndReason.HostLeft || reason == GameEndReason.ConnectionFailed || reason == GameEndReason.Kicked;
            SetStatus(message, isError);
            RefreshView();
        }

        private void HandleError(string msg) => SetStatus(msg, true);

        // ---------------------------------------------------------------
        // 화면

        private void RefreshView()
        {
            bool inLobby = sm != null && sm.InSession;
            if (browserRoot) browserRoot.SetActive(!inLobby);
            if (roomRoot) roomRoot.SetActive(inLobby);

            bool waiting = inLobby && sm.State == SessionState.WaitingApproval;
            bool needPassword = inLobby && sm.PasswordRequired;
            if (passwordPanel) passwordPanel.SetActive(needPassword);
            if (waitingPanel) waitingPanel.SetActive(waiting && !needPassword);
            if (waitingText && waiting) waitingText.text = "게임이 진행 중입니다.\n호스트가 참가를 승인하면 바로 접속합니다.";

            HandleJoinRequests(sm != null ? sm.PendingJoinRequests : new List<JoinRequest>());
            if (!inLobby) return;

            if (roomTitleText) roomTitleText.text = lobby.GetLobbyData(LobbyKeys.Name);
            if (roomInfoText) roomInfoText.text = $"참가 코드  {sm.JoinCode}      로비 ID  {lobby.CurrentLobbyId}";
            if (roomRuleText)
            {
                var rule = new StringBuilder($"인원 {sm.MinPlayers}~{sm.MaxPlayers}명");
                rule.Append(sm.RequireAllReady ? " · 준비 필요" : " · 준비 불필요");
                rule.Append(sm.AllowLateJoin ? " · 난입 승인제" : " · 난입 불가");
                if (sm.HasPassword) rule.Append(" · 비밀번호");
                if (!sm.IsListed) rule.Append(" · 목록 숨김");
                rule.Append(sm.State == SessionState.InGame ? "      [게임 진행 중]" : "");
                roomRuleText.text = rule.ToString();
            }

            RefreshMembers();

            bool inGame = sm.IsGameActive;
            if (readyButton)
            {
                readyButton.gameObject.SetActive(!sm.IsHost && !inGame && !waiting);
                if (readyButtonText) readyButtonText.text = lobby.IsLocalReady() ? "준비 취소" : "준비 완료";
            }
            if (endGameButton) endGameButton.gameObject.SetActive(sm.IsHost && sm.State == SessionState.InGame);
            if (leaveButtonText) leaveButtonText.text = inGame ? "게임 나가기" : "로비 나가기";
            RefreshStartButton();
        }

        private void RefreshStartButton()
        {
            if (sm == null || !sm.InSession) return;
            string block = sm.GetStartBlockReason();
            bool showStart = sm.IsHost && sm.State == SessionState.InLobby;
            if (startButton)
            {
                startButton.gameObject.SetActive(showStart);
                startButton.interactable = block == null;
            }
            if (startHintText)
            {
                if (showStart) startHintText.text = block ?? "모두 준비되었습니다. 게임을 시작하세요.";
                else if (sm.State == SessionState.InLobby) startHintText.text = "호스트가 게임을 시작하기를 기다리는 중...";
                else if (sm.State == SessionState.InGame) startHintText.text = sm.IsHost ? "게임 진행 중 (내 PC 가 리슨 서버)" : "게임 진행 중";
                else startHintText.text = "";
            }
        }

        private void RefreshMembers()
        {
            if (memberListContent == null || memberItemPrefab == null) return;
            foreach (var go in spawnedMemberItems) Destroy(go);
            spawnedMemberItems.Clear();

            foreach (var m in lobby.Members)
            {
                GameObject row = Instantiate(memberItemPrefab, memberListContent);
                row.SetActive(true);
                string name = (m.isOwner ? "★ " : "") + m.personaName + (m.isLocal ? "  (나)" : "");
                SetChildText(row.transform, "Name", name);

                string statusLabel;
                bool good;
                if (m.isOwner) { statusLabel = "호스트"; good = true; }
                else if (lobby.GetMemberData(m.steamId, SessionKeys.JoinRequest) == "1") { statusLabel = "승인 대기"; good = false; }
                else { statusLabel = m.isReady ? "준비 완료" : "대기 중"; good = m.isReady; }

                Text status = SetChildText(row.transform, "Status", statusLabel);
                if (status) status.color = good ? readyColor : waitingColor;
                spawnedMemberItems.Add(row);
            }

            int limit = sm.MaxPlayers;
            for (int i = lobby.Members.Count; i < limit; i++)
            {
                GameObject row = Instantiate(memberItemPrefab, memberListContent);
                row.SetActive(true);
                Text name = SetChildText(row.transform, "Name", "빈 자리");
                if (name) name.color = emptySlotColor;
                SetChildText(row.transform, "Status", "");
                spawnedMemberItems.Add(row);
            }
        }

        private static Text SetChildText(Transform parent, string childName, string value)
        {
            Transform t = parent.Find(childName);
            Text text = t ? t.GetComponent<Text>() : null;
            if (text) text.text = value;
            return text;
        }

        private void SetInteractable(bool value)
        {
            foreach (var b in new[] { createButton, joinCodeButton, refreshButton, readyButton, startButton, inviteButton, leaveButton, chatSendButton, copyCodeButton })
                if (b) b.interactable = value;
        }

        private void SetStatus(string msg, bool isError = false)
        {
            if (!statusText) return;
            statusText.text = msg;
            statusText.color = isError ? statusErrorColor : statusNormalColor;
        }
    }
}
