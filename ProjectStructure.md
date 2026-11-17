# Magnetic Steam Lobby — 프로젝트 구조 문서

> **Unity에서 Steam 로비로 플레이어를 모으고, 게임 시작 시 호스트 PC의 리슨 서버로 연결해 주는 프레임워크**

이 문서는 패키지의 적용 방법, 사용법, 내부 구현 방식, 설계 이유를 한곳에 정리한 것이다.
빠른 소개는 `README.md`, 변경 내역은 `CHANGELOG.md` 를 본다.

- 패키지 ID: `com.magnetic.steamlobby`
- 버전: 0.1.0 (기준일 2026-11-18)
- 대상: Unity 6000.0 이상 (6000.3.24f1 에서 개발·컴파일 확인), Windows / macOS / Linux Standalone
- 의존성: Netcode for GameObjects 2.13.3 (자동 설치), Steamworks.NET 2024.8.0 (별도 설치)

---

## 목차

1. [한눈에 보기](#1-한눈에-보기)
2. [범위 — 해 주는 것과 해 주지 않는 것](#2-범위--해-주는-것과-해-주지-않는-것)
3. [폴더 · 어셈블리 · 네임스페이스](#3-폴더--어셈블리--네임스페이스)
4. [패키지 적용](#4-패키지-적용)
5. [씬 구성](#5-씬-구성)
6. [사용법 (API)](#6-사용법-api)
7. [전체 동작 흐름](#7-전체-동작-흐름)
8. [구현 상세 — 계층별](#8-구현-상세--계층별)
9. [로비 메타데이터 규약](#9-로비-메타데이터-규약)
10. [핵심 규칙의 구현 방식](#10-핵심-규칙의-구현-방식)
11. [에디터 도구](#11-에디터-도구)
12. [샘플](#12-샘플)
13. [설계 원칙](#13-설계-원칙)
14. [테스트 방법](#14-테스트-방법)
15. [문제 해결](#15-문제-해결)
16. [알려진 한계와 확장 방향](#16-알려진-한계와-확장-방향)
17. [Magnetic Multi-Game 프로젝트 적용 기록](#17-magnetic-multi-game-프로젝트-적용-기록)

---

## 1. 한눈에 보기

멀티플레이 게임이 "같이 할 사람을 모으고 → 한 PC 를 서버로 삼아 → 모두 접속" 하기까지의 과정을 대신 처리한다.
연결이 끝난 뒤 무엇을 동기화할지는 게임이 Netcode for GameObjects(NGO)로 직접 작성한다.

```
 ┌──────────── Steam (Valve 서버) ────────────┐
 │  로비 = 대기실 (메타데이터 · 멤버 · 채팅)    │      ← 매치메이킹만 담당. 게임 데이터는 오가지 않음
 └──────▲───────────────────────▲─────────────┘
        │ 로비 생성/검색/참가     │
 ┌──────┴───────┐   Steam P2P   ┌┴─────────────┐
 │  호스트 PC   │◄─────────────►│  참가자 PC   │  ← 게임 데이터. NAT 통과는 Steam 릴레이(SDR)
 │ 리슨 서버    │  (SteamID로   │  클라이언트  │
 │ (StartHost)  │   접속)       │ (StartClient)│
 └──────────────┘               └──────────────┘
```

| 구성 요소 | 한 줄 설명 |
|---|---|
| `ServerManager` | 세션 규칙(인원·준비·시작·난입·비밀번호·종료)과 NGO 호스트/클라이언트 시작을 담당하는 중심 클래스 |
| `SessionPreset` | 최소/최대 인원 등 세션 규칙을 담은 ScriptableObject |
| `SteamLobbyService` | 게임 규칙과 무관한 범용 Steam 로비 래퍼 |
| `SteamNetworkingSocketsTransport` | NGO 용 Steam P2P 트랜스포트 (직접 구현) |
| `SteamManager` | Steam API 초기화·콜백 펌프·종료 |
| `SteamLog` | 한국어 로그 창구 |

---

## 2. 범위 — 해 주는 것과 해 주지 않는 것

| 프레임워크가 해 주는 것 | 해 주지 않는 것 (게임 쪽 책임) |
|---|---|
| Steam 로비 생성·검색·참가 (목록 / 참가 코드 / 로비 ID / 비밀번호 / 친구 초대) | 게임 로직, 캐릭터, 전투 |
| 최소/최대 인원·준비 확인 후 게임 시작 | 게임 오브젝트 동기화 (NetworkObject, NetworkVariable, RPC) |
| 호스트 PC 에서 리슨 서버 실행, 참가자 자동 접속 | 호스트 마이그레이션 (게임 중 호스트 교체) |
| 게임 중 난입 승인 대기열 | 저장, 계정·진행 데이터 |
| 게임 종료 후 로비 복귀 / 호스트 이탈 처리 | 플레이어 프리팹·스폰 위치 결정 (NGO 설정으로 지정) |
| 접속 승인(로비 멤버만 게임 서버 접속 허용) | 치트 방지 (리슨 서버 구조상 호스트는 신뢰됨) |
| 한국어 로그, 에디터 설치 도우미, 예제 UI | 최종 게임 UI 디자인 |

경계는 **"연결까지는 프레임워크, 연결 후 동기화는 게임"** 이다.

---

## 3. 폴더 · 어셈블리 · 네임스페이스

```
MagneticSteamLobby/
├─ package.json                         패키지 정의 (NGO 의존성, 샘플 목록)
├─ README.md / CHANGELOG.md / ProjectStructure.md
├─ Runtime/                             어셈블리 Magnetic.SteamLobby
│  ├─ Magnetic.SteamLobby.asmdef
│  ├─ Core/
│  │  ├─ SteamLog.cs                    한국어 로그 (수준·분류·링버퍼·이벤트)
│  │  └─ SteamManager.cs                Steam API 수명 관리
│  ├─ Lobby/
│  │  ├─ LobbyTypes.cs                  LobbyVisibility / LobbySummary / LobbyMember / LobbyKeys
│  │  └─ SteamLobbyService.cs           범용 로비 래퍼
│  ├─ Session/
│  │  ├─ SessionPreset.cs               세션 규칙 ScriptableObject
│  │  ├─ SessionTypes.cs                SessionState / GameEndReason / 옵션 / 키 / 유틸
│  │  └─ ServerManager.cs               세션 관리자 (핵심)
│  └─ Transport/
│     └─ SteamNetworkingSocketsTransport.cs   NGO NetworkTransport 구현
├─ Editor/                              어셈블리 Magnetic.SteamLobby.Editor (에디터 전용)
│  ├─ SteamworksDependencyInstaller.cs  Steamworks.NET 설치 안내·설치, steam_appid.txt 생성
│  └─ SteamSessionServicesMenu.cs       서비스 오브젝트 생성, 프리셋 생성 유틸
└─ Samples~/                            Package Manager 에서 가져오는 예제 (Unity 가 직접 임포트하지 않음)
   ├─ KoreanLog/                        어셈블리 Magnetic.SteamLobby.Samples.KoreanLog
   │  ├─ SteamLogConsole.cs             F1 게임 내 로그 창
   │  └─ SteamLogFileWriter.cs          로그 파일 저장
   └─ LobbyUI/                          어셈블리 Magnetic.SteamLobby.Samples.LobbyUI
      ├─ SessionLobbyController.cs      uGUI 로비 화면
      └─ Editor/                        어셈블리 Magnetic.SteamLobby.Samples.LobbyUI.Editor
         └─ LobbySceneBuilder.cs        로비 씬 자동 구성 도구
```

### 어셈블리 참조 관계

```
Magnetic.SteamLobby ──► com.rlabrecque.steamworks.net (없으면 무시됨)
        │          └──► Unity.Netcode.Runtime
        ▲
        ├── Magnetic.SteamLobby.Editor
        ├── Magnetic.SteamLobby.Samples.KoreanLog ──► Unity.InputSystem
        └── Magnetic.SteamLobby.Samples.LobbyUI ──► UnityEngine.UI, Unity.InputSystem
                     ▲
                     └── Magnetic.SteamLobby.Samples.LobbyUI.Editor ──► Magnetic.SteamLobby.Editor
```

### 네임스페이스

| 네임스페이스 | 내용 |
|---|---|
| `Magnetic.SteamLobby` | 런타임 전체 (게임 코드는 이것만 `using` 하면 됨) |
| `Magnetic.SteamLobby.EditorTools` | 패키지 에디터 도구 (`Editor` 라는 이름은 `UnityEditor.Editor` 와 충돌하므로 피함) |
| `Magnetic.SteamLobby.Samples` | 샘플 런타임 |
| `Magnetic.SteamLobby.Samples.EditorTools` | 샘플 에디터 도구 |

### 조건부 컴파일

Steam 을 쓰는 파일 맨 위에 공통으로 다음 조건이 있다.

```csharp
#if STEAMWORKS_NET && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX) && !DISABLESTEAMWORKS
#define STEAM_ENABLED
#endif
```

- `STEAMWORKS_NET` 은 asmdef 의 **versionDefines** 가 Steamworks.NET 패키지 설치 여부로 자동 정의한다.
- 조건을 만족하지 않으면(패키지 없음 / Android·WebGL 등) Steam 호출부가 통째로 빠지고, 모든 클래스가 "오프라인 모드"로 컴파일·동작한다. 그래서 **Steamworks.NET 이 없어도 프로젝트가 깨지지 않는다.**
- 게임 코드가 쓰는 공개 타입(`LobbySummary`, `LobbyMember`, `SessionState` 등)은 Steamworks 타입을 노출하지 않는다.

---

## 4. 패키지 적용

### 4-1. Steamworks.NET 설치

git URL 로 설치한 패키지는 `package.json` 에 다른 **git 패키지**를 의존성으로 적어도 자동 설치되지 않는다 (Unity 레지스트리 패키지인 NGO 는 자동 설치됨). 그래서 Steamworks.NET 은 따로 설치한다.

- 이 패키지를 먼저 설치하면 에디터 시작 시 "Steamworks.NET 을 설치할까요?" 대화상자가 한 번 뜬다.
- 또는 메뉴 **Tools > Magnetic Steam Lobby > Steamworks.NET 설치**
- 또는 Package Manager > `+` > *Install package from git URL*:

```
https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2024.8.0
```

### 4-2. 이 패키지 설치

| 방법 | `Packages/manifest.json` 항목 | 용도 |
|---|---|---|
| git URL | `"com.magnetic.steamlobby": "https://github.com/<계정>/MagneticSteamLobby.git"` | 다른 프로젝트에서 사용 (읽기 전용) |
| git URL + 버전 | `"...MagneticSteamLobby.git#v0.1.0"` | 특정 태그로 고정 |
| 로컬 폴더 | `"com.magnetic.steamlobby": "file:../../MagneticSteamLobby"` | 패키지 개발 중 (수정 즉시 반영) |

`file:` 경로는 `Packages` 폴더 기준 상대 경로다.

### 4-3. steam_appid.txt

에디터에서 실행할 때 Steam 은 **프로젝트 루트(Assets 옆)의 `steam_appid.txt`** 로 AppID 를 알아낸다.

- 메뉴 **Tools > Magnetic Steam Lobby > steam_appid.txt 만들기 (480)**
- 480 은 Valve 의 공용 테스트 AppID(Spacewar). 출시 전 발급받은 AppID 로 바꾸고 `SteamManager` 의 `App Id` 도 같이 바꾼다.
- 빌드에서는 실행 파일 옆에 같은 파일이 필요하다 (Steam 클라이언트로 실행하면 불필요).

### 4-4. 빌드 대상

**File > Build Profiles** 에서 Windows / macOS / Linux 중 하나를 선택한다. 그 외 플랫폼에서는 오프라인 모드가 된다.

---

## 5. 씬 구성

### 5-1. 서비스 오브젝트

Hierarchy 우클릭 > **Magnetic > Steam Session Services** 를 누르면 `SteamSessionServices` 오브젝트가 생기고 아래 컴포넌트가 붙는다.

| 컴포넌트 | 주요 설정 |
|---|---|
| `SteamManager` | `App Id` (기본 480), `Restart If Not Launched By Steam` (릴리즈에서만 권장), `Console Log Level` |
| `SteamLobbyService` | `Game Id` (게임 고유 이름, 기본 Product Name), `Game Version` (기본 Application.version), `Lobby List Max Results` |
| `SteamNetworkingSocketsTransport` | `Virtual Port` (호스트·클라이언트 동일해야 함), `Max Messages Per Receive` |
| `NetworkManager` (NGO) | `Network Transport` = 위 트랜스포트, `Connection Approval` = 켬, 플레이어 프리팹·네트워크 프리팹은 게임에서 지정 |
| `ServerManager` | `Default Preset`, `Filter Search By Mode`, `Return Scene Name`, 각종 제한 시간 |

**모든 서비스를 한 오브젝트에 둔다.** 이유:
- `SteamManager` 가 `DontDestroyOnLoad` 와 중복 제거를 담당하므로, 같은 오브젝트의 다른 서비스도 함께 유지·제거된다.
- 씬을 다시 불러와 서비스 오브젝트가 하나 더 생기면 `SteamManager.Awake` 가 새 오브젝트를 통째로 `Destroy` 한다. 다른 서비스들은 `Instance` 가 이미 있으면 아무것도 하지 않는다.

### 5-2. 세션 프리셋

Project 창 우클릭 > **Create > Magnetic > Steam Lobby > Session Preset**

| 필드 | 기본값 | 의미 |
|---|---|---|
| `displayName` | 기본 2~4인 | 화면 표시용 이름 |
| `modeId` | default | 로비에 기록되는 모드 식별자. 같은 모드끼리만 검색하는 필터로 사용 |
| `minPlayers` | 2 | 게임을 시작할 수 있는 최소 인원 (호스트 포함) |
| `maxPlayers` | 4 | 로비 최대 인원 = Steam 로비 인원 제한 (1~250) |
| `visibility` | Public | Private / FriendsOnly / Public / Invisible |
| `requireAllReady` | true | 호스트를 제외한 전원이 준비해야 시작 가능 |
| `allowLateJoin` | true | 게임 중 들어온 플레이어를 승인 대기열에 받음 |
| `gameSceneName` | (비움) | 시작 시 호스트가 NetworkSceneManager 로 불러올 씬. 비우면 씬 이동 없음 |

`OnValidate` 가 `1 ≤ minPlayers ≤ maxPlayers` 를 보장한다.
프리셋을 여러 개 만들어 모드별(협동/PvP 등)로 고르게 할 수 있다.

### 5-3. 로비 UI

- 직접 만들기: 6장의 API 로 UI 를 작성한다.
- 샘플 사용: Package Manager 에서 *Lobby UI* 샘플을 가져온 뒤 **Tools > Magnetic Steam Lobby > Build Lobby Scene (현재 씬)** → 12장 참고.

---

## 6. 사용법 (API)

게임 코드는 `ServerManager.Instance` 하나만 알면 된다. 로비 세부 기능이 필요하면 `ServerManager.Instance.Lobby` (`SteamLobbyService`) 를 쓴다.

```csharp
using Magnetic.SteamLobby;
```

### 6-1. 호스트

```csharp
var sm = ServerManager.Instance;

// 로비 만들기 (프리셋 생략 시 Default Preset)
sm.CreateSession(coopPreset, new SessionCreateOptions
{
    lobbyName = "같이 하실 분",   // 비우면 "닉네임의 로비"
    password  = "1234",          // 비우면 비밀번호 없음
    unlisted  = false,           // true 면 목록에 안 보이고 코드·초대로만 참가
    visibility = null,           // 지정하면 프리셋 공개 범위 대신 사용
    minPlayers = null,           // 지정하면 프리셋 값 대신 사용
    maxPlayers = null,
});

string code = sm.JoinCode;           // 6자리 참가 코드 (친구에게 공유)
sm.OpenInviteDialog();               // Steam 오버레이 초대 창

string why = sm.GetStartBlockReason(); // 시작 못 하는 이유 (한국어), 가능하면 null
if (sm.CanStartGame) sm.StartGame();   // 리슨 서버 시작

// 게임 중
sm.ApproveJoin(steamId);  // 난입 승인
sm.RejectJoin(steamId);   // 난입 거절
sm.Kick(steamId, "사유"); // 내보내기
sm.EndGame();             // 모두 로비로 복귀
```

### 6-2. 참가자

```csharp
sm.SearchSessions();                         // 결과는 OnSessionListUpdated
sm.OnSessionListUpdated += list =>
{
    foreach (LobbySummary s in list)
        Debug.Log($"{s.Name} {s.memberCount}/{s.maxMembers} 비밀번호={s.HasPassword()} 진행중={s.IsInGame()}");
};

sm.JoinSession(lobbyId, password);           // 목록 클릭 / 로비 ID
sm.JoinByCode("K7P2QX", password);           // 참가 코드
sm.OnPasswordRequired += () => ShowPasswordPopup();   // 초대로 들어와 비밀번호가 필요할 때
sm.SubmitPassword(input);

sm.ToggleReady();                            // 또는 SetReady(true/false)
sm.SendChat("안녕하세요");
sm.LeaveSession();
```

### 6-3. 공통 이벤트

| 이벤트 | 인자 | 시점 |
|---|---|---|
| `OnStateChanged` | `SessionState` | 상태 변경 (UI 전체 갱신에 사용) |
| `OnSessionError` | `string` | 사용자에게 보여줄 한국어 오류 |
| `OnSessionListUpdated` | `IReadOnlyList<LobbySummary>` | 검색 결과 |
| `OnPasswordRequired` | — | 비밀번호 입력 필요 |
| `OnJoinRequestsChanged` | `IReadOnlyList<JoinRequest>` | (호스트) 난입 대기열 변경 |
| `OnGameStarted` | `bool isHost` | 호스트: StartHost 성공 / 참가자: 접속 완료 |
| `OnGameEnded` | `GameEndReason, string` | 게임 종료 (이유, 설명) |

로비 세부 이벤트(`Lobby.OnMembersChanged`, `Lobby.OnChatMessage`, `Lobby.OnLobbyDataChanged` 등)는 `SteamLobbyService` 에 있다.

### 6-4. 읽기 전용 상태

| 속성 | 의미 |
|---|---|
| `State` | 현재 `SessionState` |
| `IsHost`, `InSession`, `IsGameActive` | 호스트 여부 / 로비 안 / 게임 진행 관련 상태 |
| `PlayerCount`, `MinPlayers`, `MaxPlayers` | 인원 (로비 데이터 기준이라 참가자도 동일한 값을 봄) |
| `RequireAllReady`, `AllowLateJoin`, `HasPassword`, `IsListed`, `JoinCode`, `ModeId` | 로비 규칙 |
| `PasswordRequired` | 참가자가 비밀번호를 아직 제출하지 않음 |
| `PendingJoinRequests` | (호스트) 난입 대기열 |
| `Sessions` | 마지막 검색 결과 |
| `ActivePreset` | (호스트) 로비를 만들 때 쓴 프리셋. 호스트를 넘겨받았으면 null |

### 6-5. 게임 씬에서

연결된 뒤에는 일반 NGO 코드를 쓴다.

```csharp
public class Player : NetworkBehaviour
{
    public override void OnNetworkSpawn()
    {
        // 이 플레이어의 SteamID (서버에서만)
        if (IsServer && ServerManager.Instance.TryGetSteamId(OwnerClientId, out ulong steamId))
            Debug.Log($"플레이어 SteamID = {steamId}");
    }
}
```

- 플레이어 프리팹은 `NetworkManager > Player Prefab` 에 지정한다. 지정하면 접속 승인 시 자동 생성된다.
- 게임을 끝낼 때는 호스트가 `ServerManager.Instance.EndGame()` 을 호출한다. `NetworkManager.Shutdown()` 을 직접 부르지 않는다 (로비 상태가 맞지 않게 됨).

---

## 7. 전체 동작 흐름

### 7-1. 로비 → 게임 시작

```
호스트                                   Steam 로비                          참가자
──────                                   ─────────                          ──────
CreateSession(preset)
 └ CreateLobby(visibility, max, data) ─► 로비 생성
   OnLobbyCreated: 메타데이터 기록 ───► mslb_game, version, name, host,
                                         mode, min, req_ready, late_join,
                                         state=waiting, joinable=1, listed,
                                         code, pw_salt
   LobbyEnter_t → State=InLobby
                                                              ◄── SearchSessions (필터 검색)
                                                              ◄── JoinSession / JoinByCode / 초대
                                         멤버 입장 ─────────────► LobbyEnter_t
                                                                  게임·버전 검사
                                                                  (비밀번호면 pw_proof 제출)
                                                                  State=InLobby
                                         ◄──── member ready=1 ─── ToggleReady
GetStartBlockReason() == null
StartGame()
 ├ roster = 현재 멤버 전원
 ├ NetworkManager.StartHost()
 │   └ 트랜스포트: CreateListenSocketP2P
 ├ 로비 데이터 기록 ─────────────────► roster, approved="", game_server=호스트ID,
 │                                     joinable, state=ingame
 ├ State=InGame, OnGameStarted(true)
 └ (gameSceneName 있으면) NetworkSceneManager.LoadScene
                                         LobbyDataUpdate ─────────► state==ingame && roster 에 내가 있음
                                                                    ConnectToHost()
                                                                     ├ transport.ConnectToSteamId = 호스트
                                                                     └ StartClient → ConnectP2P
 트랜스포트 Connecting → AcceptConnection ◄═══════ Steam P2P ═══════
 ConnectionApprovalCallback
  로비 멤버? roster 또는 approved? → 허용 ═══════════════════════► OnClientConnected
                                                                    State=InGame, OnGameStarted(false)
                                                                    (NGO 가 현재 씬·오브젝트 동기화)
```

핵심 순서: **호스트는 리슨 소켓을 먼저 연 뒤에 `state=ingame` 을 기록한다.** 참가자가 그 값을 보고 접속을 시도할 때 서버는 이미 열려 있다.

### 7-2. 게임 종료

```
[정상 종료] 호스트 EndGame()
  State=Ending
  로비를 대기 상태로 되돌림 (state=waiting, roster/approved/game_server 비움, joinable=1)
  시스템 메시지 game_end 전송
  0.5초 대기 (hostEndDelay: 알림이 먼저 도착하도록)
  NetworkManager.Shutdown → 복귀 씬 로드 → State=InLobby, OnGameEnded(HostEnded)

  참가자: game_end 수신 → Shutdown → 준비 해제 → 로비에 남음 → 복귀 씬 → OnGameEnded(HostEnded)
          (알림보다 연결 끊김이 먼저 오면 아래 판정 로직으로 같은 결과)

[호스트 이탈] 호스트가 게임을 끄거나 LeaveSession
  참가자: 연결 끊김 감지 → State=Ending → 1.5초 대기 (disconnectCheckDelay)
          호스트가 로비에 있고 state=waiting  → HostEnded (로비에 남음)
          호스트가 로비에 없음                → HostLeft  (로비에서 나감)
          그 외                               → ConnectionFailed (로비에서 나감)
```

### 7-3. 난입 (게임 시작 후 참가)

```
새 플레이어 입장 (state=ingame, late_join=1)
  roster 또는 approved 에 있음 → 바로 재접속 (튕긴 뒤 복귀한 경우)
  없음 → member join_req=1, State=WaitingApproval
호스트: OnMemberDataChanged → RefreshJoinRequests
  (비밀번호 로비면 비밀번호 확인된 사람만) → PendingJoinRequests 추가 → OnJoinRequestsChanged
호스트 ApproveJoin(id) → approved 에 추가 → 로비 데이터 approved 갱신
  참가자: approved 에 내가 있음 → ConnectToHost → 접속 승인 통과 → InGame
호스트 RejectJoin(id) → 시스템 메시지 reject → 참가자 로비에서 나감
호스트가 그사이 EndGame → state=waiting → 대기자는 join_req 해제 후 InLobby
```

### 7-4. 상태 전이

```
            Steam 사용 불가
 Offline ◄────────────────── (시작 시)
            Steam 사용 가능
   Idle ◄──────────────────────────────────────────────┐
    │ CreateSession / JoinSession 성공                   │ 로비 나감
    ▼                                                    │
 InLobby ──(게임 진행 중인 로비에 입장)──► WaitingApproval ─┤
    │  StartGame / state=ingame 감지          │ 승인      │ 거절·취소
    ▼                                          ▼          │
 Starting ◄────────────────────────────────────┘          │
    │ 호스트: StartHost 성공 / 참가자: 접속 완료           │
    ▼                                                    │
  InGame ──(EndGame·연결 끊김·강퇴·나가기)──► Ending ──────┤
                                               │ 로비 유지│
                                               └──► InLobby
```

| 상태 | 의미 |
|---|---|
| `Offline` | Steam 사용 불가 (Steam 미실행, Steamworks.NET 없음, 지원하지 않는 플랫폼) |
| `Idle` | 로비 밖 |
| `InLobby` | 로비에서 대기 |
| `WaitingApproval` | 진행 중인 게임에 난입 요청, 호스트 승인 대기 |
| `Starting` | 리슨 서버 시작 / 호스트 접속 중 |
| `InGame` | 게임 진행 중 |
| `Ending` | 종료 처리 중 (중복 처리 방지용) |

---

## 8. 구현 상세 — 계층별

### 8-1. `SteamLog` (Core)

- 정적 클래스. `Verbose / Info / Warn / Error(분류, 메시지)`.
- 분류(`SteamLogCategory`): 스팀 / 로비 / 세션 / 네트워크. 수준(`SteamLogLevel`): 상세 / 정보 / 경고 / 오류.
- 기록 위치 세 곳:
  1. Unity 콘솔 — `[스팀로비][로비] 메시지` 형식, `ConsoleLevel` 이상만 출력
  2. 링버퍼 `Recent` (기본 256줄)
  3. 이벤트 `OnLogged` — 화면 콘솔·파일 저장 등은 이 이벤트를 구독해 구현
- `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 로 정적 상태를 초기화한다. **Enter Play Mode Options(도메인 리로드 끔)** 를 켜도 이전 플레이 세션의 구독자·로그가 남지 않게 하기 위함이며, 모든 싱글턴 클래스가 같은 방식을 쓴다.

### 8-2. `SteamManager` (Core)

- `[DefaultExecutionOrder(-1000)]` — 다른 모든 스크립트보다 먼저 `Awake` 에서 Steam 을 초기화한다.
- 초기화 순서: `Packsize.Test()` (플랫폼 바이너리 확인) → `DllCheck.Test()` (DLL 버전 확인) → `RestartAppIfNecessary` (옵션) → `SteamAPI.InitEx` → 사용자 정보 읽기 → `OnInitializeFinished(true)`.
- 실패하면 `LastInitError` 에 한국어 사유를 남기고 `OnInitializeFinished(false)`. 다른 서비스는 이 사유를 그대로 보여준다.
- `Update` 에서 `SteamAPI.RunCallbacks()` — 모든 Steam 콜백은 메인 스레드의 이 지점에서 실행된다.
- **종료 순서 보장**: Unity 는 `OnDestroy` 호출 순서를 보장하지 않는다. 그래서 `SteamManager.OnDestroy` 가 `SteamAPI.Shutdown()` 전에 직접 `ServerManager.HandleSteamShutdown()` (네트워크 종료) → `SteamLobbyService.HandleSteamShutdown()` (로비 퇴장·콜백 해제) 를 호출한다.

### 8-3. `SteamLobbyService` (Lobby)

게임 규칙을 모르는 범용 계층. 다른 게임에서도 그대로 쓸 수 있도록 세션 규칙은 넣지 않았다.

| 기능 | 메서드 | 결과 |
|---|---|---|
| 생성 | `CreateLobby(name, visibility, max, extraData)` | `LobbyCreated_t` 에서 기본 키 + extraData 기록 → `LobbyEnter_t` → `OnLobbyEntered` |
| 검색 | `RequestLobbyList(filters, onResult)` | 게임·버전 필터는 항상 추가. 콜백을 주면 콜백으로만, 아니면 `OnLobbyListUpdated` |
| 참가 | `JoinLobby(id)` | `LobbyEnter_t` → 게임·버전 검사 → `OnLobbyEntered` |
| 퇴장 | `LeaveLobby()` | 즉시 상태 초기화 → `OnLobbyLeft` |
| 로비 데이터 | `SetLobbyData` (호스트만), `GetLobbyData`, `GetAllLobbyData`, `SetLobbyJoinable` | `OnLobbyDataChanged` |
| 멤버 데이터 | `SetMemberData` (내 것만), `GetMemberData` | `OnMemberDataChanged(steamId)` |
| 준비 | `SetReady`, `IsLocalReady`, `AreAllMembersReady` | 멤버 데이터 `ready` |
| 채팅 | `SendChat` | `OnChatMessage(member, text)` |
| 시스템 메시지 | `SendSystemMessage(command, arg)` | `OnSystemMessage(sender, command, arg)` |
| 초대 | `OpenInviteDialog` | Steam 오버레이 |
| 멤버 변동 | — | `OnMembersChanged`, `OnMemberJoined`, `OnMemberLeft`, `OnOwnerChanged` |

구현 포인트:
- **모든 입장 경로를 `LobbyEnter_t` 하나로 처리**: 직접 생성, 목록 클릭, ID 입력, 친구 초대(`GameLobbyJoinRequested_t`), 친구 목록 "게임 참가"(`GameRichPresenceJoinRequested_t`), 게임 미실행 중 초대 수락(실행 인자 `+connect_lobby <id>`) 이 모두 `JoinLobby` → `LobbyEnter_t` 로 모인다.
- **입장 시 검사**: 초대·ID 로 들어오면 검색 필터를 거치지 않으므로, 입장 직후 `mslb_game` 과 `version` 을 비교해 다르면 즉시 나간다.
- **Rich Presence**: 입장하면 `connect = "+connect_lobby <id>"` 를 설정해 친구 목록에서 바로 참가할 수 있게 한다.
- **시스템 메시지**: 로비 채팅에 `\u0001mslb:명령|인자` 형식으로 보낸다. 수신 측은 접두사를 보고 채팅창에 표시하지 않고 `OnSystemMessage` 로 넘긴다. 별도 네트워크 채널 없이 로비 전원에게 즉시 알릴 수 있다.
- **호스트 변경**: `LobbyChatUpdate_t` 마다 소유자를 다시 읽어 바뀌었으면 `OnOwnerChanged`. 새 호스트는 `host`, `host_name` 을 자기 정보로 갱신해 로비 목록에 이전 호스트가 남지 않게 한다.
- **늦게 도착하는 닉네임**: `PersonaStateChange_t` 를 받으면 멤버 목록을 다시 읽는다 ("[unknown]" 방지).
- 공개 타입은 Steamworks 타입 대신 `ulong`, `LobbySummary`(로비 메타데이터 전체를 Dictionary 로 보관), `LobbyMember` 만 쓴다.

### 8-4. `SteamNetworkingSocketsTransport` (Transport)

NGO 의 `NetworkTransport` 추상 클래스를 Steam Networking Sockets(P2P)로 구현했다. 커뮤니티 트랜스포트를 쓰지 않고 직접 구현한 이유는 git 의존성을 하나 줄이고, `SteamID ↔ clientId` 매핑을 접속 승인에 직접 쓰기 위해서다.

| NGO 메서드 | 구현 |
|---|---|
| `Initialize` | 상태 콜백(`SteamNetConnectionStatusChangedCallback_t`) 등록, `InitRelayNetworkAccess()` 로 릴레이 접속 예열 |
| `StartServer` | `CreateListenSocketP2P(virtualPort)` + `CreatePollGroup()` |
| `StartClient` | `ConnectToSteamId` 로 `SteamNetworkingIdentity` 를 만들어 `ConnectP2P` |
| `Send` | 배열을 `GCHandle` 로 고정해 `SendMessageToConnection`. Unreliable 계열 → `UnreliableNoNagle`, 나머지 → `ReliableNoNagle` |
| `PollEvent` | 큐가 비었으면 메시지를 받아(`ReceiveMessagesOnPollGroup` / `OnConnection`) 큐에 넣고 하나씩 반환 |
| `DisconnectRemoteClient` / `DisconnectLocalClient` | `CloseConnection(..., linger: true)` — 연결 종료 사유 같은 마지막 신뢰성 메시지를 보낸 뒤 닫음 |
| `GetCurrentRtt` | `GetConnectionRealTimeStatus` 의 `m_nPing` |
| `Shutdown` | 모든 연결·리슨 소켓·폴 그룹 정리, 콜백 해제 |

연결 상태 콜백 처리:

| Steam 상태 | 서버 | 클라이언트 |
|---|---|---|
| `Connecting` | 우리 리슨 소켓으로 온 요청이면 `AcceptConnection` | — |
| `Connected` | 새 clientId(1부터) 발급, `clientId ↔ 연결 ↔ SteamID` 매핑, 폴 그룹 등록, `Connect` 이벤트 | `Connect(ServerClientId=0)` 이벤트 |
| `ClosedByPeer` / `ProblemDetectedLocally` | 매핑 제거, `Disconnect` 이벤트, 연결 닫기 | `Disconnect` 이벤트, 연결 닫기 |

- `TryGetSteamId(clientId, out steamId)` — 서버에서 NGO clientId 에 해당하는 SteamID. `ServerManager` 의 접속 승인과 강퇴에 쓰인다.
- 이벤트를 큐에 담는 이유: Steam 콜백은 `Update`(RunCallbacks)에서, NGO 폴링은 `EarlyUpdate` 에서 일어나므로 둘 사이를 큐로 이어 준다.
- 수신 메시지는 매번 새 `byte[]` 로 복사한 뒤 즉시 `Release` 한다 (Steam 메시지 버퍼를 NGO 에 넘기지 않음).

### 8-5. `SessionPreset` / `SessionTypes` (Session)

- `SessionPreset`: 5-2 참고.
- `SessionCreateOptions`: 로비별 덮어쓰기 (이름, 비밀번호, 목록 숨김, 공개 범위, 인원).
- `JoinRequest`: 난입 대기열 항목 (`steamId`, `personaName`, `requestTime`).
- `SessionKeys`: 세션 계층 메타데이터 키와 시스템 메시지 명령 (9장).
- `LobbySummarySessionExtensions`: 검색 결과를 세션 관점으로 읽는 확장 메서드 — `ModeId()`, `HasPassword()`, `IsInGame()`, `AllowsLateJoin()`, `MinPlayers()`.
- `SessionUtil` (internal): 참가 코드·솔트 생성(`RandomNumberGenerator`), 비밀번호 증명값 계산(SHA256), SteamID 목록 직렬화(쉼표 구분).

### 8-6. `ServerManager` (Session)

로비 서비스와 NGO 를 엮어 세션 규칙을 구현한다. 내부 상태는 다음과 같다.

| 필드 | 보관 위치 | 의미 |
|---|---|---|
| `roster` | 호스트 메모리 + 로비 데이터 | 게임 시작 시점의 멤버 |
| `approved` | 호스트 메모리 + 로비 데이터 | 난입 승인된 멤버 |
| `pendingRequests` | 호스트 메모리 | 난입 대기열 |
| `passwordVerified` / `passwordDeadlines` | 호스트 메모리 | 비밀번호 확인 완료 / 제출 기한 |
| `hostPassword` | **호스트 메모리에만** | 비밀번호 원문 |
| `activeGameScene` / `activeReturnScene` | 로컬 | 불러올 게임 씬 / 돌아갈 씬 |
| `connectedHostSteamId` | 로컬 | 접속한 호스트 (이탈 판정용) |

이벤트 연결:

| 받는 이벤트 | 처리 |
|---|---|
| `Lobby.OnLobbyEntered` | 호스트면 InLobby. 참가자면 비밀번호 처리 후, 진행 중인 로비면 난입 처리 |
| `Lobby.OnLobbyLeft` | 로비 상태 초기화, 네트워크가 살아 있으면 종료, Idle |
| `Lobby.OnLobbyDataChanged` | (참가자) InLobby 에서 `state=ingame` → 접속 또는 난입 / WaitingApproval 에서 승인·게임 종료 감지 |
| `Lobby.OnMemberJoined` | (호스트) 비밀번호 로비면 제출 기한 설정 |
| `Lobby.OnMemberDataChanged` | (호스트) 비밀번호 검증, 난입 대기열 갱신 |
| `Lobby.OnMemberLeft` | 검증·기한·대기열에서 제거 |
| `Lobby.OnOwnerChanged` | (새 호스트, 게임 전) 호스트 역할 인수 |
| `Lobby.OnSystemMessage` | **로비 소유자가 보낸 것만** 처리: kick / reject / game_end |
| `NetworkManager.OnClientConnectedCallback` | (참가자) Starting → InGame |
| `NetworkManager.OnClientDisconnectCallback` | (참가자) 연결 끊김 → 종료 판정 |
| `NetworkManager.OnTransportFailure` | 오류로 종료 |

`Update` 는 두 가지만 검사한다: (호스트) 비밀번호 제출 기한 초과 → 강퇴, (참가자) 접속 제한 시간(기본 20초) 초과 → 실패 처리.

모든 종료 경로는 `FinishLocalGame(reason, stayInLobby, message)` 한 곳으로 모인다:
네트워크 종료 → 게임 상태 정리 → 로비에 남거나 나가기 → 복귀 씬 로드 → `OnGameEnded`.

---

## 9. 로비 메타데이터 규약

Steam 로비는 문자열 키/값 저장소다. 모든 클라이언트가 같은 값을 보므로 **"공유 상태"** 로 쓴다.

### 9-1. 로비 데이터 (호스트만 기록)

| 키 | 기록 주체 | 값 | 용도 |
|---|---|---|---|
| `mslb_game` | LobbyService | Game Id | 공용 AppID 에서 우리 게임만 검색·입장 |
| `version` | LobbyService | Game Version | 다른 버전 매칭 차단 |
| `name` | LobbyService | 로비 이름 | 목록 표시 |
| `host`, `host_name` | LobbyService | 호스트 SteamID / 닉네임 | 목록 표시 (호스트 변경 시 갱신) |
| `mode` | Session | 프리셋 modeId | 모드별 검색 |
| `min` | Session | 최소 인원 | 시작 조건 (참가자·새 호스트도 읽음) |
| `req_ready` | Session | "1"/"0" | 준비 필요 여부 |
| `late_join` | Session | "1"/"0" | 난입 허용 |
| `state` | Session | `waiting` / `ingame` | 세션 단계. 참가자의 접속 신호 |
| `joinable` | Session | "1"/"0" | 검색 노출 (대기 중이거나, 진행 중이지만 난입 허용) |
| `listed` | Session | "1"/"0" | 로비 목록 노출 (0 이면 코드·초대 전용) |
| `code` | Session | 6자리 | 참가 코드 검색 |
| `pw_salt` | Session | Base64 솔트 / 빈 값 | 비밀번호 유무 + 증명값 계산용 |
| `game_server` | Session | 호스트 SteamID | 리슨 서버 접속 대상 |
| `roster` | Session | SteamID 쉼표 목록 | 시작 멤버 (재접속 허용) |
| `approved` | Session | SteamID 쉼표 목록 | 난입 승인 멤버 |

### 9-2. 멤버 데이터 (각자 자기 것만 기록)

| 키 | 값 | 용도 |
|---|---|---|
| `ready` | "1"/"0" | 준비 상태 |
| `join_req` | "1"/빈 값 | 난입 요청 |
| `pw_proof` | Base64 | 비밀번호 증명값 |

### 9-3. 시스템 메시지 (로비 채팅 경유, 호스트 → 전원)

| 명령 | 인자 | 수신 측 동작 |
|---|---|---|
| `kick` | `대상SteamID:사유` | 대상이면 게임·로비에서 나감 (`Kicked`) |
| `reject` | `대상SteamID` | 대상이 승인 대기 중이면 로비에서 나감 |
| `game_end` | — | 게임 중이면 로비로 복귀 (`HostEnded`) |

### 9-4. 검색 필터

| 검색 | 필터 |
|---|---|
| `SearchSessions` | `mslb_game`, `version`, `joinable=1`, `listed=1`, (+ `mode`, `Filter Search By Mode` 가 켜져 있고 기본 프리셋 modeId 가 있을 때) |
| `JoinByCode` | `mslb_game`, `version`, `code` — 찾은 뒤 `joinable` 을 확인 |

Steam 의 로비 검색은 **Public 로비만** 반환한다. 그래서 "목록 숨김" 은 Steam 공개 범위가 아니라 `listed` 키로 구현했고, 코드 검색은 Public 로비에서만 동작한다.

---

## 10. 핵심 규칙의 구현 방식

### 10-1. 시작 조건 (`GetStartBlockReason`)

위에서부터 검사해 처음 걸리는 이유를 한국어로 돌려준다.

1. 로비에 있는지 / 호스트인지 / 상태가 InLobby 인지
2. 이전 네트워크 세션이 정리 중인지 (`IsListening`, `ShutdownInProgress`)
3. `PlayerCount < MinPlayers` → "최소 N명이 필요합니다"
4. `PlayerCount > MaxPlayers`
5. `RequireAllReady` 면 **호스트를 제외한** 전원 준비 여부 (호스트는 시작 버튼이 곧 준비)
6. 비밀번호 로비면 전원 비밀번호 확인 여부

### 10-2. 접속 승인 (`ApprovalCheck`)

리슨 서버는 누구나 SteamID 로 접속을 시도할 수 있으므로 NGO 의 Connection Approval 로 막는다.

1. 호스트 자신(`ServerClientId`) → 허용
2. 트랜스포트에서 SteamID 를 모름 → 거부
3. 현재 로비 멤버가 아님 → 거부
4. `roster` 에도 `approved` 에도 없음 → "호스트 승인이 필요합니다"
5. `AdditionalApprovalCheck` (게임이 지정한 추가 검사, 예: 차단 목록)
6. 통과 → 허용, 플레이어 프리팹이 있으면 생성

SteamID 는 클라이언트가 보낸 `ConnectionData` 가 아니라 **Steam 이 인증한 연결 정보(트랜스포트 매핑)** 에서 가져오므로 위조할 수 없다.

### 10-3. 비밀번호

```
호스트: 비밀번호 원문은 메모리에만 보관, 로비에는 pw_salt 만 공개
참가자: pw_proof = Base64(SHA256("솔트:비밀번호:내SteamID")) 를 멤버 데이터로 제출
호스트: 같은 계산으로 검증
        일치 → passwordVerified 에 추가
        불일치 → Kick("비밀번호가 틀렸습니다.")
        30초(passwordTimeout) 안에 제출 안 함 → Kick("비밀번호 확인 시간이 초과되었습니다.")
```

설계 이유:
- 로비 데이터는 누구나 읽을 수 있으므로 비밀번호 해시를 공개하면 그대로 복사해 통과할 수 있다. 그래서 **원문은 호스트만** 갖고, 증명값에 **각자의 SteamID 를 섞어** 다른 사람의 값을 복사해도 통과하지 못하게 했다.
- 친구 초대로 들어와도 비밀번호를 묻는다 (`OnPasswordRequired`).
- 게임 시작과 난입 승인 모두 "비밀번호 확인된 사람" 만 대상이 된다. 확인되지 않은 사람은 강퇴되고 로비 멤버가 아니게 되므로 접속 승인도 통과할 수 없다.
- 게임 시작 전 호스트가 나가 다른 사람이 호스트가 되면 새 호스트는 비밀번호를 모른다. 이때 기존 멤버는 확인된 것으로 간주하고, **새 참가는 막는다** (`joinable=0`, `SetLobbyJoinable(false)`).

### 10-4. 참가 코드와 목록 숨김

- 생성 시 6자리 코드를 `RandomNumberGenerator` 로 만든다. 헷갈리는 `0/O`, `1/I` 는 뺀 32자 집합을 쓴다.
- `JoinByCode` 는 입력을 대문자로 정규화한 뒤 `code` 필터로 검색하고, `joinable` 을 확인한 다음 `JoinSession` 한다.
- 샘플 UI 는 입력이 10자를 넘는 숫자면 로비 ID, 아니면 참가 코드로 판단한다.

### 10-5. 게임 씬 이동과 복귀

- 시작 시 `activeReturnScene` = `Return Scene Name`(비우면 현재 씬)을 기록한다.
- 프리셋에 `gameSceneName` 이 있으면 호스트가 `NetworkSceneManager.LoadScene(Single)`. 참가자는 NGO 가 접속 시 호스트의 씬으로 자동 동기화한다 (난입자 포함).
- `gameSceneName` 이 비어 있으면 씬을 옮기지 않는다. NGO 는 클라이언트가 이미 같은 씬에 있으면 다시 불러오지 않으므로 로비 씬에서 그대로 게임 상태가 된다.
- 종료 시 현재 씬이 복귀 씬과 다르면 `SceneManager.LoadScene(복귀 씬)`.
- 서비스 오브젝트는 `DontDestroyOnLoad` 라 씬이 바뀌어도 유지된다.

### 10-6. 호스트 변경 (게임 전)

Steam 은 소유자가 나가면 남은 멤버 중 한 명을 자동으로 새 소유자로 정한다.

- `SteamLobbyService`: `host`, `host_name` 갱신
- `ServerManager`: 기존 멤버를 비밀번호 확인 완료로 처리, 비밀번호 로비면 새 참가 차단, 게임 씬은 기본 프리셋 값을 사용
- 이후 새 호스트가 정상적으로 `StartGame` 할 수 있다.

게임 **중** 호스트 변경(호스트 마이그레이션)은 지원하지 않는다 → 7-2 의 호스트 이탈 처리.

---

## 11. 에디터 도구

| 메뉴 | 클래스 | 동작 |
|---|---|---|
| Tools > Magnetic Steam Lobby > Steamworks.NET 설치 | `SteamworksDependencyInstaller` | `PackageManager.Client.Add(git URL)` 로 설치. 이미 설치되어 있으면 비활성 |
| (에디터 시작 시 자동) | `SteamworksDependencyInstaller` | Steamworks.NET 이 없으면 세션당 한 번 설치 여부 질문 (배치 모드 제외) |
| Tools > Magnetic Steam Lobby > steam_appid.txt 만들기 (480) | `SteamworksDependencyInstaller` | 프로젝트 루트에 파일 생성 (있으면 그대로) |
| GameObject > Magnetic > Steam Session Services | `SteamSessionServicesMenu` | 5-1 의 서비스 오브젝트 생성, 참조 자동 연결 |
| Create > Magnetic > Steam Lobby > Session Preset | `SessionPreset` | 프리셋 에셋 생성 |
| Tools > Magnetic Steam Lobby > Build Lobby Scene (현재 씬) | `LobbySceneBuilder` (샘플) | 12-2 참고 |

`SteamSessionServicesMenu.CreateServices(preset, gameId, gameVersion)` 와 `LoadOrCreatePreset(path)` 는 public 이라 다른 에디터 스크립트에서도 쓸 수 있다.

---

## 12. 샘플

Package Manager > Magnetic Steam Lobby > Samples > Import. `Assets/Samples/Magnetic Steam Lobby/<버전>/` 에 복사된다.

### 12-1. Korean Log Console

| 컴포넌트 | 기능 |
|---|---|
| `SteamLogConsole` | F1 열기/닫기, F2·F3 글자 크기. 수준(상세/정보/경고/오류)·분류(스팀/로비/세션/네트워크) 필터, 자동 스크롤, 복사, 지우기, 하단에 Steam·세션 상태. 기본적으로 Development 빌드·에디터에서만 동작 |
| `SteamLogFileWriter` | `persistentDataPath/SteamLobbyLogs/steamlobby_yyyyMMdd_HHmmss.log` 에 UTF-8(BOM) 저장 (메모장 한글 정상). 최근 10개만 보관. 컴포넌트 우클릭 > "로그 폴더 열기" |

둘 다 아무 오브젝트에 붙이면 되고 `DontDestroyOnLoad`·중복 방지가 되어 있다. 새 입력 시스템과 기존 입력 매니저를 모두 지원한다 (`ENABLE_INPUT_SYSTEM` / `ENABLE_LEGACY_INPUT_MANAGER`).

### 12-2. Lobby UI

`SessionLobbyController` 화면 구성:

| 영역 | 내용 |
|---|---|
| 상단 | 제목, 내 닉네임, 뒤로 버튼 (`Back Scene Name` 으로 이동, 비우면 로비만 나감) |
| 로비 밖 (왼쪽) | 로비 이름, 비밀번호, 프리셋 선택(2개 이상일 때만 표시), 목록 숨기기, 친구만, 만들기 / 코드·ID 입력, 참가 비밀번호, 참가 |
| 로비 밖 (오른쪽) | 로비 목록 (`[비밀번호]` 표시, 인원, 호스트, 진행 중 여부, 가득 차면 비활성), 새로고침, 10초 자동 새로고침 |
| 로비 안 | 로비 이름, 규칙 요약(인원·준비·난입·비밀번호·목록 숨김·진행 중), 참가 코드·로비 ID, 코드 복사 |
| 로비 안 (왼쪽) | 멤버 목록 (★ 호스트, (나), 준비 완료/대기 중/승인 대기) + 빈 자리 |
| 로비 안 (오른쪽) | 채팅 (Enter 전송) |
| 하단 액션 | 준비(참가자) / 게임 시작·시작 불가 사유(호스트) / 게임 종료(호스트, 게임 중) / 친구 초대 / 나가기 |
| 난입 승인 패널 | 호스트·게임 중에만 표시. 요청자별 승인/거절 버튼 |
| 오버레이 | 비밀번호 입력 창, 승인 대기 창 |

`LobbySceneBuilder` 가 하는 일:
1. 스크립트가 빠진(Missing Script) 컴포넌트 정리, 그 결과 빈 루트 오브젝트 삭제
2. 기본 프리셋 생성 (`Assets/Settings/SteamLobby/DefaultSessionPreset.asset`)
3. ServerManager 가 없는 예전 Steam 오브젝트 교체, 서비스 오브젝트가 없으면 생성
4. Korean Log 샘플이 있으면 로그 콘솔·파일 기록을 서비스 오브젝트에 부착
5. `SteamLobbyCanvas`, EventSystem, 프리팹 3종(`Assets/Prefabs/SteamLobbyUI/` 의 LobbyListItem / LobbyMemberItem / JoinRequestItem) 생성
6. 컨트롤러 참조 전부 자동 연결 후 씬 저장

배치 모드 실행:

```
Unity.exe -batchmode -quit -projectPath <프로젝트> ^
  -executeMethod Magnetic.SteamLobby.Samples.EditorTools.LobbySceneBuilder.BuildFromCommandLine ^
  -lobbyScene Assets/Scenes/Lobby.unity ^
  [-backScene Hub] [-gameId MyGame] [-gameVersion 0.1.0] [-removeObjects 예전Canvas,예전오브젝트]
```

UI 는 다시 실행하면 처음 상태로 재생성되므로, 직접 꾸민 뒤에는 재실행에 주의한다. 컨트롤러의 모든 참조는 비워도 되므로 UI 일부를 지워도 동작은 유지된다.

---

## 13. 설계 원칙

| 원칙 | 적용 |
|---|---|
| **계층 분리** | Steam 로비(범용) ← 세션 규칙(ServerManager) ← UI. 로비 서비스는 게임 규칙을 모르고, UI 는 Steamworks 를 모른다 |
| **Steam 없이도 컴파일** | `STEAM_ENABLED` 조건부 컴파일 + versionDefines. 공개 타입에 Steamworks 타입 미노출 |
| **로비 데이터 = 공유 상태** | 별도 서버 없이 Steam 로비 메타데이터로 상태를 공유. 호스트만 로비 데이터를, 각자 자기 멤버 데이터만 기록 |
| **호스트 권한 신뢰, 나머지 검증** | 시스템 메시지는 로비 소유자가 보낸 것만 처리. 게임 접속은 Steam 인증 SteamID 로 승인 |
| **단일 종료 경로** | 모든 게임 종료는 `FinishLocalGame` 한 곳. `Ending` 상태로 중복 처리 방지 |
| **순서 보장** | 리슨 소켓을 연 뒤 `state=ingame` 기록 / Steam 종료 전 상위 서비스부터 정리 / 종료 알림 후 지연 뒤 서버 종료 |
| **도메인 리로드 끔 대응** | 모든 정적 상태를 `SubsystemRegistration` 에서 초기화 |
| **사용자 메시지는 한국어** | 로그·오류 이벤트·UI 문구 모두 한국어. 사용자에게 보여줄 문구는 `OnSessionError` 로 일원화 |
| **참조는 null 허용** | 샘플 UI 와 서비스 참조는 비어 있어도 예외 없이 동작 (같은 오브젝트에서 자동 탐색) |

---

## 14. 테스트 방법

### 14-1. 준비

- Steam 클라이언트 실행·로그인
- 프로젝트 루트에 `steam_appid.txt` (480)
- 빌드 대상 Windows/Mac/Linux

### 14-2. 혼자 확인할 수 있는 것

같은 Steam 계정으로 두 클라이언트를 동시에 로비에 넣을 수 없으므로 혼자서는 다음까지만 확인할 수 있다.

1. 최소 인원 1명 프리셋을 만들어 Default Preset 으로 지정
2. 플레이 → 로비 만들기 → 로그에 "로비를 만들었습니다", 참가 코드 표시 확인
3. 게임 시작 → "리슨 서버를 열었습니다" → 상태 InGame
4. 게임 종료 → 로비로 복귀, 다시 시작 가능
5. 로비 나가기 → 목록에서 사라짐

### 14-3. 멀티 확인 (PC 2대 + Steam 계정 2개)

| 항목 | 확인 방법 |
|---|---|
| 검색·참가 | B 의 목록에 A 로비 표시 → 클릭 참가 |
| 참가 코드 | A 의 코드를 B 가 입력 |
| 비밀번호 | 틀린 비밀번호 → B 강퇴 / 맞는 비밀번호 → 정상 / 30초 미입력 → 강퇴 |
| 준비·시작 | B 준비 전 A 시작 버튼 비활성·사유 표시 → 준비 후 시작 → B 자동 접속 |
| 게임 씬 | 프리셋에 게임 씬 지정 → 둘 다 이동 |
| 난입 | 게임 중 C 참가 → A 의 승인 패널 → 승인 시 C 접속 / 거절 시 C 로비 퇴장 |
| 재접속 | B 가 게임을 끄고 다시 로비 참가 → 승인 없이 재접속 |
| 정상 종료 | A 게임 종료 → B 로비 복귀, 다시 시작 가능 |
| 호스트 이탈 | 게임 중 A 종료 → B 게임 종료 + 로비 밖으로 복귀 (`HostLeft`) |
| 호스트 변경 | 게임 전 A 퇴장 → B 가 새 호스트, 시작 버튼 표시 |
| 초대 | 빌드에서 Shift+Tab 오버레이 초대 (에디터는 오버레이 없음) |

로그는 Unity 콘솔에서 `[스팀로비]` 로 검색하거나 F1 콘솔, 로그 파일로 확인한다.

---

## 15. 문제 해결

| 증상 | 원인 / 해결 |
|---|---|
| "Steamworks.NET 패키지가 설치되지 않았습니다" | 4-1 설치 |
| "Steam API 초기화 실패: ... NoSteamClient" | Steam 클라이언트 실행·로그인 |
| "설정된 appId 와 실제 실행 AppID 가 다릅니다" | `steam_appid.txt` 와 `SteamManager.App Id` 를 맞춤 |
| "현재 플랫폼은 Steamworks 를 지원하지 않습니다" | 빌드 대상을 PC Standalone 으로 |
| 로비 목록이 비어 있음 | 상대와 `Game Id`·`Game Version`·`mode` 가 같은지 확인. 상대 로비가 Public 이고 `listed=1` 인지, 게임이 진행 중이면서 난입 불가(`joinable=0`)는 아닌지 확인 |
| 코드로 못 찾음 | 로비 공개 범위가 Public 이 아님 (FriendsOnly/Private 은 검색 불가) |
| "다른 게임의 로비입니다" / "게임 버전이 다릅니다" | 초대·ID 로 다른 게임·버전 로비에 들어감 |
| 시작 버튼 비활성 | 하단 사유 문구 확인 (`GetStartBlockReason`) |
| 참가자가 접속하지 못함 (시간 초과) | 방화벽, Steam 릴레이 접속 지연. 로그의 "호스트와의 연결이 끊겼습니다: 사유" 확인. 두 쪽의 `Virtual Port` 가 같은지 |
| "게임 접속이 거부되었습니다: 호스트 승인이 필요합니다" | 시작 이후 들어왔는데 승인 전 접속 시도. 승인 대기 흐름을 따름 |
| 초대 창이 안 뜸 | 에디터에서는 오버레이가 동작하지 않음. 빌드에서 확인 |
| 게임 씬을 못 불러옴 | 씬이 Build Profiles 씬 목록에 없음 |
| 씬 재진입 시 서비스가 둘 | 정상. 새 오브젝트는 `SteamManager` 가 즉시 제거 |

---

## 16. 알려진 한계와 확장 방향

### 한계

- **호스트 마이그레이션 없음**: 게임 중 호스트가 나가면 게임이 끝난다. 리슨 서버의 게임 상태를 다른 PC 로 옮기려면 게임별 상태 직렬화가 필요하다.
- **호스트 신뢰**: 리슨 서버 구조상 호스트 PC 가 권한을 가진다. 경쟁 게임의 치트 방지에는 전용 서버가 필요하다.
- **비밀번호 강도**: 캐주얼 용도. 솔트와 각 멤버의 증명값이 로비 멤버에게 보이므로, 같은 로비에 들어온 사람은 짧은 비밀번호를 오프라인 대입으로 알아낼 수 있다.
- **코드 검색은 Public 로비만**: Steam 검색 API 의 제약.
- **멀티 테스트에 계정 2개 필요**: 같은 계정 동시 접속 불가.
- **시스템 메시지 신뢰 범위**: 로비 소유자 확인만 하므로, 호스트가 변조된 클라이언트면 임의로 강퇴할 수 있다 (호스트 신뢰 원칙과 동일).

### 확장 방향

| 아이디어 | 구현 위치 |
|---|---|
| 빠른 매칭 (검색 → 없으면 생성) | `ServerManager` 에 `QuickMatch()` 추가: `SearchSessions` 결과 첫 항목 참가, 0개면 `CreateSession` |
| 차단 목록·밴 | `AdditionalApprovalCheck` + 로비 입장 시 `Kick` |
| 팀/슬롯 선택 | 멤버 데이터에 `team` 키 추가, 시작 조건에 팀 인원 검사 |
| 로비 설정 변경 (인원·규칙) | 호스트가 `Lobby.SetLobbyData(SessionKeys.MinPlayers, ...)` 갱신 |
| 시작 카운트다운 | 호스트가 `state=countdown` + 종료 시각 기록 후 `StartGame` |
| 커스텀 플레이어 생성 정보 | `ConnectionData` 에 캐릭터 선택 등을 담고 `AdditionalApprovalCheck` 에서 읽기 |

---

## 17. Magnetic Multi-Game 프로젝트 적용 기록

이 패키지는 `Magnetic Multi-Game` 프로젝트의 Steam 로비 코드를 분리·확장해 만들었다. 적용 상태(2026-11-18):

| 항목 | 내용 |
|---|---|
| 참조 방식 | `Packages/manifest.json` → `"com.magnetic.steamlobby": "file:../../MagneticSteamLobby"` (로컬 개발) |
| 자동 설치된 의존성 | `com.unity.netcode.gameobjects` 2.13.3 (+ `com.unity.transport`) |
| 샘플 | `Assets/Samples/Magnetic Steam Lobby/0.1.0/Korean Log Console`, `.../Lobby UI` |
| Lobby 씬 | `LobbySceneBuilder` 배치 모드로 재구성: `SteamSessionServices`(로그 콘솔·파일 기록 포함), `SteamLobbyCanvas`, `SessionLobbyController`, `EventSystem` |
| 생성 에셋 | `Assets/Settings/SteamLobby/DefaultSessionPreset.asset` (2~4인), `Assets/Prefabs/SteamLobbyUI/` 프리팹 3종 |
| 로비 설정 | Game Id = `MagneticMultiGame`, Game Version = `0.1.0` |
| 이전 코드 | `_Backup_LegacySteam_20261118/` 로 이동 (예전 `MagneticMultiGame.Steam` 어셈블리, `LobbyController`, `LobbyUIBuilder`, `SteamDebugOverlay`, 예전 프리팹, 변경 전 Lobby 씬) |
| 검증 | Unity 6000.3.24f1 배치 모드에서 컴파일 오류·경고 없음, 씬 구성 성공. 실제 Steam 멀티플레이는 미검증 (PC 2대·계정 2개 필요) |

예전 코드에서 바뀐 점:

| 예전 (`MagneticMultiGame.Steam`) | 지금 (`Magnetic.SteamLobby`) |
|---|---|
| 로비 서비스가 게임 시작 신호까지 담당 | 로비 서비스는 범용, 세션 규칙은 `ServerManager` |
| 게임 시작 후 접속은 TODO (트랜스포트 없음) | NGO + 자체 Steam 트랜스포트로 리슨 서버 접속까지 구현 |
| 인원 규칙은 Steam 최대 인원만 | 프리셋으로 최소/최대 인원·준비·난입 규칙 |
| 게임 중 참가 불가 | 승인 대기열로 난입 |
| 비밀번호·코드 없음 | 비밀번호(호스트 검증)·6자리 코드·목록 숨김 |
| 호스트 변경 시 로비 정보에 이전 호스트가 남음 | 새 호스트가 갱신 |
| 영어/한국어 혼용 로그 | 전부 한국어 로그 + 분류·수준·파일 저장 |
| F1 디버그 오버레이 | Korean Log Console 샘플로 대체 |

GitHub 에 올린 뒤에는 `file:` 경로를 git URL 로 바꾸면 된다 (4-2 참고).
