# Magnetic Steam Lobby

Steam 로비로 플레이어를 모으고, 게임을 시작하면 **호스트 PC 에서 리슨 서버**를 열어 게임을 진행하는 Unity 패키지.

- **ServerManager**: 최소/최대 인원 프리셋으로 로비 생성 → 준비 확인 → 호스트 PC 에서 `StartHost()` → 참가자 자동 접속
- **Steam 로비**: 로비 목록, 로비 ID/참가 코드 참가, Steam 친구 초대, 비밀번호 로비, 채팅
- **난입 승인 대기열**: 게임 중 들어온 플레이어는 대기하다가 호스트가 승인하면 진행 중인 게임에 접속
- **Steam Networking Sockets 트랜스포트**: Netcode for GameObjects 용. IP/포트 없이 SteamID 로 접속, NAT 통과는 Steam 릴레이가 처리
- **한국어 로그**: 모든 로그가 한국어. 화면 콘솔·파일 저장 샘플 포함

| 항목 | 버전 |
|---|---|
| Unity | 6000.0 이상 (6000.3 에서 개발) |
| Netcode for GameObjects | 2.13.3 (자동 설치) |
| Steamworks.NET | 2024.8.0 (별도 설치, 아래 참고) |
| 플랫폼 | Windows / macOS / Linux Standalone |

---

## 설치

### 1. Steamworks.NET 설치

git URL 패키지는 다른 git 패키지를 의존성으로 자동 설치하지 못하므로 따로 설치해야 한다.
이 패키지를 먼저 설치하면 에디터가 설치 여부를 묻는다. 또는 메뉴 **Tools > Magnetic Steam Lobby > Steamworks.NET 설치**.

직접 설치: Package Manager > `+` > *Install package from git URL*

```
https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2024.8.0
```

### 2. 이 패키지 설치

Package Manager > `+` > *Install package from git URL*

```
https://github.com/<계정>/MagneticSteamLobby.git
```

특정 버전 고정: `...MagneticSteamLobby.git#v0.1.0`

로컬 폴더로 개발할 때는 `Packages/manifest.json` 에 경로를 넣는다.

```json
"com.magnetic.steamlobby": "file:../../MagneticSteamLobby"
```

### 3. steam_appid.txt

에디터에서 실행하려면 프로젝트 루트(Assets 옆)에 `steam_appid.txt` 가 있어야 한다. 메뉴 **Tools > Magnetic Steam Lobby > steam_appid.txt 만들기 (480)**.
480 은 Valve 의 테스트용 AppID(Spacewar). 출시 전 발급받은 AppID 로 바꾸고 `SteamManager.appId` 도 같이 바꾼다.

---

## 빠른 시작

1. **프리셋 만들기**: Project 창 우클릭 > Create > Magnetic > Steam Lobby > Session Preset
   - 최소/최대 인원, 준비 필요 여부, 난입 허용, 게임 씬 이름, 모드 ID
2. **서비스 오브젝트 만들기**: Hierarchy 우클릭 > Magnetic > Steam Session Services
   - `SteamManager` + `SteamLobbyService` + `SteamNetworkingSocketsTransport` + `NetworkManager` + `ServerManager` 가 한 오브젝트에 생성됨
   - `ServerManager.Default Preset` 에 프리셋 지정, `SteamLobbyService.Game Id` 에 게임 고유 이름 지정
3. **UI 연결**: 샘플 *Lobby UI* 를 가져온 뒤 **Tools > Magnetic Steam Lobby > Build Lobby Scene (현재 씬)** 을 실행하면 로비 화면 전체가 생성된다.
   또는 아래 API 로 직접 UI 를 만든다.

```csharp
using Magnetic.SteamLobby;

var sm = ServerManager.Instance;

// 호스트
sm.CreateSession(myPreset, new SessionCreateOptions { lobbyName = "같이 하실 분", password = "1234" });
sm.StartGame();          // 최소 인원·준비 조건을 만족하면 호스트 PC 에서 리슨 서버 시작
sm.EndGame();            // 모두 로비로 복귀 (로비 유지)

// 참가자
sm.SearchSessions();     // 결과: sm.OnSessionListUpdated
sm.JoinSession(lobbyId, "1234");
sm.JoinByCode("K7P2QX");
sm.ToggleReady();

// 공통
sm.OnStateChanged += state => { /* Idle / InLobby / WaitingApproval / Starting / InGame / Ending */ };
sm.OnSessionError += msg => ShowToast(msg);   // 한국어 메시지
sm.OnGameStarted += isHost => { };
sm.OnGameEnded += (reason, msg) => { };
sm.LeaveSession();
```

