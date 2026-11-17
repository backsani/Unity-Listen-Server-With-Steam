#if STEAMWORKS_NET && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX) && !DISABLESTEAMWORKS
#define STEAM_ENABLED
#endif

using System;
using System.Collections.Generic;
using UnityEngine;
#if STEAM_ENABLED
using System.Text;
using Steamworks;
#endif

namespace Magnetic.SteamLobby
{
    /// <summary>
    /// Steam Matchmaking 로비 래퍼 (게임 규칙과 무관한 범용 계층).
    /// 로비 생성/검색/참가/퇴장, 멤버 목록, 로비·멤버 메타데이터, 채팅, 시스템 메시지, 초대를 담당한다.
    /// 외부에는 Steamworks 타입을 노출하지 않고 ulong / <see cref="LobbySummary"/> / <see cref="LobbyMember"/> 만 노출한다.
    ///
    /// 게임 시작·인원 규칙·난입 승인 같은 세션 규칙은 <see cref="ServerManager"/> 가 이 클래스 위에서 처리한다.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Magnetic/Steam Lobby/Steam Lobby Service")]
    public class SteamLobbyService : MonoBehaviour
    {
        [Tooltip("공용 AppID(480 등)에서 우리 게임 로비만 검색되도록 로비에 기록하는 게임 식별자")]
        [SerializeField] private string gameId = "MyGame";
        [Tooltip("다른 버전 클라이언트끼리 매칭되지 않도록 로비 검색 필터와 입장 검사에 사용")]
        [SerializeField] private string gameVersion = "0.1.0";
        [SerializeField] private int lobbyListMaxResults = 50;

        public static SteamLobbyService Instance { get; private set; }

        /// <summary>채팅 메시지 중 이 접두사로 시작하는 것은 시스템 메시지로 처리 (화면 채팅에 표시 안 함)</summary>
        private const string SystemPrefix = "\u0001mslb:";

        // ---- 상태 ----
        public bool IsAvailable => SteamManager.Initialized && callbacksRegistered;
        public bool InLobby => CurrentLobbyId != 0;
        public ulong CurrentLobbyId { get; private set; }
        public ulong OwnerSteamId { get; private set; }
        public bool IsHost => InLobby && OwnerSteamId != 0 && OwnerSteamId == LocalSteamId;
        public bool IsBusy { get; private set; }
        public string GameId => gameId;
        public string GameVersion => gameVersion;
        public static ulong LocalSteamId => SteamManager.Instance != null ? SteamManager.Instance.LocalSteamId : 0;

        /// <summary>현재 로비의 최대 인원 (로비 밖이면 0)</summary>
        public int MemberLimit
        {
            get
            {
#if STEAM_ENABLED
                if (InLobby && SteamManager.Initialized)
                    return SteamMatchmaking.GetLobbyMemberLimit(new CSteamID(CurrentLobbyId));
#endif
                return 0;
            }
        }

        private readonly List<LobbySummary> lobbyList = new List<LobbySummary>();
        private readonly List<LobbyMember> members = new List<LobbyMember>();
        public IReadOnlyList<LobbySummary> LobbyList => lobbyList;
        public IReadOnlyList<LobbyMember> Members => members;

        // ---- 이벤트 ----
        public event Action<ulong> OnLobbyEntered;
        public event Action OnLobbyLeft;
        /// <summary>콜백 없이 요청한 로비 검색의 결과</summary>
        public event Action<IReadOnlyList<LobbySummary>> OnLobbyListUpdated;
        public event Action<IReadOnlyList<LobbyMember>> OnMembersChanged;
        public event Action<ulong> OnMemberJoined;
        public event Action<ulong> OnMemberLeft;
        /// <summary>로비 메타데이터 변경</summary>
        public event Action OnLobbyDataChanged;
        /// <summary>특정 멤버의 멤버 데이터 변경 (인자: steamId)</summary>
        public event Action<ulong> OnMemberDataChanged;
        /// <summary>로비 소유자 변경 (인자: 새 소유자 steamId)</summary>
        public event Action<ulong> OnOwnerChanged;
        public event Action<LobbyMember, string> OnChatMessage;
        /// <summary>시스템 메시지 수신 (보낸 사람 steamId, 명령, 인자)</summary>
        public event Action<ulong, string, string> OnSystemMessage;
        public event Action<string> OnError;

