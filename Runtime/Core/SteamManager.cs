// Steamworks.NET 패키지가 설치되어 있고(STEAMWORKS_NET: asmdef versionDefines),
// 스탠드얼론(PC) 플랫폼일 때만 실제 Steam API를 사용한다.
#if STEAMWORKS_NET && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX) && !DISABLESTEAMWORKS
#define STEAM_ENABLED
#endif

using System;
using UnityEngine;
#if STEAM_ENABLED
using System.Text;
using Steamworks;
#endif

namespace Magnetic.SteamLobby
{
    /// <summary>
    /// Steam API 초기화 / 콜백 펌프 / 종료를 담당하는 단일 인스턴스.
    /// DontDestroyOnLoad 로 씬 전환 후에도 유지된다. 다른 Steam 기능은 반드시 <see cref="Initialized"/> 가 true 일 때만 호출할 것.
    /// 같은 오브젝트에 다른 서비스(로비, 서버 매니저, NetworkManager)를 함께 붙이는 것을 권장한다.
    /// 씬을 다시 로드해 중복 오브젝트가 생기면 새 오브젝트를 통째로 제거한다.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    [AddComponentMenu("Magnetic/Steam Lobby/Steam Manager")]
    public class SteamManager : MonoBehaviour
    {
        [Tooltip("개발 중에는 480(Spacewar). 출시 시 Steamworks 파트너 사이트에서 발급받은 AppID로 교체.")]
        [SerializeField] private uint appId = 480;

        [Tooltip("릴리즈 빌드에서만 true 권장. Steam 클라이언트로 실행되지 않았다면 Steam을 통해 재실행 후 종료한다.")]
        [SerializeField] private bool restartIfNotLaunchedBySteam = false;

        [Tooltip("이 수준 이상의 로그만 Unity 콘솔에 출력")]
        [SerializeField] private SteamLogLevel consoleLogLevel = SteamLogLevel.Info;

        public static SteamManager Instance { get; private set; }
        public static bool Initialized => Instance != null && Instance.initialized;
        public static string LastInitError { get; private set; } = string.Empty;

        /// <summary>초기화 성공/실패 시 1회 호출 (성공 여부)</summary>
        public static event Action<bool> OnInitializeFinished;

        public uint AppId => appId;
        public ulong LocalSteamId { get; private set; }
        public string LocalPersonaName { get; private set; } = string.Empty;

        private bool initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            LastInitError = string.Empty;
            OnInitializeFinished = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // 같은 씬에 재진입하면 중복 오브젝트가 생기므로 새 것을 제거
                Destroy(gameObject);
                return;
            }

            Instance = this;
            SteamLog.ConsoleLevel = consoleLogLevel;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }

#if STEAM_ENABLED
        private SteamAPIWarningMessageHook_t warningHook;

        [AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
        private static void SteamWarningHook(int severity, StringBuilder text)
        {
            SteamLog.Warn(SteamLogCategory.Steam, $"Steam API 경고 (심각도 {severity}): {text}");
        }

        private void Initialize()
        {
            SteamLog.Info(SteamLogCategory.Steam, $"Steam API 초기화를 시작합니다. appId={appId}, 플랫폼={Application.platform}, 에디터={Application.isEditor}");

            if (!Packsize.Test())
            {
                Fail("Packsize 검사 실패: 다른 플랫폼용 Steamworks.NET 바이너리를 사용 중입니다.");
                return;
            }

            if (!DllCheck.Test())
            {
                Fail("DLL 검사 실패: steam_api(64) DLL 버전이 Steamworks.NET 과 맞지 않습니다.");
                return;
            }

            try
            {
                if (restartIfNotLaunchedBySteam && !Application.isEditor &&
                    SteamAPI.RestartAppIfNecessary(new AppId_t(appId)))
                {
                    SteamLog.Info(SteamLogCategory.Steam, "Steam 클라이언트를 통해 다시 실행합니다. 현재 프로세스를 종료합니다.");
                    Application.Quit();
                    return;
                }
            }
            catch (DllNotFoundException e)
            {
                Fail($"steam_api DLL 을 찾을 수 없습니다: {e.Message}");
                return;
            }

            ESteamAPIInitResult result = SteamAPI.InitEx(out string errMsg);
            if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                Fail($"Steam API 초기화 실패: {result} / {errMsg}. " +
                     "Steam 클라이언트 실행·로그인 여부와 steam_appid.txt(프로젝트 루트 또는 실행 파일 옆)를 확인하세요.");
                return;
            }

            initialized = true;
            warningHook = SteamWarningHook;
            SteamClient.SetWarningMessageHook(warningHook);

            LocalSteamId = SteamUser.GetSteamID().m_SteamID;
            LocalPersonaName = SteamFriends.GetPersonaName();
            uint runningAppId = SteamUtils.GetAppID().m_AppId;

            SteamLog.Info(SteamLogCategory.Steam,
                $"Steam 초기화 완료. 사용자={LocalPersonaName} ({LocalSteamId}), 실행 AppID={runningAppId}, " +
                $"로그인={SteamUser.BLoggedOn()}, 국가={SteamUtils.GetIPCountry()}, 오버레이={SteamUtils.IsOverlayEnabled()}");

            if (runningAppId != appId)
                SteamLog.Warn(SteamLogCategory.Steam, $"설정된 appId({appId})와 실제 실행 AppID({runningAppId})가 다릅니다. steam_appid.txt 를 확인하세요.");

            OnInitializeFinished?.Invoke(true);
        }

        private void Update()
        {
            if (initialized) SteamAPI.RunCallbacks();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;

            if (initialized)
            {
                // OnDestroy 호출 순서는 보장되지 않으므로 Shutdown 전에 상위 서비스부터 직접 정리한다.
                // (네트워크 세션 → 로비 순서. Instance 를 비우기 전에 호출해야 실제 Steam 호출이 수행된다)
                if (ServerManager.Instance != null) ServerManager.Instance.HandleSteamShutdown();
                if (SteamLobbyService.Instance != null) SteamLobbyService.Instance.HandleSteamShutdown();
            }

            Instance = null;
            if (!initialized) return;
            initialized = false;
            SteamLog.Info(SteamLogCategory.Steam, "Steam API 를 종료합니다.");
            SteamAPI.Shutdown();
        }
#else
        private void Initialize()
        {
#if !STEAMWORKS_NET
            Fail("Steamworks.NET 패키지가 설치되지 않았습니다 (com.rlabrecque.steamworks.net). " +
                 "메뉴 [Tools > Magnetic Steam Lobby > Steamworks.NET 설치] 로 설치하세요. Steam 기능은 오프라인 모드로 동작합니다.");
#else
            Fail($"현재 플랫폼({Application.platform})은 Steamworks 를 지원하지 않습니다. 빌드 타겟을 Windows/Mac/Linux Standalone 으로 설정하세요.");
#endif
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
#endif

        private void Fail(string reason)
        {
            initialized = false;
            LastInitError = reason;
            SteamLog.Error(SteamLogCategory.Steam, reason);
            OnInitializeFinished?.Invoke(false);
        }
    }
}