---

## 동작 구조

```
[호스트 PC]                                   [참가자 PC]
CreateSession(preset)
 └ Steam 로비 생성 + 메타데이터 기록 ───────►  SearchSessions / JoinByCode / 초대
                                              JoinSession → 로비 입장
                    ◄─────── 멤버 데이터 ready=1 ─ ToggleReady
StartGame()
 ├ 인원(min~max)·준비·비밀번호 확인
 ├ NetworkManager.StartHost()  ← 리슨 서버
 │   (SteamNetworkingSocketsTransport: P2P 리슨 소켓)
 └ 로비 데이터 state=ingame, game_server=내 SteamID,
   roster=시작 멤버 목록 ──────────────────►  로비 데이터 변경 감지
                                              transport.ConnectToSteamId = 호스트
                    ◄──────── Steam P2P 접속 ── NetworkManager.StartClient()
 ConnectionApproval: 로비 멤버 + (명단 or 승인) 인 SteamID 만 허용
 (게임 씬이 지정되어 있으면 NetworkSceneManager 로 모두 이동)
```

### 상태 (`SessionState`)

| 상태 | 의미 |
|---|---|
| `Offline` | Steam 사용 불가 (Steam 미실행, Steamworks.NET 없음, 지원하지 않는 플랫폼) |
| `Idle` | 로비 밖 |
| `InLobby` | 로비에서 대기 |
| `WaitingApproval` | 진행 중인 게임에 난입 요청, 호스트 승인 대기 |
| `Starting` | 리슨 서버 시작 / 호스트 접속 중 |
| `InGame` | 게임 진행 중 |
| `Ending` | 종료 처리 중 |

### 게임 종료 규칙

| 상황 | 결과 (`GameEndReason`) |
|---|---|
| 호스트가 `EndGame()` | 모두 로비로 복귀, 로비 유지 → 다시 시작 가능 (`HostEnded`) |
| 호스트가 나가거나 연결이 끊김 | 모든 참가자 게임 종료 + 로비에서 나감 (`HostLeft`) |
| 참가자가 `LeaveSession()` | 그 참가자만 종료 (`LocalLeft`) |
| 호스트 접속 실패 / 연결 끊김 | `ConnectionFailed` |
| 호스트가 `Kick()` | `Kicked` |

게임 씬에 있었다면 종료 후 **복귀 씬**(`ServerManager.Return Scene Name`, 비우면 게임을 시작한 씬)을 불러온다.

### 난입 (게임 시작 후 참가)

프리셋의 `Allow Late Join` 이 켜져 있으면 게임 중에도 로비가 열려 있다.

1. 새 플레이어가 로비 입장 → 자동으로 `join_req=1` 기록, 상태 `WaitingApproval`
2. 호스트: `OnJoinRequestsChanged` / `PendingJoinRequests` 로 대기열 확인
3. `ApproveJoin(steamId)` → 해당 플레이어가 리슨 서버에 접속 (NGO 가 현재 씬·오브젝트를 동기화)
   `RejectJoin(steamId)` → 해당 플레이어는 로비에서 나감

게임 시작 명단(roster)에 있던 플레이어는 튕겨도 로비에 다시 들어오면 승인 없이 재접속한다.

### 비밀번호 / 참가 코드