        private bool callbacksRegistered;
        private Action<IReadOnlyList<LobbySummary>> pendingListCallback;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake()
        {
            if (Instance != null && Instance != this) return; // 중복 오브젝트는 SteamManager 가 제거
            Instance = this;
        }

        private void Start()
        {
            if (Instance != this) return;

            if (!SteamManager.Initialized)
            {
                SteamLog.Warn(SteamLogCategory.Lobby, "Steam 이 초기화되지 않아 로비 기능을 끕니다. 원인: " + SteamManager.LastInitError);
                return;
            }
#if STEAM_ENABLED
            RegisterCallbacks();
            HandleCommandLineJoin();
#endif
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (InLobby) LeaveLobby();
            Instance = null;
#if STEAM_ENABLED
            UnregisterCallbacks();
#endif
        }

        /// <summary>SteamManager 가 SteamAPI.Shutdown() 직전에 호출. 로비 퇴장 + 콜백 해제.</summary>
        internal void HandleSteamShutdown()
        {
            if (InLobby) LeaveLobby();
#if STEAM_ENABLED
            UnregisterCallbacks();
#endif
        }

        internal void RaiseError(string msg)
        {
            IsBusy = false;
            SteamLog.Error(SteamLogCategory.Lobby, msg);
            OnError?.Invoke(msg);
        }

#if STEAM_ENABLED
        // =====================================================================
        // Callbacks
        // =====================================================================
        private CallResult<LobbyCreated_t> lobbyCreatedResult;
        private CallResult<LobbyMatchList_t> lobbyMatchListResult;
        private Callback<LobbyEnter_t> lobbyEnterCb;
        private Callback<GameLobbyJoinRequested_t> joinRequestedCb;
        private Callback<GameRichPresenceJoinRequested_t> richPresenceJoinCb;
        private Callback<LobbyChatUpdate_t> chatUpdateCb;
        private Callback<LobbyDataUpdate_t> dataUpdateCb;
        private Callback<LobbyChatMsg_t> chatMsgCb;
        private Callback<PersonaStateChange_t> personaChangeCb;

        private readonly byte[] chatBuffer = new byte[4096];

        private void RegisterCallbacks()
        {
            lobbyCreatedResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreatedResult);
            lobbyMatchListResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchListResult);
            lobbyEnterCb = Callback<LobbyEnter_t>.Create(OnLobbyEnterCb);
            joinRequestedCb = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            richPresenceJoinCb = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
            chatUpdateCb = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            dataUpdateCb = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
            chatMsgCb = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMsg);
            personaChangeCb = Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);
            callbacksRegistered = true;
            SteamLog.Verbose(SteamLogCategory.Lobby, "로비 콜백을 등록했습니다.");
        }

        private void UnregisterCallbacks()
        {
            if (!callbacksRegistered) return;
            lobbyCreatedResult?.Dispose();
            lobbyMatchListResult?.Dispose();
            lobbyEnterCb?.Dispose();
            joinRequestedCb?.Dispose();
            richPresenceJoinCb?.Dispose();
            chatUpdateCb?.Dispose();
            dataUpdateCb?.Dispose();
            chatMsgCb?.Dispose();
            personaChangeCb?.Dispose();
            callbacksRegistered = false;
        }

        /// <summary>게임이 꺼진 상태에서 친구 초대를 수락하면 "+connect_lobby &lt;id&gt;" 인자로 실행된다.</summary>
        private void HandleCommandLineJoin()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                {
                    SteamLog.Info(SteamLogCategory.Lobby, $"실행 인자로 로비 {id} 에 참가합니다.");
                    JoinLobby(id);
                    return;
                }
            }
        }
