using System;
using System.Collections.Generic;
using UnityEngine;

namespace Magnetic.SteamLobby
{
    /// <summary>로그 중요도</summary>
    public enum SteamLogLevel
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>로그 분류. 콘솔/파일에서 분류별로 거를 수 있다.</summary>
    public enum SteamLogCategory
    {
        Steam,
        Lobby,
        Session,
        Network,
    }

    /// <summary>로그 한 줄</summary>
    public readonly struct SteamLogEntry
    {
        public readonly DateTime Time;
        public readonly SteamLogLevel Level;
        public readonly SteamLogCategory Category;
        public readonly string Message;

        public SteamLogEntry(DateTime time, SteamLogLevel level, SteamLogCategory category, string message)
        {
            Time = time;
            Level = level;
            Category = category;
            Message = message;
        }

        /// <summary>예: [14:03:22][정보][로비] 로비에 입장했습니다.</summary>
        public override string ToString() =>
            $"[{Time:HH:mm:ss}][{SteamLog.LevelLabel(Level)}][{SteamLog.CategoryLabel(Category)}] {Message}";
    }

    /// <summary>
    /// 패키지 전체의 로그 창구. 모든 메시지는 한국어로 기록된다.
    /// - Unity 콘솔 출력 (콘솔 검색창에 "[스팀로비]" 로 필터)
    /// - 최근 로그 링버퍼 (<see cref="Recent"/>)
    /// - 구독 이벤트 (<see cref="OnLogged"/>): 화면 콘솔, 파일 저장 등은 이 이벤트를 받아 구현한다 (Samples~/KoreanLog 참고)
    /// </summary>
    public static class SteamLog
    {
        public const string Prefix = "[스팀로비]";

        /// <summary>이 수준 미만의 로그는 Unity 콘솔에 출력하지 않는다 (링버퍼·이벤트에는 항상 기록).</summary>
        public static SteamLogLevel ConsoleLevel = SteamLogLevel.Info;

        /// <summary>false 면 Unity 콘솔 출력을 끈다.</summary>
        public static bool WriteToUnityConsole = true;

        /// <summary>링버퍼 크기</summary>
        public static int Capacity = 256;

        private static readonly LinkedList<SteamLogEntry> buffer = new LinkedList<SteamLogEntry>();

        public static IEnumerable<SteamLogEntry> Recent => buffer;

        /// <summary>새 로그가 기록될 때마다 호출 (메인 스레드)</summary>
        public static event Action<SteamLogEntry> OnLogged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Enter Play Mode Options(도메인 리로드 끔) 대응
            buffer.Clear();
            OnLogged = null;
        }

        public static void Verbose(SteamLogCategory category, string message) => Write(SteamLogLevel.Verbose, category, message);
        public static void Info(SteamLogCategory category, string message) => Write(SteamLogLevel.Info, category, message);
        public static void Warn(SteamLogCategory category, string message) => Write(SteamLogLevel.Warning, category, message);
        public static void Error(SteamLogCategory category, string message) => Write(SteamLogLevel.Error, category, message);

        public static void Write(SteamLogLevel level, SteamLogCategory category, string message)
        {
            var entry = new SteamLogEntry(DateTime.Now, level, category, message);

            buffer.AddLast(entry);
            while (buffer.Count > Mathf.Max(1, Capacity)) buffer.RemoveFirst();

            if (WriteToUnityConsole && level >= ConsoleLevel)
            {
                string line = $"{Prefix}[{CategoryLabel(category)}] {message}";
                switch (level)
                {
                    case SteamLogLevel.Error: Debug.LogError(line); break;
                    case SteamLogLevel.Warning: Debug.LogWarning(line); break;
                    default: Debug.Log(line); break;
                }
            }

            OnLogged?.Invoke(entry);
        }

        public static void Clear() => buffer.Clear();

        public static string LevelLabel(SteamLogLevel level)
        {
            switch (level)
            {
                case SteamLogLevel.Verbose: return "상세";
                case SteamLogLevel.Info: return "정보";
                case SteamLogLevel.Warning: return "경고";
                case SteamLogLevel.Error: return "오류";
                default: return level.ToString();
            }
        }

        public static string CategoryLabel(SteamLogCategory category)
        {
            switch (category)
            {
                case SteamLogCategory.Steam: return "스팀";
                case SteamLogCategory.Lobby: return "로비";
                case SteamLogCategory.Session: return "세션";
                case SteamLogCategory.Network: return "네트워크";
                default: return category.ToString();
            }
        }
    }
}
