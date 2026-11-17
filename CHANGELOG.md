# Changelog

## [0.1.0] - 2026-11-18

### 추가
- `ServerManager`: 세션 프리셋(최소/최대 인원, 준비 필요, 난입 허용, 게임 씬) 기반 로비 생성·검색·참가, 호스트 PC 리슨 서버 시작, 참가자 자동 접속, 게임 종료·복귀
- 난입 승인 대기열 (`PendingJoinRequests`, `ApproveJoin`, `RejectJoin`)
- 비밀번호 로비 (호스트 검증, 제한 시간), 6자리 참가 코드, 목록 숨김 로비, 강퇴
- `SteamLobbyService`: 범용 로비 래퍼 (로비·멤버 데이터, 시스템 메시지, 검색 콜백, 호스트 변경 처리)
- `SteamNetworkingSocketsTransport`: Netcode for GameObjects 용 Steam P2P 트랜스포트
- `SteamLog`: 한국어 로그 시스템
- 에디터: Steamworks.NET 설치 도우미, `steam_appid.txt` 생성, 세션 서비스 오브젝트 생성 메뉴
- 샘플: Korean Log Console, Lobby UI (씬 자동 구성 도구 포함)