#endif

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// 로비를 만든다. 결과는 <see cref="OnLobbyEntered"/> 또는 <see cref="OnError"/> 로 온다.
        /// </summary>
        /// <param name="lobbyName">비우면 "닉네임의 로비"</param>
        /// <param name="extraData">생성 직후 함께 기록할 로비 메타데이터</param>
        public void CreateLobby(string lobbyName, LobbyVisibility visibility, int maxMembers, IDictionary<string, string> extraData = null)
        {
            if (!CheckAvailable("로비 생성")) return;
            if (InLobby) LeaveLobby();
#if STEAM_ENABLED
            pendingLobbyName = string.IsNullOrWhiteSpace(lobbyName) ? $"{SteamManager.Instance.LocalPersonaName}의 로비" : lobbyName.Trim();
            pendingLobbyData = extraData != null ? new Dictionary<string, string>(extraData) : null;
            int max = Mathf.Clamp(maxMembers, 1, 250);
            IsBusy = true;
            SteamLog.Info(SteamLogCategory.Lobby, $"로비 생성 요청: 이름='{pendingLobbyName}', 공개범위={visibility}, 최대인원={max}");
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ToSteam(visibility), max);
            lobbyCreatedResult.Set(call);
#endif
        }

        /// <summary>
        /// 로비 목록을 요청한다. 게임 식별자·버전 필터는 항상 추가된다.
        /// </summary>
        /// <param name="stringFilters">추가 문자열 일치 필터 (키, 값)</param>
        /// <param name="onResult">지정하면 결과를 이 콜백으로만 전달하고 <see cref="OnLobbyListUpdated"/> 는 발생시키지 않는다.</param>
        public void RequestLobbyList(IEnumerable<KeyValuePair<string, string>> stringFilters = null,
                                     Action<IReadOnlyList<LobbySummary>> onResult = null)
        {
            if (!CheckAvailable("로비 검색")) { onResult?.Invoke(Array.Empty<LobbySummary>()); return; }
#if STEAM_ENABLED
            IsBusy = true;
            pendingListCallback = onResult;
            // 필터는 RequestLobbyList 직전에 매번 추가해야 한다 (호출 후 초기화됨)
            SteamMatchmaking.AddRequestLobbyListStringFilter(LobbyKeys.Game, gameId, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(LobbyKeys.Version, gameVersion, ELobbyComparison.k_ELobbyComparisonEqual);
            if (stringFilters != null)
            {
                foreach (var f in stringFilters)
                    SteamMatchmaking.AddRequestLobbyListStringFilter(f.Key, f.Value, ELobbyComparison.k_ELobbyComparisonEqual);
            }
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(lobbyListMaxResults);
            SteamLog.Verbose(SteamLogCategory.Lobby, $"로비 목록 요청 (게임={gameId}, 버전={gameVersion})");
            lobbyMatchListResult.Set(SteamMatchmaking.RequestLobbyList());
#endif
        }

        public void JoinLobby(ulong lobbyId)
        {
            if (!CheckAvailable("로비 참가")) return;
            if (lobbyId == 0) { RaiseError("로비 참가 실패: 잘못된 로비 ID(0) 입니다."); return; }
            if (lobbyId == CurrentLobbyId) { SteamLog.Info(SteamLogCategory.Lobby, "이미 해당 로비에 있습니다."); return; }
            if (InLobby) LeaveLobby();
#if STEAM_ENABLED
            IsBusy = true;
            SteamLog.Info(SteamLogCategory.Lobby, $"로비 {lobbyId} 참가 요청");
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)); // 결과는 LobbyEnter_t 콜백으로 수신
#endif
        }

        public void LeaveLobby()
        {
            if (!InLobby) return;
#if STEAM_ENABLED
            if (SteamManager.Initialized)
            {
                SteamLog.Info(SteamLogCategory.Lobby, $"로비 {CurrentLobbyId} 에서 나갑니다.");
                SteamMatchmaking.LeaveLobby(new CSteamID(CurrentLobbyId));
                SteamFriends.ClearRichPresence();
            }
#endif
            CurrentLobbyId = 0;
            OwnerSteamId = 0;
            IsBusy = false;
            members.Clear();
            OnLobbyLeft?.Invoke();
            OnMembersChanged?.Invoke(members);
        }

        public void SetReady(bool ready) => SetMemberData(LobbyKeys.Ready, ready ? "1" : "0");

        public bool IsLocalReady()
        {
            ulong me = LocalSteamId;
            foreach (var m in members) if (m.steamId == me) return m.isReady;
            return false;
        }

        public bool AreAllMembersReady()
        {
            if (members.Count == 0) return false;
            foreach (var m in members) if (!m.isReady) return false;
            return true;
        }

        public bool IsMember(ulong steamId)
        {
            foreach (var m in members) if (m.steamId == steamId) return true;
            return false;
        }

        public void SendChat(string message)
        {
            if (string.IsNullOrEmpty(message) || message.StartsWith(SystemPrefix)) return;
            SendRaw(message);
        }

        /// <summary>
        /// 로비 전원에게 시스템 메시지를 보낸다 (채팅창에 표시되지 않음).
        /// 수신 측은 <see cref="OnSystemMessage"/> 로 받으며, 보낸 사람이 로비 소유자인지 직접 확인해야 한다.
        /// </summary>
        public void SendSystemMessage(string command, string arg = "")
        {
            if (string.IsNullOrEmpty(command)) return;
            SendRaw($"{SystemPrefix}{command}|{arg}");
        }

        private void SendRaw(string message)
        {
            if (!InLobby || !SteamManager.Initialized) return;
#if STEAM_ENABLED
            byte[] bytes = Encoding.UTF8.GetBytes(message + "\0");
            if (!SteamMatchmaking.SendLobbyChatMsg(new CSteamID(CurrentLobbyId), bytes, bytes.Length))
                RaiseError("로비 메시지 전송에 실패했습니다.");
#endif
        }

        /// <summary>Steam 오버레이의 친구 초대 창을 연다. (오버레이가 꺼져 있으면 동작 안 함: 에디터 등)</summary>
        public void OpenInviteDialog()
        {
            if (!InLobby || !SteamManager.Initialized) return;
#if STEAM_ENABLED
            if (!SteamUtils.IsOverlayEnabled())
                SteamLog.Warn(SteamLogCategory.Lobby, "Steam 오버레이가 꺼져 있습니다 (에디터 실행 시 일반적). 초대 창이 보이지 않을 수 있습니다.");
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(CurrentLobbyId));
#endif
        }

        // ---- 메타데이터 ----

        /// <summary>호스트 전용. 로비 메타데이터 기록</summary>
        public bool SetLobbyData(string key, string value)
        {
            if (!InLobby || !SteamManager.Initialized) return false;
            if (!IsHost) { SteamLog.Warn(SteamLogCategory.Lobby, $"로비 데이터 '{key}' 는 호스트만 바꿀 수 있습니다."); return false; }
#if STEAM_ENABLED
            return SteamMatchmaking.SetLobbyData(new CSteamID(CurrentLobbyId), key, value ?? string.Empty);
#else
            return false;
#endif
        }

        public string GetLobbyData(string key)
        {
#if STEAM_ENABLED
            if (InLobby && SteamManager.Initialized)
                return SteamMatchmaking.GetLobbyData(new CSteamID(CurrentLobbyId), key);
#endif
            return string.Empty;
        }

        /// <summary>디버그용: 현재 로비의 모든 키/값</summary>
        public Dictionary<string, string> GetAllLobbyData()
        {
            var dict = new Dictionary<string, string>();
#if STEAM_ENABLED
            if (InLobby && SteamManager.Initialized) ReadAllLobbyData(new CSteamID(CurrentLobbyId), dict);
#endif
            return dict;
        }

        /// <summary>호스트 전용. 로비 참가 가능 여부 (false 면 누구도 새로 들어올 수 없음)</summary>
        public void SetLobbyJoinable(bool joinable)
        {
            if (!InLobby || !SteamManager.Initialized || !IsHost) return;
#if STEAM_ENABLED
            SteamMatchmaking.SetLobbyJoinable(new CSteamID(CurrentLobbyId), joinable);
#endif
        }

        /// <summary>내 멤버 데이터 기록 (로비 전원이 읽을 수 있음)</summary>
        public void SetMemberData(string key, string value)
        {
            if (!InLobby || !SteamManager.Initialized) return;
#if STEAM_ENABLED
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(CurrentLobbyId), key, value ?? string.Empty);
#endif
        }

        public string GetMemberData(ulong steamId, string key)
        {
#if STEAM_ENABLED
            if (InLobby && SteamManager.Initialized)
                return SteamMatchmaking.GetLobbyMemberData(new CSteamID(CurrentLobbyId), new CSteamID(steamId), key);
#endif
            return string.Empty;
        }

        public string GetPersonaName(ulong steamId)
        {
#if STEAM_ENABLED
            if (SteamManager.Initialized) return SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
#endif
            return steamId.ToString();
        }

        private bool CheckAvailable(string op)
        {
            if (IsAvailable) return true;
            RaiseError($"{op} 실패: Steam 로비를 사용할 수 없습니다. ({SteamManager.LastInitError})");
            return false;
        }