- **참가 코드**: 로비를 만들 때 6자리 코드(헷갈리는 0/O, 1/I 제외)가 자동 생성된다. `sm.JoinCode`. 공개 범위가 `Public` 인 로비만 코드로 찾을 수 있다.
- **목록 숨김**: `SessionCreateOptions.unlisted = true` 면 로비 목록에는 안 나오고 코드·초대·ID 로만 참가.
- **비밀번호**: 비밀번호는 **호스트 PC 메모리에만** 있고 로비에는 솔트만 공개된다. 참가자는 `SHA256(솔트:비밀번호:내 SteamID)` 를 멤버 데이터로 제출하고, 호스트가 검증해 틀리거나 제한 시간(기본 30초) 안에 제출하지 않으면 내보낸다. 리슨 서버 접속도 로비 멤버만 허용되므로 비밀번호를 통과하지 못한 사람은 게임에 들어올 수 없다.
  - 친구 초대로 들어와도 비밀번호를 물어본다 (`OnPasswordRequired` → `SubmitPassword`).
  - 게임 시작 전 호스트가 나가 다른 사람이 호스트가 되면 비밀번호를 검증할 수 없으므로 새 참가를 막는다.

---

## 구성 요소

| 클래스 | 역할 |
|---|---|
| `SteamManager` | Steam API 초기화·콜백 펌프·종료. `DontDestroyOnLoad`, 중복 오브젝트 자동 제거 |
| `SteamLobbyService` | 범용 Steam 로비 래퍼. 생성/검색/참가, 로비·멤버 데이터, 채팅, 시스템 메시지, 초대 |
| `ServerManager` | 세션 규칙 (인원·준비·시작·난입·비밀번호·종료) + NGO 호스트/클라이언트 시작 |
| `SessionPreset` | 세션 규칙 ScriptableObject |
| `SteamNetworkingSocketsTransport` | NGO `NetworkTransport` 구현 (Steam P2P) |
| `SteamLog` | 한국어 로그 (수준·분류, 링버퍼, `OnLogged` 이벤트) |

### 접속 검사 확장

```csharp
ServerManager.Instance.AdditionalApprovalCheck = (steamId, request) =>
{
    if (IsBanned(steamId)) return "차단된 플레이어입니다.";
    return null; // 허용
};
```

### 로비 메타데이터 키

`LobbyKeys` (로비 서비스): `mslb_game`, `name`, `version`, `host`, `host_name`, 멤버 `ready`
`SessionKeys` (세션): `mode`, `min`, `req_ready`, `late_join`, `state`, `joinable`, `listed`, `code`, `pw_salt`, `game_server`, `roster`, `approved`, 멤버 `join_req`, `pw_proof`

검색은 `mslb_game`(=Game Id)·`version`·`joinable=1`·`listed=1`(+`mode`) 필터를 쓴다. 다른 게임이나 다른 버전 로비는 검색되지 않고, 초대/ID 로 들어와도 입장 시 차단된다.

---

## 샘플

Package Manager > Magnetic Steam Lobby > Samples 에서 가져온다.

- **Korean Log Console**: `SteamLogConsole`(F1 로 여는 게임 내 로그 창, 수준·분류 필터, 복사) + `SteamLogFileWriter`(UTF-8 로그 파일, 오래된 파일 자동 삭제)
- **Lobby UI**: `SessionLobbyController` + `LobbySceneBuilder` 에디터 도구. 로비 목록 / 만들기(비밀번호·프리셋·목록 숨김·친구만) / 코드 참가 / 멤버·준비 / 시작·종료 / 초대 / 채팅 / 비밀번호 입력 창 / 난입 승인 패널 / 승인 대기 창

---

## 테스트할 때

- Steam 클라이언트가 실행·로그인되어 있어야 한다.
- 같은 Steam 계정으로 두 클라이언트를 동시에 접속할 수 없다. **멀티 테스트는 PC 2대(또는 VM) + Steam 계정 2개**가 필요하다.
- 혼자 흐름만 확인하려면 최소 인원 1명 프리셋으로 로비 생성 → 시작 → 종료를 해 볼 수 있다.
- 에디터에서는 Steam 오버레이가 동작하지 않아 초대 창이 안 뜰 수 있다 (빌드에서는 정상).
- 로그는 Unity 콘솔에서 `[스팀로비]` 로 검색.

## 알려진 한계

- 호스트 마이그레이션(게임 중 호스트 교체)은 지원하지 않는다. 호스트가 나가면 게임이 끝난다.
- 비밀번호는 캐주얼 용도다. 로비 솔트가 공개되어 있으므로 짧은 비밀번호는 오프라인 대입 공격에 약하다.
- 참가 코드 검색은 Steam 의 `Public` 로비에서만 동작한다.
