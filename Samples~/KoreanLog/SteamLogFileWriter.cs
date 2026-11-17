using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Magnetic.SteamLobby.Samples
{
    /// <summary>
    /// 한국어 스팀 로비 로그를 파일로 저장한다.
    /// 위치: Application.persistentDataPath/SteamLobbyLogs/steamlobby_yyyyMMdd_HHmmss.log
    /// (Windows 예: %USERPROFILE%\AppData\LocalLow\회사명\제품명\SteamLobbyLogs)
    /// UTF-8(BOM) 으로 저장해 메모장에서도 한글이 깨지지 않는다. 오래된 파일은 자동 삭제.
    /// </summary>
    [DisallowMultipleComponent]
    public class SteamLogFileWriter : MonoBehaviour
    {
        [SerializeField] private SteamLogLevel minimumLevel = SteamLogLevel.Verbose;
        [Tooltip("보관할 최대 로그 파일 수 (오래된 것부터 삭제)")]
        [SerializeField] private int keepFiles = 10;

        private static SteamLogFileWriter instance;
        private StreamWriter writer;

        public string CurrentFilePath { get; private set; }
        public static string LogDirectory => Path.Combine(Application.persistentDataPath, "SteamLobbyLogs");

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
            if (transform.parent == null) DontDestroyOnLoad(gameObject);
            Open();
        }

        private void Open()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                CleanupOldFiles();
                CurrentFilePath = Path.Combine(LogDirectory, $"steamlobby_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                writer = new StreamWriter(CurrentFilePath, false, new UTF8Encoding(true)) { AutoFlush = true };
                writer.WriteLine($"=== 스팀 로비 로그 시작: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, 버전 {Application.version}, 플랫폼 {Application.platform} ===");

                // 이 컴포넌트가 생기기 전에 쌓인 로그도 기록
                foreach (var e in SteamLog.Recent) Write(e);
                SteamLog.OnLogged += Write;
                SteamLog.Info(SteamLogCategory.Steam, "로그 파일: " + CurrentFilePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[스팀로비] 로그 파일을 열지 못했습니다: " + ex.Message);
                writer = null;
            }
        }

        private void Write(SteamLogEntry entry)
        {
            if (writer == null || entry.Level < minimumLevel) return;
            try { writer.WriteLine($"[{entry.Time:yyyy-MM-dd HH:mm:ss.fff}][{SteamLog.LevelLabel(entry.Level)}][{SteamLog.CategoryLabel(entry.Category)}] {entry.Message}"); }
            catch (Exception) { /* 디스크 오류 등은 무시 */ }
        }

        private void CleanupOldFiles()
        {
            var files = new DirectoryInfo(LogDirectory).GetFiles("steamlobby_*.log");
            if (files.Length < keepFiles) return;
            Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
            for (int i = 0; i <= files.Length - keepFiles; i++)
            {
                try { files[i].Delete(); } catch (Exception) { /* 사용 중이면 건너뜀 */ }
            }
        }

        /// <summary>로그 폴더를 탐색기로 연다.</summary>
        [ContextMenu("로그 폴더 열기")]
        public void OpenLogFolder() => Application.OpenURL("file://" + LogDirectory);

        private void OnDestroy()
        {
            if (instance != this) return;
            instance = null;
            SteamLog.OnLogged -= Write;
            if (writer != null)
            {
                writer.WriteLine($"=== 로그 종료: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                writer.Dispose();
                writer = null;
            }
        }
    }
}
