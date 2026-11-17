using System;
using System.Collections.Generic;

// NOTE: 이 파일은 Steamworks 타입을 사용하지 않는다.
// 게임 코드(UI 등)는 이 타입들만 사용하므로 Steamworks.NET 이 없어도 컴파일된다.
namespace Magnetic.SteamLobby
{
    /// <summary>로비 공개 범위 (Steamworks ELobbyType 래핑)</summary>
    public enum LobbyVisibility
    {
        /// <summary>초대로만 참가. 검색 불가</summary>
        Private,
        /// <summary>친구만 검색 가능</summary>
        FriendsOnly,
        /// <summary>누구나 검색 가능</summary>
        Public,
        /// <summary>검색 불가, 친구 목록에도 표시 안 됨</summary>
        Invisible,
    }

    /// <summary>로비 목록에 표시할 요약 정보. 로비 메타데이터 전체를 담는다.</summary>
    [Serializable]
    public class LobbySummary
    {
        public ulong lobbyId;
        public int memberCount;
        public int maxMembers;
        public readonly Dictionary<string, string> data = new Dictionary<string, string>();

        public string Name => GetData(LobbyKeys.Name);
        public string HostName => GetData(LobbyKeys.HostName);
        public string Version => GetData(LobbyKeys.Version);
        public bool IsFull => maxMembers > 0 && memberCount >= maxMembers;

        public string GetData(string key) => data.TryGetValue(key, out string v) ? v : string.Empty;

        public override string ToString() => $"{Name} ({memberCount}/{maxMembers}) 호스트={HostName} v{Version} id={lobbyId}";
    }

    /// <summary>현재 로비에 있는 멤버 정보</summary>
    [Serializable]
    public struct LobbyMember
    {
        public ulong steamId;
        public string personaName;
        public bool isReady;
        public bool isOwner;
        public bool isLocal;
    }

    /// <summary>
    /// 로비 서비스가 직접 관리하는 기본 메타데이터 키.
    /// 세션(게임 진행) 관련 키는 <see cref="SessionKeys"/> 에 있다.
    /// </summary>
    public static class LobbyKeys
    {
        /// <summary>Spacewar(480) 같은 공용 AppID 에서 우리 게임 로비만 거르기 위한 키</summary>
        public const string Game = "mslb_game";
        public const string Name = "name";
        public const string Version = "version";
        public const string HostSteamId = "host";
        public const string HostName = "host_name";

        // member data
        public const string Ready = "ready";
    }
}