#if STEAM_ENABLED
        // =====================================================================
        // Handlers
        // =====================================================================
        private string pendingLobbyName;
        private Dictionary<string, string> pendingLobbyData;

        private static ELobbyType ToSteam(LobbyVisibility v)
        {
            switch (v)
            {
                case LobbyVisibility.Private: return ELobbyType.k_ELobbyTypePrivate;
                case LobbyVisibility.FriendsOnly: return ELobbyType.k_ELobbyTypeFriendsOnly;
                case LobbyVisibility.Invisible: return ELobbyType.k_ELobbyTypeInvisible;
                default: return ELobbyType.k_ELobbyTypePublic;
            }
        }

        private static void ReadAllLobbyData(CSteamID lobby, Dictionary<string, string> dict)
        {
            int count = SteamMatchmaking.GetLobbyDataCount(lobby);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyDataByIndex(lobby, i, out string key, Constants.k_nMaxLobbyKeyLength,
                        out string value, Constants.k_cubChatMetadataMax))
                    dict[key] = value;
            }
        }

        private void OnLobbyCreatedResult(LobbyCreated_t data, bool ioFailure)
        {
            if (ioFailure || data.m_eResult != EResult.k_EResultOK)
            {
                RaiseError($"로비 생성 실패: 결과={data.m_eResult}, IO 실패={ioFailure}");
                return;
            }

            var lobby = new CSteamID(data.m_ulSteamIDLobby);
            var sm = SteamManager.Instance;
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeys.Game, gameId);
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeys.Name, pendingLobbyName);
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeys.Version, gameVersion);
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeys.HostSteamId, sm.LocalSteamId.ToString());
            SteamMatchmaking.SetLobbyData(lobby, LobbyKeys.HostName, sm.LocalPersonaName);
            if (pendingLobbyData != null)
            {
                foreach (var kv in pendingLobbyData)
                    SteamMatchmaking.SetLobbyData(lobby, kv.Key, kv.Value ?? string.Empty);
            }
            pendingLobbyData = null;
            SteamLog.Info(SteamLogCategory.Lobby, $"로비를 만들었습니다: {data.m_ulSteamIDLobby}");
            // 입장 처리(CurrentLobbyId 설정 등)는 LobbyEnter_t 에서 공통으로 처리
        }

        private void OnLobbyMatchListResult(LobbyMatchList_t data, bool ioFailure)
        {
            IsBusy = false;
            var callback = pendingListCallback;
            pendingListCallback = null;

            var results = new List<LobbySummary>();
            if (ioFailure)
            {
                RaiseError("로비 목록 요청 중 네트워크 오류가 발생했습니다.");
            }
            else
            {
                for (int i = 0; i < data.m_nLobbiesMatching; i++)
                {
                    CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                    var summary = new LobbySummary
                    {
                        lobbyId = id.m_SteamID,
                        memberCount = SteamMatchmaking.GetNumLobbyMembers(id),
                        maxMembers = SteamMatchmaking.GetLobbyMemberLimit(id),
                    };
                    ReadAllLobbyData(id, summary.data);
                    results.Add(summary);
                }
                SteamLog.Verbose(SteamLogCategory.Lobby, $"로비 {results.Count}개를 찾았습니다.");
            }

            if (callback != null)
            {
                callback(results);
                return;
            }

            lobbyList.Clear();
            lobbyList.AddRange(results);
            OnLobbyListUpdated?.Invoke(lobbyList);
        }

        private void OnLobbyEnterCb(LobbyEnter_t data)
        {
            IsBusy = false;
            var response = (EChatRoomEnterResponse)data.m_EChatRoomEnterResponse;
            if (response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                RaiseError($"로비 입장 실패: {DescribeEnterResponse(response)} (로비={data.m_ulSteamIDLobby})");
                return;
            }

            var lobby = new CSteamID(data.m_ulSteamIDLobby);
            CurrentLobbyId = data.m_ulSteamIDLobby;
            OwnerSteamId = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;

            // 다른 게임/버전 로비 차단 (초대·ID 로 들어온 경우 검색 필터를 거치지 않음)
            if (!IsHost)
            {
                string lobbyGame = SteamMatchmaking.GetLobbyData(lobby, LobbyKeys.Game);
                if (lobbyGame != gameId)
                {
                    RaiseError($"다른 게임의 로비입니다 (게임={lobbyGame}). 로비에서 나갑니다.");
                    LeaveLobby();
                    return;
                }

                string lobbyVersion = SteamMatchmaking.GetLobbyData(lobby, LobbyKeys.Version);
                if (!string.IsNullOrEmpty(lobbyVersion) && lobbyVersion != gameVersion)
                {
                    RaiseError($"게임 버전이 다릅니다 (로비={lobbyVersion}, 내 버전={gameVersion}). 로비에서 나갑니다.");
                    LeaveLobby();
                    return;
                }
            }

            SteamMatchmaking.SetLobbyMemberData(lobby, LobbyKeys.Ready, "0");
            // 친구 목록의 "게임 참가" / 게임 미실행 상태에서 초대 수락 시 사용
            SteamFriends.SetRichPresence("connect", $"+connect_lobby {CurrentLobbyId}");

            SteamLog.Info(SteamLogCategory.Lobby, $"로비 {CurrentLobbyId} 에 입장했습니다. 소유자={OwnerSteamId}, 호스트 여부={IsHost}");
            RefreshMembers();
            OnLobbyEntered?.Invoke(CurrentLobbyId);
        }

        private static string DescribeEnterResponse(EChatRoomEnterResponse r)
        {
            switch (r)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist: return "로비가 존재하지 않습니다";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed: return "참가가 허용되지 않았습니다";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull: return "로비가 가득 찼습니다";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned: return "이 로비에서 차단되었습니다";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited: return "제한된 계정은 참가할 수 없습니다";
                default: return r.ToString();
            }
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            SteamLog.Info(SteamLogCategory.Lobby, $"친구 초대로 로비 참가: 로비={data.m_steamIDLobby.m_SteamID}, 친구={data.m_steamIDFriend.m_SteamID}");
            JoinLobby(data.m_steamIDLobby.m_SteamID);
        }

        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            SteamLog.Info(SteamLogCategory.Lobby, $"친구 목록에서 게임 참가: connect='{data.m_rgchConnect}'");
            string[] parts = (data.m_rgchConnect ?? string.Empty).Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong id))
                {
                    JoinLobby(id);
                    return;
                }
            }
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t data)
        {
            if (data.m_ulSteamIDLobby != CurrentLobbyId) return;
            var change = (EChatMemberStateChange)data.m_rgfChatMemberStateChange;
            ulong user = data.m_ulSteamIDUserChanged;
            bool entered = (change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            SteamLog.Info(SteamLogCategory.Lobby, $"{GetPersonaName(user)} 님이 로비에 {(entered ? "들어왔습니다" : "나갔습니다")}.");

            ulong prevOwner = OwnerSteamId;
            OwnerSteamId = SteamMatchmaking.GetLobbyOwner(new CSteamID(CurrentLobbyId)).m_SteamID;

            RefreshMembers();

            if (entered) OnMemberJoined?.Invoke(user);
            else OnMemberLeft?.Invoke(user);

            if (prevOwner != OwnerSteamId)
            {
                SteamLog.Warn(SteamLogCategory.Lobby, $"로비 소유자가 바뀌었습니다: {GetPersonaName(OwnerSteamId)} (내가 호스트={IsHost})");
                if (IsHost)
                {
                    // 새 호스트가 호스트 정보를 갱신 (로비 목록에 이전 호스트가 남지 않도록)
                    SetLobbyData(LobbyKeys.HostSteamId, LocalSteamId.ToString());
                    SetLobbyData(LobbyKeys.HostName, SteamManager.Instance.LocalPersonaName);
                }
                OnOwnerChanged?.Invoke(OwnerSteamId);
            }
        }

        private void OnLobbyDataUpdate(LobbyDataUpdate_t data)
        {
            if (data.m_ulSteamIDLobby != CurrentLobbyId) return; // 목록용 데이터 요청 결과 등은 무시
            if (data.m_bSuccess == 0)
            {
                SteamLog.Warn(SteamLogCategory.Lobby, $"로비 데이터 갱신 실패 (멤버={data.m_ulSteamIDMember})");
                return;
            }

            if (data.m_ulSteamIDMember == data.m_ulSteamIDLobby)
            {
                OnLobbyDataChanged?.Invoke();
            }
            else
            {
                RefreshMembers();
                OnMemberDataChanged?.Invoke(data.m_ulSteamIDMember);
            }
        }

        private void OnLobbyChatMsg(LobbyChatMsg_t data)
        {
            if (data.m_ulSteamIDLobby != CurrentLobbyId) return;
            int len = SteamMatchmaking.GetLobbyChatEntry(new CSteamID(CurrentLobbyId), (int)data.m_iChatID,
                out CSteamID sender, chatBuffer, chatBuffer.Length, out EChatEntryType type);
            if (len <= 0 || type != EChatEntryType.k_EChatEntryTypeChatMsg) return;

            string text = Encoding.UTF8.GetString(chatBuffer, 0, len).TrimEnd('\0');
            if (text.StartsWith(SystemPrefix))
            {
                string body = text.Substring(SystemPrefix.Length);
                int sep = body.IndexOf('|');
                string command = sep >= 0 ? body.Substring(0, sep) : body;
                string arg = sep >= 0 ? body.Substring(sep + 1) : string.Empty;
                SteamLog.Verbose(SteamLogCategory.Lobby, $"시스템 메시지 수신: {command}({arg}) 보낸 사람={sender.m_SteamID}");
                OnSystemMessage?.Invoke(sender.m_SteamID, command, arg);
                return;
            }

            LobbyMember who = FindMember(sender.m_SteamID);
            OnChatMessage?.Invoke(who, text);
        }

        private void OnPersonaStateChange(PersonaStateChange_t data)
        {
            // 멤버 닉네임이 늦게 도착하는 경우("[unknown]") 갱신
            if (!InLobby) return;
            foreach (var m in members)
            {
                if (m.steamId == data.m_ulSteamID) { RefreshMembers(); return; }
            }
        }

        private void RefreshMembers()
        {
            members.Clear();
            if (!InLobby) { OnMembersChanged?.Invoke(members); return; }

            var lobby = new CSteamID(CurrentLobbyId);
            ulong me = LocalSteamId;
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                members.Add(new LobbyMember
                {
                    steamId = id.m_SteamID,
                    personaName = SteamFriends.GetFriendPersonaName(id),
                    isReady = SteamMatchmaking.GetLobbyMemberData(lobby, id, LobbyKeys.Ready) == "1",
                    isOwner = id.m_SteamID == OwnerSteamId,
                    isLocal = id.m_SteamID == me,
                });
            }
            OnMembersChanged?.Invoke(members);
        }

        private LobbyMember FindMember(ulong steamId)
        {
            foreach (var m in members) if (m.steamId == steamId) return m;
            return new LobbyMember { steamId = steamId, personaName = SteamFriends.GetFriendPersonaName(new CSteamID(steamId)) };
        }
#endif
    }
}
