using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Magnetic.SteamLobby
{
    /// <summary>
    /// Steam 로비로 플레이어를 모으고, 게임 시작 시 호스트 PC 에서 Netcode for GameObjects 리슨 서버를 실행하는 세션 관리자.
    ///
    /// 흐름
    /// 1. 호스트: <see cref="CreateSession"/> (프리셋의 최소/최대 인원·규칙으로 로비 생성)
    /// 2. 참가자: <see cref="SearchSessions"/> → <see cref="JoinSession"/> / <see cref="JoinByCode"/> / Steam 친구 초대
    /// 3. 참가자: <see cref="SetReady"/>
    /// 4. 호스트: <see cref="StartGame"/> → 호스트 PC 에서 StartHost() (리슨 서버) → 로비에 서버 정보 기록
    /// 5. 참가자: 로비 데이터 변경을 감지해 호스트 SteamID 로 자동 접속 (StartClient)
    /// 6. 게임 중 들어온 플레이어: 승인 대기열(<see cref="PendingJoinRequests"/>) → 호스트가 <see cref="ApproveJoin"/> 하면 접속
    /// 7. 호스트: <see cref="EndGame"/> → 모두 로비로 복귀. 호스트가 나가면 모두 게임 종료 후 로비 밖으로 복귀.
    ///
    /// 같은 오브젝트에 SteamManager, SteamLobbyService, NetworkManager, SteamNetworkingSocketsTransport 를 함께 둔다.
    /// (메뉴 GameObject > Magnetic > Steam Session Services 로 한 번에 생성)
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Magnetic/Steam Lobby/Server Manager")]
    public class ServerManager : MonoBehaviour
    {
        [Header("기본 설정")]
        [Tooltip("CreateSession 에 프리셋을 넘기지 않았을 때 사용할 프리셋")]
        [SerializeField] private SessionPreset defaultPreset;

        [Tooltip("true 면 로비 검색 시 기본 프리셋과 같은 모드의 로비만 찾는다.")]
        [SerializeField] private bool filterSearchByMode = true;

        [Tooltip("게임이 끝난 뒤 돌아갈 씬. 비우면 게임을 시작한 시점의 씬으로 돌아간다.")]
        [SerializeField] private string returnSceneName = "";

        [Header("시간 (초)")]
        [Tooltip("비밀번호 로비에서 참가자가 비밀번호를 입력해야 하는 제한 시간. 넘기면 내보낸다.")]
        [SerializeField] private float passwordTimeout = 30f;

        [Tooltip("호스트 접속 제한 시간")]
        [SerializeField] private float connectTimeout = 20f;

        [Tooltip("호스트가 게임 종료를 알린 뒤 서버를 닫기까지 기다리는 시간 (종료 알림 전달용)")]
        [SerializeField] private float hostEndDelay = 0.5f;

        [Tooltip("호스트 연결이 끊긴 뒤, 호스트가 로비에 남아 있는지 확인하기까지 기다리는 시간")]
        [SerializeField] private float disconnectCheckDelay = 1.5f;

        [Header("참조 (비우면 같은 오브젝트에서 찾음)")]
        [SerializeField] private SteamLobbyService lobbyService;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private SteamNetworkingSocketsTransport transport;

        public static ServerManager Instance { get; private set; }

        // =====================================================================
        // 상태
        // =====================================================================
        public SessionState State { get; private set; } = SessionState.Offline;
        public SteamLobbyService Lobby => lobbyService;
        public NetworkManager NetworkManager => networkManager;
        public SteamNetworkingSocketsTransport Transport => transport;
        public SessionPreset DefaultPreset => defaultPreset;
        /// <summary>호스트가 로비를 만들 때 사용한 프리셋 (참가자이거나 호스트를 넘겨받았다면 null)</summary>
        public SessionPreset ActivePreset { get; private set; }

        public bool IsHost => lobbyService != null && lobbyService.IsHost;
        public bool InSession => lobbyService != null && lobbyService.InLobby;
        public int PlayerCount => lobbyService != null ? lobbyService.Members.Count : 0;
        public int MinPlayers => int.TryParse(GetData(SessionKeys.MinPlayers), out int v) ? Mathf.Max(1, v) : 1;
        public int MaxPlayers => lobbyService != null ? lobbyService.MemberLimit : 0;
        public bool RequireAllReady => GetData(SessionKeys.RequireReady) != "0";
        public bool AllowLateJoin => GetData(SessionKeys.LateJoin) == "1";
        public bool HasPassword => !string.IsNullOrEmpty(GetData(SessionKeys.PasswordSalt));
        public bool IsListed => GetData(SessionKeys.Listed) != "0";
        public string JoinCode => GetData(SessionKeys.Code);
        public string ModeId => GetData(SessionKeys.Mode);

        /// <summary>참가자: 비밀번호 로비에 비밀번호 없이 들어와 입력이 필요한 상태</summary>
        public bool PasswordRequired { get; private set; }

        /// <summary>호스트: 게임 중 난입을 요청한 플레이어 목록</summary>
        public IReadOnlyList<JoinRequest> PendingJoinRequests => pendingRequests;

        /// <summary>마지막 검색 결과</summary>
        public IReadOnlyList<LobbySummary> Sessions => sessions;

        /// <summary>
        /// 호스트: 추가 접속 검사. (참가자 SteamID, 요청) → 거절 사유 문자열, 허용이면 null.
        /// 로비 멤버·명단·승인 검사를 통과한 연결에만 호출된다.
        /// </summary>
        public Func<ulong, NetworkManager.ConnectionApprovalRequest, string> AdditionalApprovalCheck;

        // =====================================================================
        // 이벤트
        // =====================================================================
        public event Action<SessionState> OnStateChanged;
        /// <summary>사용자에게 보여줄 오류 메시지 (한국어)</summary>
        public event Action<string> OnSessionError;
        /// <summary>검색 결과</summary>
        public event Action<IReadOnlyList<LobbySummary>> OnSessionListUpdated;
        /// <summary>참가자: 비밀번호 입력이 필요함 → <see cref="SubmitPassword"/> 호출</summary>
        public event Action OnPasswordRequired;
        /// <summary>호스트: 난입 대기열 변경</summary>
        public event Action<IReadOnlyList<JoinRequest>> OnJoinRequestsChanged;
        /// <summary>게임 시작 (인자: 내가 호스트인지)</summary>
        public event Action<bool> OnGameStarted;
        /// <summary>게임 종료 (이유, 설명)</summary>
        public event Action<GameEndReason, string> OnGameEnded;

        // =====================================================================
        // 내부 상태
        // =====================================================================
        private readonly List<LobbySummary> sessions = new List<LobbySummary>();
        private readonly List<JoinRequest> pendingRequests = new List<JoinRequest>();
        private readonly HashSet<ulong> roster = new HashSet<ulong>();
        private readonly HashSet<ulong> approved = new HashSet<ulong>();
        private readonly HashSet<ulong> passwordVerified = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> passwordDeadlines = new Dictionary<ulong, float>();

        private string hostPassword;        // 호스트 PC 에만 존재
        private string pendingJoinPassword; // 참가 시 입력한 비밀번호
        private string activeGameScene;
        private string activeReturnScene;
        private ulong connectedHostSteamId;
        private float connectStartedAt;
        private Coroutine endRoutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake()
        {
            if (Instance != null && Instance != this) return; // 중복 오브젝트는 SteamManager 가 제거
            Instance = this;

            if (lobbyService == null) lobbyService = GetComponent<SteamLobbyService>();
            if (networkManager == null) networkManager = GetComponent<NetworkManager>();
            if (transport == null) transport = GetComponent<SteamNetworkingSocketsTransport>();

            if (networkManager != null && transport != null)
            {
                if (networkManager.NetworkConfig == null) networkManager.NetworkConfig = new NetworkConfig();
                networkManager.NetworkConfig.NetworkTransport = transport;
            }
        }

        private void Start()
        {
            if (Instance != this) return;

            if (lobbyService == null || networkManager == null || transport == null)
            {
                SteamLog.Error(SteamLogCategory.Session,
                    "ServerManager 설정 오류: SteamLobbyService / NetworkManager / SteamNetworkingSocketsTransport 중 빠진 것이 있습니다. " +
                    "메뉴 [GameObject > Magnetic > Steam Session Services] 로 다시 만들어 주세요.");
                enabled = false;
                return;
            }

            lobbyService.OnLobbyEntered += HandleLobbyEntered;
            lobbyService.OnLobbyLeft += HandleLobbyLeft;
            lobbyService.OnLobbyDataChanged += HandleLobbyDataChanged;
            lobbyService.OnMemberDataChanged += HandleMemberDataChanged;
            lobbyService.OnMemberJoined += HandleMemberJoined;
            lobbyService.OnMemberLeft += HandleMemberLeft;
            lobbyService.OnOwnerChanged += HandleOwnerChanged;
            lobbyService.OnSystemMessage += HandleSystemMessage;
            lobbyService.OnError += ForwardLobbyError;

            networkManager.NetworkConfig.ConnectionApproval = true;
            networkManager.ConnectionApprovalCallback = ApprovalCheck;
            networkManager.OnClientConnectedCallback += HandleNetClientConnected;
            networkManager.OnClientDisconnectCallback += HandleNetClientDisconnected;
            networkManager.OnTransportFailure += HandleTransportFailure;

            SetState(SteamManager.Initialized ? SessionState.Idle : SessionState.Offline);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;

            if (lobbyService != null)
            {
                lobbyService.OnLobbyEntered -= HandleLobbyEntered;
                lobbyService.OnLobbyLeft -= HandleLobbyLeft;
                lobbyService.OnLobbyDataChanged -= HandleLobbyDataChanged;
                lobbyService.OnMemberDataChanged -= HandleMemberDataChanged;
                lobbyService.OnMemberJoined -= HandleMemberJoined;
                lobbyService.OnMemberLeft -= HandleMemberLeft;
                lobbyService.OnOwnerChanged -= HandleOwnerChanged;
                lobbyService.OnSystemMessage -= HandleSystemMessage;
                lobbyService.OnError -= ForwardLobbyError;
            }

            if (networkManager != null)
            {
                networkManager.OnClientConnectedCallback -= HandleNetClientConnected;
                networkManager.OnClientDisconnectCallback -= HandleNetClientDisconnected;
                networkManager.OnTransportFailure -= HandleTransportFailure;
            }
        }

        /// <summary>SteamManager 가 SteamAPI.Shutdown() 직전에 호출</summary>
        internal void HandleSteamShutdown()
        {
            if (networkManager != null && networkManager.IsListening) networkManager.Shutdown(true);
        }

        private void Update()
        {
            if (Instance != this) return;

            // 호스트: 비밀번호 제한 시간 검사
            if (IsHost && passwordDeadlines.Count > 0)
            {
                float now = Time.unscaledTime;
                foreach (var kv in passwordDeadlines.ToList())
                {
                    if (now >= kv.Value && !passwordVerified.Contains(kv.Key))
                    {
                        passwordDeadlines.Remove(kv.Key);
                        Kick(kv.Key, "비밀번호 확인 시간이 초과되었습니다.");
                    }
                }
            }

            // 참가자: 호스트 접속 제한 시간
            if (State == SessionState.Starting && !IsHost && Time.unscaledTime - connectStartedAt > connectTimeout)
            {
                SteamLog.Warn(SteamLogCategory.Session, "호스트 접속 시간이 초과되었습니다.");
                FinishLocalGame(GameEndReason.ConnectionFailed, false, "호스트에 접속하지 못했습니다 (시간 초과).");
            }
        }

        // =====================================================================
        // Public API : 로비
        // =====================================================================

        /// <summary>호스트로 새 세션(로비)을 만든다. 결과는 <see cref="OnStateChanged"/>(InLobby) 또는 <see cref="OnSessionError"/>.</summary>
        public void CreateSession(SessionPreset preset = null, SessionCreateOptions options = null)
        {
            if (!CheckReady("로비 생성")) return;
            preset = preset != null ? preset : defaultPreset;
            if (preset == null)
            {
                RaiseError("로비 생성 실패: 세션 프리셋이 없습니다. ServerManager 에 기본 프리셋을 지정하세요.");
                return;
            }
            options ??= new SessionCreateOptions();

            int max = Mathf.Clamp(options.maxPlayers ?? preset.maxPlayers, 1, 250);
            int min = Mathf.Clamp(options.minPlayers ?? preset.minPlayers, 1, max);
            LobbyVisibility visibility = options.visibility ?? preset.visibility;

            ClearLobbyState();
            hostPassword = string.IsNullOrEmpty(options.password) ? null : options.password;
            ActivePreset = preset;
            activeGameScene = preset.gameSceneName;

            var data = new Dictionary<string, string>
            {
                { SessionKeys.Mode, preset.modeId ?? string.Empty },
                { SessionKeys.MinPlayers, min.ToString() },
                { SessionKeys.RequireReady, preset.requireAllReady ? "1" : "0" },
                { SessionKeys.LateJoin, preset.allowLateJoin ? "1" : "0" },
                { SessionKeys.State, SessionKeys.StateWaiting },
                { SessionKeys.Joinable, "1" },
                { SessionKeys.Listed, options.unlisted ? "0" : "1" },
                { SessionKeys.Code, SessionUtil.GenerateCode() },
                { SessionKeys.PasswordSalt, hostPassword != null ? SessionUtil.GenerateSalt() : string.Empty },
            };

            if (visibility != LobbyVisibility.Public)
                SteamLog.Info(SteamLogCategory.Session, $"공개 범위가 {visibility} 이면 참가 코드 검색이 동작하지 않습니다. 초대 또는 로비 ID 로 참가하세요.");

            SteamLog.Info(SteamLogCategory.Session,
                $"세션 생성: 프리셋='{preset.DisplayNameOrDefault}', 인원 {min}~{max}명, 준비 필요={preset.requireAllReady}, " +
                $"난입 허용={preset.allowLateJoin}, 비밀번호={(hostPassword != null ? "있음" : "없음")}, 목록 표시={!options.unlisted}");
            lobbyService.CreateLobby(options.lobbyName, visibility, max, data);
        }

        /// <summary>참가 가능한 세션을 검색한다. 결과는 <see cref="OnSessionListUpdated"/>.</summary>
        public void SearchSessions()
        {
            if (!CheckReady("로비 검색")) return;
            var filters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(SessionKeys.Joinable, "1"),
                new KeyValuePair<string, string>(SessionKeys.Listed, "1"),
            };
            if (filterSearchByMode && defaultPreset != null && !string.IsNullOrEmpty(defaultPreset.modeId))
                filters.Add(new KeyValuePair<string, string>(SessionKeys.Mode, defaultPreset.modeId));

            lobbyService.RequestLobbyList(filters, results =>
            {
                sessions.Clear();
                sessions.AddRange(results);
                OnSessionListUpdated?.Invoke(sessions);
            });
        }

        /// <summary>로비 ID 로 참가. 비밀번호 로비면 password 를 함께 넘기거나, 나중에 <see cref="SubmitPassword"/>.</summary>
        public void JoinSession(ulong lobbyId, string password = null)
        {
            if (!CheckReady("로비 참가")) return;
            if (IsGameActive) { RaiseError("게임 중에는 다른 로비에 참가할 수 없습니다. 먼저 게임을 나가세요."); return; }
            pendingJoinPassword = string.IsNullOrEmpty(password) ? null : password;
            lobbyService.JoinLobby(lobbyId);
        }

        /// <summary>6자리 참가 코드로 참가 (공개 범위 Public 로비만 가능)</summary>
        public void JoinByCode(string code, string password = null)
        {
            if (!CheckReady("코드로 참가")) return;
            string normalized = SessionUtil.NormalizeCode(code);
            if (normalized.Length == 0) { RaiseError("참가 코드를 입력하세요."); return; }

            SteamLog.Info(SteamLogCategory.Session, $"참가 코드 '{normalized}' 로 로비를 찾습니다.");
            var filters = new[] { new KeyValuePair<string, string>(SessionKeys.Code, normalized) };
            lobbyService.RequestLobbyList(filters, results =>
            {
                if (results.Count == 0)
                {
                    RaiseError($"참가 코드 '{normalized}' 에 해당하는 로비를 찾지 못했습니다.");
                    return;
                }
                LobbySummary target = results[0];
                if (target.GetData(SessionKeys.Joinable) != "1")
                {
                    RaiseError("해당 로비는 지금 참가할 수 없습니다 (게임 진행 중이거나 참가가 막혀 있음).");
                    return;
                }
                JoinSession(target.lobbyId, password);
            });
        }

        /// <summary>참가자: 비밀번호 제출 (비밀번호 로비 입장 후)</summary>
        public void SubmitPassword(string password)
        {
            if (!InSession || IsHost) return;
            string salt = GetData(SessionKeys.PasswordSalt);
            if (string.IsNullOrEmpty(salt)) { PasswordRequired = false; return; }
            if (string.IsNullOrEmpty(password)) { RaiseError("비밀번호를 입력하세요."); return; }

            string proof = SessionUtil.PasswordProof(salt, password, SteamLobbyService.LocalSteamId);
            lobbyService.SetMemberData(SessionKeys.PasswordProof, proof);
            PasswordRequired = false;
            SteamLog.Info(SteamLogCategory.Session, "비밀번호를 제출했습니다. 호스트 확인을 기다립니다.");
        }

        public void SetReady(bool ready)
        {
            if (State != SessionState.InLobby) return;
            lobbyService.SetReady(ready);
        }

        public void ToggleReady() => SetReady(!lobbyService.IsLocalReady());

        public void OpenInviteDialog() => lobbyService.OpenInviteDialog();

        public void SendChat(string message) => lobbyService.SendChat(message);

        /// <summary>세션을 나간다. 게임 중이면 게임에서도 나간다 (호스트가 나가면 다른 플레이어들의 게임도 종료).</summary>
        public void LeaveSession()
        {
            bool wasInGame = State == SessionState.Starting || State == SessionState.InGame || State == SessionState.Ending;
            if (endRoutine != null) { StopCoroutine(endRoutine); endRoutine = null; }

            if (wasInGame)
            {
                FinishLocalGame(GameEndReason.LocalLeft, false, "게임을 나갔습니다.");
                return;
            }

            if (networkManager.IsListening) networkManager.Shutdown();
            lobbyService.LeaveLobby();
        }

        /// <summary>호스트: 플레이어를 내보낸다 (로비와 게임 모두).</summary>
        public void Kick(ulong steamId, string reason = "호스트가 내보냈습니다.")
        {
            if (!IsHost || steamId == SteamLobbyService.LocalSteamId) return;
            SteamLog.Info(SteamLogCategory.Session, $"{lobbyService.GetPersonaName(steamId)} 님을 내보냅니다: {reason}");

            roster.Remove(steamId);
            if (approved.Remove(steamId)) lobbyService.SetLobbyData(SessionKeys.Approved, SessionUtil.JoinIdList(approved));
            RemovePendingRequest(steamId);
            passwordDeadlines.Remove(steamId);

            lobbyService.SendSystemMessage(SessionKeys.CmdKick, $"{steamId}:{reason}");

            if (networkManager.IsServer && TryFindClientId(steamId, out ulong clientId))
                networkManager.DisconnectClient(clientId, reason);
        }

        // =====================================================================
        // Public API : 게임
        // =====================================================================

        /// <summary>게임 진행 관련 상태인지 (시작 중 / 진행 중 / 종료 중)</summary>
        public bool IsGameActive => State == SessionState.Starting || State == SessionState.InGame || State == SessionState.Ending;

        public bool CanStartGame => GetStartBlockReason() == null;

        /// <summary>지금 게임을 시작할 수 없는 이유 (한국어). 시작 가능하면 null.</summary>
        public string GetStartBlockReason()
        {
            if (!InSession) return "로비에 있지 않습니다.";
            if (!IsHost) return "호스트만 게임을 시작할 수 있습니다.";
            if (State != SessionState.InLobby) return "지금은 게임을 시작할 수 없습니다.";
            if (networkManager.IsListening || networkManager.ShutdownInProgress) return "이전 네트워크 세션을 정리하는 중입니다.";

            int count = PlayerCount;
            if (count < MinPlayers) return $"최소 {MinPlayers}명이 필요합니다 (현재 {count}명).";
            if (MaxPlayers > 0 && count > MaxPlayers) return $"최대 {MaxPlayers}명까지 시작할 수 있습니다 (현재 {count}명).";

            ulong me = SteamLobbyService.LocalSteamId;
            if (RequireAllReady)
            {
                foreach (var m in lobbyService.Members)
                    if (m.steamId != me && !m.isReady) return "모든 플레이어가 준비 완료해야 합니다.";
            }

            if (HasPassword && hostPassword != null)
            {
                foreach (var m in lobbyService.Members)
                    if (m.steamId != me && !passwordVerified.Contains(m.steamId)) return "비밀번호 확인 중인 플레이어가 있습니다.";
            }
            return null;
        }

        /// <summary>
        /// 호스트: 게임 시작. 호스트 PC 에서 리슨 서버(StartHost)를 열고, 로비에 서버 정보를 기록해 참가자들이 접속하게 한다.
        /// 프리셋에 게임 씬이 지정되어 있으면 NetworkSceneManager 로 모두 함께 이동한다.
        /// </summary>
        public bool StartGame()
        {
            string block = GetStartBlockReason();
            if (block != null) { RaiseError("게임 시작 불가: " + block); return false; }

            activeReturnScene = string.IsNullOrEmpty(returnSceneName) ? SceneManager.GetActiveScene().name : returnSceneName;
            if (string.IsNullOrEmpty(activeGameScene) && defaultPreset != null) activeGameScene = defaultPreset.gameSceneName;

            roster.Clear();
            approved.Clear();
            foreach (var m in lobbyService.Members) roster.Add(m.steamId);

            SetState(SessionState.Starting);
            SteamLog.Info(SteamLogCategory.Session, $"게임을 시작합니다. 리슨 서버를 엽니다 (참가자 {roster.Count}명).");

            if (!networkManager.StartHost())
            {
                SetState(SessionState.InLobby);
                RaiseError("게임 시작 실패: 리슨 서버를 열지 못했습니다. 로그를 확인하세요.");
                return false;
            }

            bool lateJoin = AllowLateJoin;
            lobbyService.SetLobbyData(SessionKeys.Roster, SessionUtil.JoinIdList(roster));
            lobbyService.SetLobbyData(SessionKeys.Approved, string.Empty);
            lobbyService.SetLobbyData(SessionKeys.GameServer, SteamLobbyService.LocalSteamId.ToString());
            lobbyService.SetLobbyData(SessionKeys.Joinable, lateJoin ? "1" : "0");
            lobbyService.SetLobbyData(SessionKeys.State, SessionKeys.StateInGame); // 참가자는 이 값을 보고 접속
            lobbyService.SetLobbyJoinable(lateJoin);

            connectedHostSteamId = SteamLobbyService.LocalSteamId;
            SetState(SessionState.InGame);
            OnGameStarted?.Invoke(true);

            if (!string.IsNullOrEmpty(activeGameScene))
            {
                SteamLog.Info(SteamLogCategory.Session, $"게임 씬 '{activeGameScene}' 을 불러옵니다.");
                var status = networkManager.SceneManager.LoadScene(activeGameScene, LoadSceneMode.Single);
                if (status != SceneEventProgressStatus.Started)
                    RaiseError($"게임 씬 '{activeGameScene}' 을 불러오지 못했습니다: {status} (Build Profiles 의 씬 목록을 확인하세요)");
            }
            return true;
        }

        /// <summary>호스트: 게임을 끝내고 모두 로비로 돌아간다 (로비는 유지되어 다시 시작 가능).</summary>
        public void EndGame()
        {
            if (!IsHost || State != SessionState.InGame) return;
            SteamLog.Info(SteamLogCategory.Session, "호스트가 게임을 종료합니다. 모두 로비로 돌아갑니다.");
            SetState(SessionState.Ending);

            ResetLobbyToWaiting();
            lobbyService.SendSystemMessage(SessionKeys.CmdGameEnd);
            endRoutine = StartCoroutine(HostEndAfterDelay());
        }

        /// <summary>호스트: 난입 요청 승인. 승인된 플레이어는 진행 중인 게임에 접속한다.</summary>
        public void ApproveJoin(ulong steamId)
        {
            if (!IsHost || State != SessionState.InGame) return;
            if (!RemovePendingRequest(steamId)) return;
            approved.Add(steamId);
            lobbyService.SetLobbyData(SessionKeys.Approved, SessionUtil.JoinIdList(approved));
            SteamLog.Info(SteamLogCategory.Session, $"{lobbyService.GetPersonaName(steamId)} 님의 참가를 승인했습니다.");
        }

        /// <summary>호스트: 난입 요청 거절. 거절된 플레이어는 로비에서 나간다.</summary>
        public void RejectJoin(ulong steamId)
        {
            if (!IsHost) return;
            if (!RemovePendingRequest(steamId)) return;
            lobbyService.SendSystemMessage(SessionKeys.CmdReject, steamId.ToString());
            SteamLog.Info(SteamLogCategory.Session, $"{lobbyService.GetPersonaName(steamId)} 님의 참가를 거절했습니다.");
        }

        /// <summary>호스트: NGO clientId 에 해당하는 SteamID</summary>
        public bool TryGetSteamId(ulong clientId, out ulong steamId) => transport.TryGetSteamId(clientId, out steamId);

        // =====================================================================
        // 로비 이벤트 처리
        // =====================================================================

        private void HandleLobbyEntered(ulong lobbyId)
        {
            PasswordRequired = false;

            if (IsHost)
            {
                // 직접 만든 로비
                SetState(SessionState.InLobby);
                return;
            }

            // 참가자
            ActivePreset = null;
            hostPassword = null;
            activeGameScene = null;

            if (HasPassword)
            {
                if (pendingJoinPassword != null)
                {
                    SubmitPassword(pendingJoinPassword);
                }
                else
                {
                    PasswordRequired = true;
                    SteamLog.Info(SteamLogCategory.Session, "비밀번호가 필요한 로비입니다. 비밀번호를 입력하세요.");
                    OnPasswordRequired?.Invoke();
                }
            }
            pendingJoinPassword = null;

            if (GetData(SessionKeys.State) == SessionKeys.StateInGame)
            {
                SetState(SessionState.InLobby);
                HandleJoinedInGameLobby();
            }
            else
            {
                SetState(SessionState.InLobby);
            }
        }

        private void HandleLobbyLeft()
        {
            pendingJoinPassword = null;
            ClearLobbyState();
            if (State == SessionState.Ending) return; // FinishLocalGame 이 마무리
            if (networkManager != null && networkManager.IsListening) networkManager.Shutdown();
            SetState(SteamManager.Initialized ? SessionState.Idle : SessionState.Offline);
        }

        private void HandleLobbyDataChanged()
        {
            if (IsHost) return;
            string lobbyState = GetData(SessionKeys.State);

            switch (State)
            {
                case SessionState.InLobby:
                    if (lobbyState == SessionKeys.StateInGame)
                    {
                        if (SessionUtil.ParseIdList(GetData(SessionKeys.Roster)).Contains(SteamLobbyService.LocalSteamId))
                            ConnectToHost();
                        else
                            HandleJoinedInGameLobby();
                    }
                    break;

                case SessionState.WaitingApproval:
                    if (lobbyState == SessionKeys.StateWaiting)
                    {
                        // 호스트가 게임을 끝냄 → 대기 로비로 전환
                        lobbyService.SetMemberData(SessionKeys.JoinRequest, string.Empty);
                        SetState(SessionState.InLobby);
                        SteamLog.Info(SteamLogCategory.Session, "진행 중이던 게임이 끝났습니다. 로비에서 다음 게임을 기다립니다.");
                    }
                    else if (SessionUtil.ParseIdList(GetData(SessionKeys.Approved)).Contains(SteamLobbyService.LocalSteamId))
                    {
                        SteamLog.Info(SteamLogCategory.Session, "호스트가 참가를 승인했습니다. 게임에 접속합니다.");
                        lobbyService.SetMemberData(SessionKeys.JoinRequest, string.Empty);
                        ConnectToHost();
                    }
                    break;
            }
        }

        private void HandleMemberJoined(ulong steamId)
        {
            if (!IsHost || steamId == SteamLobbyService.LocalSteamId) return;
            if (HasPassword && hostPassword != null)
                passwordDeadlines[steamId] = Time.unscaledTime + passwordTimeout;
            RefreshJoinRequests();
        }

        private void HandleMemberLeft(ulong steamId)
        {
            passwordVerified.Remove(steamId);
            passwordDeadlines.Remove(steamId);
            if (IsHost) RemovePendingRequest(steamId);
        }

        private void HandleMemberDataChanged(ulong steamId)
        {
            if (!IsHost || steamId == SteamLobbyService.LocalSteamId) return;
            CheckPassword(steamId);
            RefreshJoinRequests();
        }

        private void HandleOwnerChanged(ulong newOwner)
        {
            if (newOwner != SteamLobbyService.LocalSteamId) return;

            // 게임 중 호스트가 나간 경우는 연결 끊김 처리(FinishLocalGame)가 담당
            if (State != SessionState.InLobby) return;

            SteamLog.Warn(SteamLogCategory.Session, "이전 호스트가 나가 내가 새 호스트가 되었습니다.");
            ActivePreset = null;
            activeGameScene = defaultPreset != null ? defaultPreset.gameSceneName : null;

            // 기존 멤버는 모두 확인된 것으로 간주
            foreach (var m in lobbyService.Members) passwordVerified.Add(m.steamId);

            if (HasPassword && hostPassword == null)
            {
                // 비밀번호는 이전 호스트 PC 에만 있었으므로 새 참가자를 확인할 수 없다 → 새 참가 차단
                lobbyService.SetLobbyData(SessionKeys.Joinable, "0");
                lobbyService.SetLobbyJoinable(false);
                SteamLog.Warn(SteamLogCategory.Session, "비밀번호 로비의 호스트가 바뀌어 새 참가를 막았습니다.");
            }
            OnStateChanged?.Invoke(State); // UI 갱신용 (호스트 전용 버튼 표시)
        }

        private void HandleSystemMessage(ulong sender, string command, string arg)
        {
            // 시스템 메시지는 로비 소유자가 보낸 것만 신뢰
            if (sender != lobbyService.OwnerSteamId || sender == SteamLobbyService.LocalSteamId) return;
            ulong me = SteamLobbyService.LocalSteamId;

            switch (command)
            {
                case SessionKeys.CmdKick:
                {
                    int sep = arg.IndexOf(':');
                    string idPart = sep >= 0 ? arg.Substring(0, sep) : arg;
                    string reason = sep >= 0 ? arg.Substring(sep + 1) : "호스트가 내보냈습니다.";
                    if (!ulong.TryParse(idPart, out ulong target) || target != me) return;

                    SteamLog.Warn(SteamLogCategory.Session, $"호스트가 나를 내보냈습니다: {reason}");
                    if (IsGameActive) FinishLocalGame(GameEndReason.Kicked, false, reason);
                    else { RaiseError(reason); lobbyService.LeaveLobby(); }
                    break;
                }

                case SessionKeys.CmdReject:
                    if (!ulong.TryParse(arg, out ulong rejected) || rejected != me) return;
                    if (State != SessionState.WaitingApproval) return;
                    RaiseError("호스트가 참가 요청을 거절했습니다.");
                    lobbyService.LeaveLobby();
                    break;

                case SessionKeys.CmdGameEnd:
                    if (State == SessionState.InGame || State == SessionState.Starting)
                        FinishLocalGame(GameEndReason.HostEnded, true, "호스트가 게임을 종료했습니다.");
                    break;
            }
        }

        // =====================================================================
        // 세션 내부 로직
        // =====================================================================

        /// <summary>참가자: 게임이 진행 중인 로비에 들어왔을 때</summary>
        private void HandleJoinedInGameLobby()
        {
            ulong me = SteamLobbyService.LocalSteamId;
            var rosterIds = SessionUtil.ParseIdList(GetData(SessionKeys.Roster));
            var approvedIds = SessionUtil.ParseIdList(GetData(SessionKeys.Approved));

            if (rosterIds.Contains(me) || approvedIds.Contains(me))
            {
                SteamLog.Info(SteamLogCategory.Session, "진행 중인 게임의 참가자입니다. 다시 접속합니다.");
                ConnectToHost();
            }
            else if (AllowLateJoin)
            {
                lobbyService.SetMemberData(SessionKeys.JoinRequest, "1");
                SetState(SessionState.WaitingApproval);
                SteamLog.Info(SteamLogCategory.Session, "게임이 진행 중입니다. 호스트의 참가 승인을 기다립니다.");
            }
            else
            {
                RaiseError("이미 게임이 진행 중인 로비입니다.");
                lobbyService.LeaveLobby();
            }
        }

        /// <summary>참가자: 로비에 기록된 호스트 SteamID 로 리슨 서버에 접속</summary>
        private void ConnectToHost()
        {
            if (!ulong.TryParse(GetData(SessionKeys.GameServer), out ulong hostId) || hostId == 0)
            {
                RaiseError("호스트 서버 정보를 찾지 못했습니다.");
                return;
            }
            if (networkManager.IsListening || networkManager.ShutdownInProgress)
            {
                RaiseError("이전 네트워크 세션을 정리하는 중입니다. 잠시 후 다시 시도하세요.");
                return;
            }

            activeReturnScene = string.IsNullOrEmpty(returnSceneName) ? SceneManager.GetActiveScene().name : returnSceneName;
            connectedHostSteamId = hostId;
            transport.ConnectToSteamId = hostId;
            networkManager.NetworkConfig.ConnectionData = BitConverter.GetBytes(SteamLobbyService.LocalSteamId);

            SetState(SessionState.Starting);
            connectStartedAt = Time.unscaledTime;
            SteamLog.Info(SteamLogCategory.Session, $"호스트 {lobbyService.GetPersonaName(hostId)} 의 리슨 서버에 접속합니다.");

            if (!networkManager.StartClient())
                FinishLocalGame(GameEndReason.ConnectionFailed, false, "호스트 접속을 시작하지 못했습니다.");
        }

        /// <summary>호스트: NGO 접속 승인 검사</summary>
        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            response.CreatePlayerObject = networkManager.NetworkConfig.PlayerPrefab != null;
            response.Pending = false;

            if (request.ClientNetworkId == NetworkManager.ServerClientId)
            {
                response.Approved = true; // 호스트 자신
                return;
            }

            string reason = null;
            if (!transport.TryGetSteamId(request.ClientNetworkId, out ulong steamId)) reason = "알 수 없는 연결입니다.";
            else if (!lobbyService.IsMember(steamId)) reason = "로비 멤버가 아닙니다.";
            else if (!roster.Contains(steamId) && !approved.Contains(steamId)) reason = "호스트 승인이 필요합니다.";
            else if (AdditionalApprovalCheck != null) reason = AdditionalApprovalCheck(steamId, request);

            response.Approved = reason == null;
            response.Reason = reason;

            if (reason == null) SteamLog.Info(SteamLogCategory.Session, $"{lobbyService.GetPersonaName(steamId)} 님의 게임 접속을 허용했습니다.");
            else SteamLog.Warn(SteamLogCategory.Session, $"게임 접속 거부 (SteamID={steamId}): {reason}");
        }

        /// <summary>호스트: 비밀번호 증명값 확인</summary>
        private void CheckPassword(ulong steamId)
        {
            if (!HasPassword || hostPassword == null || passwordVerified.Contains(steamId)) return;
            string proof = lobbyService.GetMemberData(steamId, SessionKeys.PasswordProof);
            if (string.IsNullOrEmpty(proof)) return; // 아직 입력 전

            string expected = SessionUtil.PasswordProof(GetData(SessionKeys.PasswordSalt), hostPassword, steamId);
            if (proof == expected)
            {
                passwordVerified.Add(steamId);
                passwordDeadlines.Remove(steamId);
                SteamLog.Info(SteamLogCategory.Session, $"{lobbyService.GetPersonaName(steamId)} 님의 비밀번호를 확인했습니다.");
            }
            else
            {
                passwordDeadlines.Remove(steamId);
                Kick(steamId, "비밀번호가 틀렸습니다.");
            }
        }

        /// <summary>호스트: 난입 대기열 갱신</summary>
        private void RefreshJoinRequests()
        {
            if (!IsHost) return;
            bool changed = false;
            ulong me = SteamLobbyService.LocalSteamId;

            if (State == SessionState.InGame)
            {
                foreach (var m in lobbyService.Members)
                {
                    if (m.steamId == me || roster.Contains(m.steamId) || approved.Contains(m.steamId)) continue;
                    if (lobbyService.GetMemberData(m.steamId, SessionKeys.JoinRequest) != "1") continue;
                    if (HasPassword && hostPassword != null && !passwordVerified.Contains(m.steamId)) continue;
                    if (pendingRequests.Exists(r => r.steamId == m.steamId)) continue;

                    pendingRequests.Add(new JoinRequest { steamId = m.steamId, personaName = m.personaName, requestTime = Time.unscaledTime });
                    SteamLog.Info(SteamLogCategory.Session, $"{m.personaName} 님이 게임 참가를 요청했습니다.");
                    changed = true;
                }
            }

            int removed = pendingRequests.RemoveAll(r =>
                !lobbyService.IsMember(r.steamId) || approved.Contains(r.steamId) || State != SessionState.InGame);
            if (changed || removed > 0) OnJoinRequestsChanged?.Invoke(pendingRequests);
        }

        private bool RemovePendingRequest(ulong steamId)
        {
            int removed = pendingRequests.RemoveAll(r => r.steamId == steamId);
            if (removed > 0) OnJoinRequestsChanged?.Invoke(pendingRequests);
            return removed > 0;
        }

        /// <summary>호스트: 로비를 다시 대기 상태로</summary>
        private void ResetLobbyToWaiting()
        {
            lobbyService.SetLobbyData(SessionKeys.State, SessionKeys.StateWaiting);
            lobbyService.SetLobbyData(SessionKeys.Roster, string.Empty);
            lobbyService.SetLobbyData(SessionKeys.Approved, string.Empty);
            lobbyService.SetLobbyData(SessionKeys.GameServer, string.Empty);
            bool canAcceptNew = !(HasPassword && hostPassword == null);
            lobbyService.SetLobbyData(SessionKeys.Joinable, canAcceptNew ? "1" : "0");
            lobbyService.SetLobbyJoinable(canAcceptNew);
        }

        private IEnumerator HostEndAfterDelay()
        {
            yield return new WaitForSecondsRealtime(hostEndDelay);
            endRoutine = null;
            FinishLocalGame(GameEndReason.HostEnded, true, "게임을 종료했습니다.");
        }

        // ---- 네트워크 이벤트 ----

        private void HandleNetClientConnected(ulong clientId)
        {
            if (networkManager.IsServer)
            {
                if (clientId != NetworkManager.ServerClientId && transport.TryGetSteamId(clientId, out ulong steamId))
                    SteamLog.Info(SteamLogCategory.Session, $"{lobbyService.GetPersonaName(steamId)} 님이 게임에 들어왔습니다.");
                return;
            }

            if (clientId == networkManager.LocalClientId && State == SessionState.Starting)
            {
                SteamLog.Info(SteamLogCategory.Session, "게임에 접속했습니다.");
                SetState(SessionState.InGame);
                OnGameStarted?.Invoke(false);
            }
        }

        private void HandleNetClientDisconnected(ulong clientId)
        {
            if (networkManager.IsServer)
            {
                if (clientId != NetworkManager.ServerClientId)
                    SteamLog.Info(SteamLogCategory.Session, $"플레이어(clientId={clientId})가 게임에서 나갔습니다.");
                return;
            }

            // 참가자: 호스트와의 연결이 끊김
            if (State != SessionState.Starting && State != SessionState.InGame) return;
            string reason = networkManager.DisconnectReason;
            bool wasStarting = State == SessionState.Starting;
            SetState(SessionState.Ending);
            if (networkManager.IsListening) networkManager.Shutdown();

            if (wasStarting && !string.IsNullOrEmpty(reason))
            {
                // 접속 단계에서 호스트가 거부함
                FinishLocalGame(GameEndReason.ConnectionFailed, true, "게임 접속이 거부되었습니다: " + reason);
                return;
            }
            endRoutine = StartCoroutine(DecideAfterDisconnect(reason));
        }

        private void HandleTransportFailure()
        {
            SteamLog.Error(SteamLogCategory.Network, "트랜스포트 오류가 발생했습니다.");
            if (IsHost && State == SessionState.InGame)
            {
                ResetLobbyToWaiting();
                FinishLocalGame(GameEndReason.ConnectionFailed, true, "네트워크 오류로 게임이 종료되었습니다.");
            }
            else if (State == SessionState.Starting || State == SessionState.InGame)
            {
                FinishLocalGame(GameEndReason.ConnectionFailed, false, "네트워크 오류로 게임이 종료되었습니다.");
            }
        }

        /// <summary>참가자: 연결이 끊긴 뒤 호스트가 게임을 끝낸 것인지, 떠난 것인지 판단</summary>
        private IEnumerator DecideAfterDisconnect(string reason)
        {
            yield return new WaitForSecondsRealtime(disconnectCheckDelay);
            endRoutine = null;

            bool hostStillHere = lobbyService.InLobby && lobbyService.IsMember(connectedHostSteamId);
            bool lobbyWaiting = GetData(SessionKeys.State) == SessionKeys.StateWaiting;

            if (hostStillHere && lobbyWaiting)
                FinishLocalGame(GameEndReason.HostEnded, true, "호스트가 게임을 종료했습니다.");
            else if (!hostStillHere)
                FinishLocalGame(GameEndReason.HostLeft, false, "호스트가 게임을 떠나 게임이 종료되었습니다.");
            else
                FinishLocalGame(GameEndReason.ConnectionFailed, false,
                    string.IsNullOrEmpty(reason) ? "호스트와의 연결이 끊겼습니다." : "호스트와의 연결이 끊겼습니다: " + reason);
        }

        /// <summary>
        /// 내 쪽 게임 정리. stayInLobby 면 로비에 남아 다음 게임을 기다리고, 아니면 로비에서도 나간다.
        /// 게임 씬에 있었다면 복귀 씬을 불러온다.
        /// </summary>
        private void FinishLocalGame(GameEndReason reason, bool stayInLobby, string message)
        {
            if (endRoutine != null) { StopCoroutine(endRoutine); endRoutine = null; }
            SetState(SessionState.Ending);

            bool wasHost = IsHost;
            if (networkManager.IsListening) networkManager.Shutdown();
            ClearGameState();

            if (stayInLobby && lobbyService.InLobby)
            {
                lobbyService.SetReady(false);
                lobbyService.SetMemberData(SessionKeys.JoinRequest, string.Empty);
                SetState(SessionState.InLobby);
            }
            else
            {
                if (wasHost && lobbyService.InLobby) ResetLobbyToWaiting();
                lobbyService.LeaveLobby();
                SetState(SteamManager.Initialized ? SessionState.Idle : SessionState.Offline);
            }

            SteamLog.Info(SteamLogCategory.Session, $"게임 종료: {message}");
            LoadReturnScene();
            OnGameEnded?.Invoke(reason, message);
        }

        private void LoadReturnScene()
        {
            string target = activeReturnScene;
            activeReturnScene = null;
            if (string.IsNullOrEmpty(target)) return;
            if (SceneManager.GetActiveScene().name == target) return;
            SteamLog.Info(SteamLogCategory.Session, $"'{target}' 씬으로 돌아갑니다.");
            SceneManager.LoadScene(target);
        }

        private void ClearGameState()
        {
            roster.Clear();
            approved.Clear();
            connectedHostSteamId = 0;
            transport.ConnectToSteamId = 0;
            if (pendingRequests.Count > 0)
            {
                pendingRequests.Clear();
                OnJoinRequestsChanged?.Invoke(pendingRequests);
            }
        }

        private void ClearLobbyState()
        {
            ClearGameState();
            passwordVerified.Clear();
            passwordDeadlines.Clear();
            PasswordRequired = false;
            hostPassword = null;
            ActivePreset = null;
            activeGameScene = null;
        }

        private bool TryFindClientId(ulong steamId, out ulong clientId)
        {
            foreach (ulong id in networkManager.ConnectedClientsIds)
            {
                if (transport.TryGetSteamId(id, out ulong sid) && sid == steamId)
                {
                    clientId = id;
                    return true;
                }
            }
            clientId = 0;
            return false;
        }

        private string GetData(string key) => lobbyService != null ? lobbyService.GetLobbyData(key) : string.Empty;

        private bool CheckReady(string op)
        {
            if (lobbyService != null && lobbyService.IsAvailable) return true;
            RaiseError($"{op} 실패: Steam 에 연결되지 않았습니다. ({SteamManager.LastInitError})");
            return false;
        }

        private void SetState(SessionState next)
        {
            if (State == next) return;
            SteamLog.Verbose(SteamLogCategory.Session, $"상태 변경: {State} → {next}");
            State = next;
            OnStateChanged?.Invoke(next);
        }

        /// <summary>로비 서비스 오류는 이미 로그가 남았으므로 이벤트만 전달</summary>
        private void ForwardLobbyError(string message) => OnSessionError?.Invoke(message);

        private void RaiseError(string message)
        {
            SteamLog.Warn(SteamLogCategory.Session, message);
            OnSessionError?.Invoke(message);
        }
    }
}
