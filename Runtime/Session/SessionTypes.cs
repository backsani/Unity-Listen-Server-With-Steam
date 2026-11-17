using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Magnetic.SteamLobby
{
    /// <summary>서버 매니저 상태</summary>
    public enum SessionState
    {
        /// <summary>Steam 을 사용할 수 없음</summary>
        Offline,
        /// <summary>로비 밖</summary>
        Idle,
        /// <summary>로비에서 대기 중 (게임 시작 전)</summary>
        InLobby,
        /// <summary>진행 중인 게임에 난입 요청 후 호스트 승인을 기다리는 중</summary>
        WaitingApproval,
        /// <summary>리슨 서버 시작 / 호스트 접속 중</summary>
        Starting,
        /// <summary>게임 진행 중</summary>
        InGame,
        /// <summary>게임 종료 처리 중</summary>
        Ending,
    }

    /// <summary>게임이 끝난 이유</summary>
    public enum GameEndReason
    {
        /// <summary>호스트가 게임을 끝내고 로비로 돌아옴</summary>
        HostEnded,
        /// <summary>호스트가 나가거나 연결이 끊겨 게임이 종료됨</summary>
        HostLeft,
        /// <summary>내가 게임을 나감</summary>
        LocalLeft,
        /// <summary>호스트 접속 실패 또는 연결 끊김</summary>
        ConnectionFailed,
        /// <summary>호스트가 나를 내보냄</summary>
        Kicked,
    }

    /// <summary>로비를 만들 때의 추가 옵션. null 인 값은 프리셋 값을 따른다.</summary>
    [Serializable]
    public class SessionCreateOptions
    {
        /// <summary>비우면 "닉네임의 로비"</summary>
        public string lobbyName;
        /// <summary>비우면 비밀번호 없음</summary>
        public string password;
        /// <summary>true 면 로비 목록에 표시되지 않고 참가 코드/초대로만 들어올 수 있다.</summary>
        public bool unlisted;
        /// <summary>지정하면 프리셋의 공개 범위 대신 사용</summary>
        public LobbyVisibility? visibility;
        public int? minPlayers;
        public int? maxPlayers;
    }

    /// <summary>난입 승인 대기열 항목</summary>
    [Serializable]
    public struct JoinRequest
    {
        public ulong steamId;
        public string personaName;
        public float requestTime;
    }

    /// <summary>세션 계층이 사용하는 로비/멤버 메타데이터 키</summary>
    public static class SessionKeys
    {
        public const string Mode = "mode";
        public const string MinPlayers = "min";
        public const string RequireReady = "req_ready";
        public const string LateJoin = "late_join";
        /// <summary>"waiting" / "ingame"</summary>
        public const string State = "state";
        /// <summary>"1" 이면 검색 결과에 표시 (대기 중이거나, 진행 중이지만 난입 허용)</summary>
        public const string Joinable = "joinable";
        /// <summary>"1" 이면 로비 목록에 표시. "0" 이면 코드/초대로만 참가</summary>
        public const string Listed = "listed";
        /// <summary>참가 코드 (6자리)</summary>
        public const string Code = "code";
        /// <summary>비밀번호 해시용 솔트. 비어 있으면 비밀번호 없음</summary>
        public const string PasswordSalt = "pw_salt";
        /// <summary>리슨 서버를 연 호스트 SteamID</summary>
        public const string GameServer = "game_server";
        /// <summary>게임 시작 시점의 멤버 SteamID 목록 (쉼표 구분)</summary>
        public const string Roster = "roster";
        /// <summary>호스트가 난입을 승인한 SteamID 목록 (쉼표 구분)</summary>
        public const string Approved = "approved";

        public const string StateWaiting = "waiting";
        public const string StateInGame = "ingame";

        // member data
        /// <summary>"1" 이면 난입 요청 중</summary>
        public const string JoinRequest = "join_req";
        /// <summary>비밀번호 증명값 (SHA256(솔트 + 비밀번호 + 내 SteamID))</summary>
        public const string PasswordProof = "pw_proof";

        // system messages
        public const string CmdKick = "kick";
        public const string CmdReject = "reject";
        public const string CmdGameEnd = "game_end";
    }

    /// <summary>로비 목록 항목을 세션 관점으로 읽는 확장 메서드</summary>
    public static class LobbySummarySessionExtensions
    {
        public static string ModeId(this LobbySummary s) => s.GetData(SessionKeys.Mode);
        public static bool HasPassword(this LobbySummary s) => !string.IsNullOrEmpty(s.GetData(SessionKeys.PasswordSalt));
        public static bool IsInGame(this LobbySummary s) => s.GetData(SessionKeys.State) == SessionKeys.StateInGame;
        public static bool AllowsLateJoin(this LobbySummary s) => s.GetData(SessionKeys.LateJoin) == "1";
        public static int MinPlayers(this LobbySummary s) => int.TryParse(s.GetData(SessionKeys.MinPlayers), out int v) ? v : 1;
    }

    internal static class SessionUtil
    {
        private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 헷갈리는 0/O, 1/I 제외

        public static string GenerateCode(int length = 6)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(length);
            foreach (byte b in bytes) sb.Append(CodeChars[b % CodeChars.Length]);
            return sb.ToString();
        }

        public static string NormalizeCode(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();

        public static string GenerateSalt()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// 비밀번호 증명값. 멤버마다 SteamID 가 섞이므로 다른 멤버의 값을 복사해도 통과하지 못한다.
        /// 비밀번호 자체는 호스트 PC 에만 있고 로비 데이터에는 솔트만 공개된다.
        /// </summary>
        public static string PasswordProof(string salt, string password, ulong steamId)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{salt}:{password}:{steamId}"));
                return Convert.ToBase64String(hash);
            }
        }

        public static HashSet<ulong> ParseIdList(string csv)
        {
            var set = new HashSet<ulong>();
            if (string.IsNullOrEmpty(csv)) return set;
            foreach (string part in csv.Split(','))
                if (ulong.TryParse(part, out ulong id)) set.Add(id);
            return set;
        }

        public static string JoinIdList(IEnumerable<ulong> ids) => string.Join(",", ids);
    }
}
