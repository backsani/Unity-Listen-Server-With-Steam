using UnityEngine;

namespace Magnetic.SteamLobby
{
    /// <summary>
    /// 세션(한 판) 규칙 프리셋. 예: "협동 2~4인", "PvP 8인".
    /// Project 창 우클릭 > Create > Magnetic > Steam Lobby > Session Preset 으로 만들고
    /// <see cref="ServerManager"/> 의 기본 프리셋에 넣거나 <see cref="ServerManager.CreateSession"/> 에 전달한다.
    /// </summary>
    [CreateAssetMenu(fileName = "SessionPreset", menuName = "Magnetic/Steam Lobby/Session Preset", order = 0)]
    public class SessionPreset : ScriptableObject
    {
        [Tooltip("화면 표시용 이름")]
        public string displayName = "기본 2~4인";

        [Tooltip("게임 모드 식별자. 로비에 기록되며, 같은 모드끼리만 검색하는 필터로 쓸 수 있다. 비우면 모드 구분 없음.")]
        public string modeId = "default";

        [Header("인원")]
        [Tooltip("게임을 시작할 수 있는 최소 인원 (호스트 포함)")]
        [Min(1)] public int minPlayers = 2;

        [Tooltip("로비 최대 인원 (호스트 포함). Steam 로비 인원 제한으로도 사용된다.")]
        [Range(1, 250)] public int maxPlayers = 4;

        [Header("규칙")]
        [Tooltip("로비 공개 범위")]
        public LobbyVisibility visibility = LobbyVisibility.Public;

        [Tooltip("true 면 모든 멤버가 준비 완료해야 호스트가 시작할 수 있다.")]
        public bool requireAllReady = true;

        [Tooltip("true 면 게임 시작 후 들어온 플레이어가 대기열에서 기다리다가 호스트 승인 시 진행 중인 게임에 참가한다.")]
        public bool allowLateJoin = true;

        [Header("씬")]
        [Tooltip("게임 시작 시 호스트가 NetworkSceneManager 로 불러올 씬 (Build Profiles 에 등록 필요). 비우면 씬을 바꾸지 않는다.")]
        public string gameSceneName = "";

        public string DisplayNameOrDefault => string.IsNullOrWhiteSpace(displayName) ? name : displayName;

        private void OnValidate()
        {
            if (maxPlayers < 1) maxPlayers = 1;
            if (minPlayers < 1) minPlayers = 1;
            if (minPlayers > maxPlayers) minPlayers = maxPlayers;
        }
    }
}
