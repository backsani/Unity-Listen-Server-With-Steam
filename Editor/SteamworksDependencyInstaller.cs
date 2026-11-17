using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Magnetic.SteamLobby.EditorTools
{
    /// <summary>
    /// git URL 로 설치한 패키지는 다른 git 패키지를 의존성으로 자동 설치하지 못한다.
    /// 그래서 Steamworks.NET 이 없으면 에디터 시작 시 한 번 안내하고, 메뉴에서 버튼 하나로 설치할 수 있게 한다.
    /// </summary>
    [InitializeOnLoad]
    public static class SteamworksDependencyInstaller
    {
        public const string SteamworksPackageName = "com.rlabrecque.steamworks.net";
        public const string SteamworksGitUrl = "https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2024.8.0";
        private const string MenuRoot = "Tools/Magnetic Steam Lobby/";
        private const string PromptedKey = "Magnetic.SteamLobby.SteamworksPrompted";

        private static AddRequest addRequest;

        static SteamworksDependencyInstaller()
        {
            EditorApplication.delayCall += CheckOnLoad;
        }

        public static bool IsSteamworksInstalled
        {
            get
            {
#if STEAMWORKS_NET
                return true;
#else
                return false;
#endif
            }
        }

        private static void CheckOnLoad()
        {
            if (IsSteamworksInstalled || Application.isBatchMode) return;
            if (UnityEditor.SessionState.GetBool(PromptedKey, false)) return;
            UnityEditor.SessionState.SetBool(PromptedKey, true);

            if (EditorUtility.DisplayDialog("Magnetic Steam Lobby",
                    "Steamworks.NET 패키지가 설치되어 있지 않습니다.\n" +
                    "Steam 로비와 리슨 서버 기능을 쓰려면 필요합니다. 지금 설치할까요?\n\n" + SteamworksGitUrl,
                    "설치", "나중에"))
            {
                InstallSteamworks();
            }
        }

        [MenuItem(MenuRoot + "Steamworks.NET 설치", false, 0)]
        public static void InstallSteamworks()
        {
            if (addRequest != null && !addRequest.IsCompleted)
            {
                Debug.Log("[스팀로비] Steamworks.NET 설치가 이미 진행 중입니다.");
                return;
            }
            Debug.Log("[스팀로비] Steamworks.NET 설치를 시작합니다: " + SteamworksGitUrl);
            addRequest = Client.Add(SteamworksGitUrl);
            EditorApplication.update += WatchAddRequest;
        }

        [MenuItem(MenuRoot + "Steamworks.NET 설치", true)]
        private static bool InstallSteamworksValidate() => !IsSteamworksInstalled;

        private static void WatchAddRequest()
        {
            if (addRequest == null || !addRequest.IsCompleted) return;
            EditorApplication.update -= WatchAddRequest;

            if (addRequest.Status == StatusCode.Success)
            {
                Debug.Log($"[스팀로비] Steamworks.NET {addRequest.Result.version} 설치 완료.");
                CreateAppIdFileIfMissing(false);
            }
            else
            {
                Debug.LogError("[스팀로비] Steamworks.NET 설치 실패: " + addRequest.Error?.message +
                               "\nPackage Manager 에서 'Install package from git URL' 로 직접 설치하세요: " + SteamworksGitUrl);
            }
            addRequest = null;
        }

        [MenuItem(MenuRoot + "steam_appid.txt 만들기 (480)", false, 1)]
        private static void CreateAppIdFileMenu() => CreateAppIdFileIfMissing(true);

        /// <summary>
        /// 에디터에서 실행할 때 Steam 은 프로젝트 루트의 steam_appid.txt 로 AppID 를 알아낸다.
        /// 빌드 시에는 실행 파일 옆에 같은 파일이 있어야 한다 (Steam 으로 실행하면 불필요).
        /// </summary>
        public static void CreateAppIdFileIfMissing(bool notify)
        {
            string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "steam_appid.txt");
            if (File.Exists(path))
            {
                if (notify) EditorUtility.DisplayDialog("Magnetic Steam Lobby", "steam_appid.txt 가 이미 있습니다.\n" + path, "확인");
                return;
            }
            File.WriteAllText(path, "480");
            Debug.Log("[스팀로비] steam_appid.txt(480, 테스트용 Spacewar) 를 만들었습니다: " + path);
            if (notify) EditorUtility.DisplayDialog("Magnetic Steam Lobby", "steam_appid.txt 를 만들었습니다 (AppID 480).\n출시 전 발급받은 AppID 로 바꾸세요.\n" + path, "확인");
        }
    }
}
