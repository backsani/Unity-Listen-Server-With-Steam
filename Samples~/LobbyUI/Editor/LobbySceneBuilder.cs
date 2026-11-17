using System;
using System.IO;
using Magnetic.SteamLobby.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using Object = UnityEngine.Object;

namespace Magnetic.SteamLobby.Samples.EditorTools
{
    /// <summary>
    /// 현재 씬을 Steam 로비 씬으로 구성한다. 메뉴 [Tools > Magnetic Steam Lobby > Build Lobby Scene]
    /// - 세션 서비스 오브젝트(SteamManager / SteamLobbyService / NetworkManager / 트랜스포트 / ServerManager)가 없으면 생성
    /// - 기본 세션 프리셋 에셋 생성 (Assets/Settings/SteamLobby/DefaultSessionPreset.asset)
    /// - SteamLobbyCanvas / EventSystem / 리스트 항목 프리팹 생성 후 SessionLobbyController 참조 자동 연결
    /// - 스크립트가 빠진(Missing Script) 컴포넌트 정리 후 씬 저장
    /// 다시 실행하면 UI 가 처음 상태로 재생성된다.
    ///
    /// 배치 모드:
    ///   Unity -batchmode -quit -projectPath . -executeMethod Magnetic.SteamLobby.Samples.EditorTools.LobbySceneBuilder.BuildFromCommandLine
    ///         -lobbyScene Assets/Scenes/Lobby.unity [-backScene Hub] [-gameId MyGame] [-gameVersion 0.1.0] [-removeObjects OldCanvas,OldObject]
    /// </summary>
    public static class LobbySceneBuilder
    {
        public const string DefaultPresetPath = "Assets/Settings/SteamLobby/DefaultSessionPreset.asset";
        private const string PrefabFolder = "Assets/Prefabs/SteamLobbyUI";
        private const string CanvasName = "SteamLobbyCanvas";

        private static readonly Color BgColor = new Color(0.08f, 0.09f, 0.11f, 1f);
        private static readonly Color HeaderColor = new Color(0.12f, 0.13f, 0.15f, 1f);
        private static readonly Color PanelColor = new Color(0.15f, 0.16f, 0.19f, 0.97f);
        private static readonly Color ItemColor = new Color(0.21f, 0.23f, 0.26f, 1f);
        private static readonly Color InputColor = new Color(0.1f, 0.1f, 0.12f, 1f);
        private static readonly Color TextColor = new Color(0.9f, 0.88f, 0.82f, 1f);
        private static readonly Color SubTextColor = new Color(0.65f, 0.63f, 0.58f, 1f);
        private static readonly Color AccentColor = new Color(0.75f, 0.72f, 0.62f, 1f);
        private static readonly Color GreenColor = new Color(0.4f, 0.6f, 0.35f, 1f);
        private static readonly Color BlueColor = new Color(0.3f, 0.48f, 0.75f, 1f);
        private static readonly Color RedColor = new Color(0.68f, 0.36f, 0.32f, 1f);
        private static readonly Color KhakiColor = new Color(0.62f, 0.56f, 0.38f, 1f);
        private static readonly Color DimColor = new Color(0f, 0f, 0f, 0.7f);

        private static Font font;
        private static Sprite uiSprite, bgSprite, inputSprite, checkSprite, dropdownSprite, maskSprite;

        [MenuItem("Tools/Magnetic Steam Lobby/Build Lobby Scene (현재 씬)", false, 20)]
        public static void BuildFromMenu()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Build Lobby Scene", "플레이 모드에서는 실행할 수 없습니다.", "확인");
                return;
            }
            Scene scene = SceneManager.GetActiveScene();
            if (!EditorUtility.DisplayDialog("Build Lobby Scene",
                    $"현재 씬 '{scene.name}' 에 로비 UI 와 세션 서비스를 구성합니다.\n기존 {CanvasName} 는 새로 만듭니다. 계속할까요?",
                    "구성", "취소"))
                return;

            Build(scene, backSceneName: "", gameId: null, gameVersion: null);
        }

        /// <summary>배치 모드 진입점</summary>
        public static void BuildFromCommandLine()
        {
            string scenePath = GetArg("-lobbyScene");
            if (string.IsNullOrEmpty(scenePath)) throw new ArgumentException("-lobbyScene <씬 경로> 인자가 필요합니다.");
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            // 예전 UI 등 지울 루트 오브젝트 (쉼표 구분)
            string remove = GetArg("-removeObjects");
            if (!string.IsNullOrEmpty(remove))
            {
                foreach (string name in remove.Split(','))
                {
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        if (root.name != name.Trim()) continue;
                        Debug.Log($"[스팀로비] 루트 오브젝트 '{root.name}' 를 삭제합니다.");
                        Object.DestroyImmediate(root);
                    }
                }
            }

            Build(scene, GetArg("-backScene") ?? "", GetArg("-gameId"), GetArg("-gameVersion"));
        }

        public static void Build(Scene scene, string backSceneName, string gameId, string gameVersion)
        {
            LoadResources();
            CleanupMissingScripts(scene);

            SessionPreset preset = SteamSessionServicesMenu.LoadOrCreatePreset(DefaultPresetPath);
            EnsureServices(preset, gameId, gameVersion);

            GameObject existing = GameObject.Find(CanvasName);
            if (existing != null) Object.DestroyImmediate(existing);

            var r = new Refs
            {
                lobbyListItemPrefab = CreateLobbyItemPrefab(),
                memberItemPrefab = CreateMemberItemPrefab(),
                joinRequestItemPrefab = CreateJoinRequestItemPrefab(),
            };

            GameObject canvas = BuildCanvas(r);
            EnsureEventSystem();
            WireController(r, preset, backSceneName);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = canvas;
            Debug.Log($"[스팀로비] '{scene.path}' 로비 씬 구성 완료. 씬을 저장했습니다.");
        }

        private static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        // =====================================================================
        // 씬 정리 / 서비스
        // =====================================================================

        /// <summary>스크립트가 삭제된 컴포넌트를 제거하고, 그 결과 빈 루트 오브젝트는 삭제</summary>
        private static void CleanupMissingScripts(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                int removed = 0;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (removed == 0) continue;

                bool empty = root.transform.childCount == 0 && root.GetComponents<Component>().Length == 1;
                Debug.Log($"[스팀로비] '{root.name}' 에서 빠진 스크립트 {removed}개를 정리했습니다{(empty ? " (빈 오브젝트 삭제)" : "")}.");
                if (empty) Object.DestroyImmediate(root);
            }
        }

        private static void EnsureServices(SessionPreset preset, string gameId, string gameVersion)
        {
            // 예전 구성(SteamManager 는 있는데 ServerManager 가 없음)은 교체
            foreach (var sm in Object.FindObjectsByType<SteamManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (sm.GetComponent<ServerManager>() != null) continue;
                Debug.Log($"[스팀로비] ServerManager 가 없는 예전 Steam 오브젝트 '{sm.gameObject.name}' 를 교체합니다.");
                Object.DestroyImmediate(sm.gameObject);
            }

            var server = Object.FindAnyObjectByType<ServerManager>(FindObjectsInactive.Include);
            if (server == null)
            {
                server = SteamSessionServicesMenu.CreateServices(preset, gameId, gameVersion).GetComponent<ServerManager>();
                Debug.Log("[스팀로비] 세션 서비스 오브젝트를 만들었습니다.");
            }
            else
            {
                var so = new SerializedObject(server);
                SerializedProperty p = so.FindProperty("defaultPreset");
                if (p.objectReferenceValue == null) { p.objectReferenceValue = preset; so.ApplyModifiedPropertiesWithoutUndo(); }
            }

            AttachKoreanLogIfImported(server.gameObject);
        }

        /// <summary>Korean Log Console 샘플을 가져온 상태면 서비스 오브젝트에 로그 콘솔·파일 기록을 붙인다.</summary>
        private static void AttachKoreanLogIfImported(GameObject target)
        {
            foreach (string typeName in new[] { "SteamLogConsole", "SteamLogFileWriter" })
            {
                Type type = Type.GetType($"Magnetic.SteamLobby.Samples.{typeName}, Magnetic.SteamLobby.Samples.KoreanLog");
                if (type == null || target.GetComponent(type) != null) continue;
                target.AddComponent(type);
                Debug.Log($"[스팀로비] {typeName} 를 '{target.name}' 에 붙였습니다.");
            }
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null) return;
#if ENABLE_INPUT_SYSTEM
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            es.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
#else
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
#endif
        }

        // =====================================================================
        // 레이아웃
        // =====================================================================
        private class Refs
        {
            public GameObject browserRoot, roomRoot, memberItemPrefab, joinRequestItemPrefab, joinRequestPanel, passwordPanel, waitingPanel;
            public InputField lobbyNameInput, createPasswordInput, joinCodeInput, joinPasswordInput, chatInput, passwordPanelInput;
            public Dropdown presetDropdown;
            public Toggle unlistedToggle, friendsOnlyToggle;
            public Button createButton, joinCodeButton, refreshButton, copyCodeButton, readyButton, startButton, endGameButton,
                inviteButton, leaveButton, chatSendButton, backButton, lobbyListItemPrefab,
                passwordSubmitButton, passwordCancelButton, waitingCancelButton;
            public Text refreshButtonText, emptyListText, roomTitleText, roomInfoText, roomRuleText, readyButtonText, leaveButtonText,
                startHintText, chatLogText, statusText, playerNameText, waitingText;
            public Transform lobbyListContent, memberListContent, joinRequestContent;
            public ScrollRect chatScroll;
        }

        private static GameObject BuildCanvas(Refs r)
        {
            GameObject canvasGo = new GameObject(CanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.layer = LayerMask.NameToLayer("UI");
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            Transform root = canvasGo.transform;

            Stretch(Img(UI("Background", root), BgColor, null).rectTransform);

            // ---- 상단 바 ----
            RectTransform header = Img(UI("Header", root), HeaderColor, null).rectTransform;
            TopBar(header, 110);
            Text title = Txt(header, "Title", "멀티플레이 로비", 48, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(title.rectTransform, new Vector2(0, 0), new Vector2(0.5f, 1), new Vector2(50, 0), Vector2.zero);
            r.playerNameText = Txt(header, "PlayerName", "플레이어", 28, TextColor, TextAnchor.MiddleRight);
            Rect(r.playerNameText.rectTransform, new Vector2(0.4f, 0), new Vector2(1, 1), Vector2.zero, new Vector2(-300, 0));
            r.backButton = Btn(header, "BackButton", "← 뒤로", ItemColor, 28, out _);
            Rect(r.backButton.GetComponent<RectTransform>(), new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-270, -35), new Vector2(-40, 35));

            // ---- 하단 상태 바 ----
            RectTransform status = Img(UI("StatusBar", root), HeaderColor, null).rectTransform;
            Rect(status, new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, 60));
            r.statusText = Txt(status, "StatusText", "", 26, TextColor, TextAnchor.MiddleLeft);
            Rect(r.statusText.rectTransform, Vector2.zero, Vector2.one, new Vector2(50, 0), new Vector2(-50, 0));

            BuildBrowser(root, r);
            BuildRoom(root, r);
            BuildOverlays(root, r);
            r.roomRoot.SetActive(false);
            r.joinRequestPanel.SetActive(false);
            r.passwordPanel.SetActive(false);
            r.waitingPanel.SetActive(false);
            return canvasGo;
        }

        private static void BuildBrowser(Transform root, Refs r)
        {
            RectTransform browser = UI("BrowserRoot", root);
            Rect(browser, Vector2.zero, Vector2.one, new Vector2(40, 90), new Vector2(-40, -140));
            r.browserRoot = browser.gameObject;

            // ---- 왼쪽: 만들기 / 코드 참가 ----
            RectTransform left = Img(UI("CreatePanel", browser), PanelColor, uiSprite).rectTransform;
            Rect(left, new Vector2(0, 0), new Vector2(0, 1), Vector2.zero, new Vector2(620, 0));
            VLayout(left.gameObject, 30, 14);

            Heading(left, "새 로비 만들기");
            r.lobbyNameInput = MakeInput(left, "LobbyNameInput", "로비 이름 (비우면 '닉네임의 로비')", 28);
            r.lobbyNameInput.characterLimit = 40;
            Pref(r.lobbyNameInput.gameObject, 64);
            r.createPasswordInput = MakeInput(left, "CreatePasswordInput", "비밀번호 (선택)", 28);
            r.createPasswordInput.contentType = InputField.ContentType.Password;
            r.createPasswordInput.characterLimit = 32;
            Pref(r.createPasswordInput.gameObject, 64);
            r.presetDropdown = MakeDropdown(left, "PresetDropdown");
            Pref(r.presetDropdown.gameObject, 64);
            r.unlistedToggle = MakeToggle(left, "UnlistedToggle", "목록에 숨기기 (코드·초대로만 참가)");
            Pref(r.unlistedToggle.gameObject, 48);
            r.friendsOnlyToggle = MakeToggle(left, "FriendsOnlyToggle", "친구만 검색 가능");
            Pref(r.friendsOnlyToggle.gameObject, 48);
            r.createButton = Btn(left, "CreateButton", "로비 만들기", GreenColor, 36, out _);
            Pref(r.createButton.gameObject, 80);

            Spacer(left, 12);
            Heading(left, "코드 / ID 로 참가");
            RectTransform joinRow = UI("JoinRow", left);
            Pref(joinRow.gameObject, 64);
            var hl = joinRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 14; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
            r.joinCodeInput = MakeInput(joinRow, "JoinCodeInput", "참가 코드 6자리 또는 로비 ID", 28);
            r.joinCodeInput.characterLimit = 24;
            r.joinCodeInput.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            r.joinCodeButton = Btn(joinRow, "JoinCodeButton", "참가", BlueColor, 30, out _);
            r.joinCodeButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 150;
            r.joinPasswordInput = MakeInput(left, "JoinPasswordInput", "비밀번호 (필요한 경우)", 28);
            r.joinPasswordInput.contentType = InputField.ContentType.Password;
            Pref(r.joinPasswordInput.gameObject, 64);

            // ---- 오른쪽: 로비 목록 ----
            RectTransform right = Img(UI("LobbyListPanel", browser), PanelColor, uiSprite).rectTransform;
            Rect(right, new Vector2(0, 0), new Vector2(1, 1), new Vector2(650, 0), Vector2.zero);

            Text listTitle = Txt(right, "Heading", "로비 목록", 38, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(listTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(36, -100), new Vector2(-300, -20));
            r.refreshButton = Btn(right, "RefreshButton", "새로고침", ItemColor, 30, out r.refreshButtonText);
            Rect(r.refreshButton.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-276, -96), new Vector2(-36, -24));

            ScrollRect list = Scroll(right, "LobbyList", out RectTransform content);
            Rect(list.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(30, 30), new Vector2(-30, -120));
            r.lobbyListContent = content;

            r.emptyListText = Txt(right, "EmptyText", "열린 로비가 없습니다.", 32, SubTextColor, TextAnchor.MiddleCenter);
            Rect(r.emptyListText.rectTransform, Vector2.zero, Vector2.one, new Vector2(30, 30), new Vector2(-30, -120));
        }

        private static void BuildRoom(Transform root, Refs r)
        {
            RectTransform room = UI("RoomRoot", root);
            Rect(room, Vector2.zero, Vector2.one, new Vector2(40, 90), new Vector2(-40, -140));
            r.roomRoot = room.gameObject;

            // ---- 상단: 로비 정보 ----
            RectTransform info = Img(UI("RoomInfo", room), PanelColor, uiSprite).rectTransform;
            Rect(info, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -150), Vector2.zero);
            r.roomTitleText = Txt(info, "RoomTitle", "로비", 42, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(r.roomTitleText.rectTransform, new Vector2(0, 0.45f), new Vector2(0.6f, 1), new Vector2(36, 0), new Vector2(0, -10));
            r.roomRuleText = Txt(info, "RoomRule", "인원", 26, SubTextColor, TextAnchor.MiddleLeft);
            Rect(r.roomRuleText.rectTransform, new Vector2(0, 0), new Vector2(0.7f, 0.45f), new Vector2(36, 10), Vector2.zero);
            r.roomInfoText = Txt(info, "RoomCode", "참가 코드", 28, TextColor, TextAnchor.MiddleRight);
            Rect(r.roomInfoText.rectTransform, new Vector2(0.45f, 0.5f), new Vector2(1, 1), Vector2.zero, new Vector2(-36, -10));
            r.copyCodeButton = Btn(info, "CopyCodeButton", "코드 복사", ItemColor, 26, out _);
            Rect(r.copyCodeButton.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-236, 14), new Vector2(-36, 66));

            // ---- 하단: 액션 바 ----
            RectTransform actions = UI("ActionBar", room);
            Rect(actions, new Vector2(0, 0), new Vector2(1, 0), Vector2.zero, new Vector2(0, 100));
            var hl = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 20; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
            r.readyButton = Btn(actions, "ReadyButton", "준비 완료", GreenColor, 34, out r.readyButtonText);
            r.readyButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 280;
            r.startButton = Btn(actions, "StartButton", "게임 시작", KhakiColor, 34, out _);
            r.startButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 280;
            r.endGameButton = Btn(actions, "EndGameButton", "게임 종료", KhakiColor, 34, out _);
            r.endGameButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 280;
            r.inviteButton = Btn(actions, "InviteButton", "친구 초대", BlueColor, 30, out _);
            r.inviteButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 240;
            r.startHintText = Txt(actions, "StartHint", "", 26, SubTextColor, TextAnchor.MiddleLeft);
            r.startHintText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            r.leaveButton = Btn(actions, "LeaveButton", "로비 나가기", RedColor, 30, out r.leaveButtonText);
            r.leaveButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 260;

            // ---- 왼쪽: 멤버 ----
            RectTransform members = Img(UI("MemberPanel", room), PanelColor, uiSprite).rectTransform;
            Rect(members, new Vector2(0, 0), new Vector2(0.55f, 1), new Vector2(0, 125), new Vector2(-15, -175));
            Text memTitle = Txt(members, "Heading", "플레이어", 36, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(memTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(36, -90), new Vector2(-36, -16));
            ScrollRect memScroll = Scroll(members, "MemberList", out RectTransform memContent);
            Rect(memScroll.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(30, 30), new Vector2(-30, -100));
            r.memberListContent = memContent;

            // ---- 오른쪽: 채팅 ----
            RectTransform chat = Img(UI("ChatPanel", room), PanelColor, uiSprite).rectTransform;
            Rect(chat, new Vector2(0.55f, 0), new Vector2(1, 1), new Vector2(15, 125), new Vector2(0, -175));
            Text chatTitle = Txt(chat, "Heading", "채팅", 36, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(chatTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(36, -90), new Vector2(-36, -16));

            r.chatScroll = Scroll(chat, "ChatLog", out RectTransform chatContent);
            Rect(r.chatScroll.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(30, 110), new Vector2(-30, -100));
            r.chatLogText = Txt(chatContent, "ChatText", "", 28, TextColor, TextAnchor.UpperLeft);
            r.chatLogText.supportRichText = true;
            r.chatLogText.horizontalOverflow = HorizontalWrapMode.Wrap;
            r.chatLogText.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform chatRow = UI("ChatInputRow", chat);
            Rect(chatRow, new Vector2(0, 0), new Vector2(1, 0), new Vector2(30, 30), new Vector2(-30, 94));
            var chl = chatRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            chl.spacing = 14; chl.childControlWidth = true; chl.childControlHeight = true; chl.childForceExpandWidth = false; chl.childForceExpandHeight = true;
            r.chatInput = MakeInput(chatRow, "ChatInput", "메시지 입력 후 Enter", 28);
            r.chatInput.characterLimit = 200;
            r.chatInput.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            r.chatSendButton = Btn(chatRow, "ChatSendButton", "전송", ItemColor, 30, out _);
            r.chatSendButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 140;

            // ---- 난입 승인 패널 (호스트, 게임 중) : 채팅 위에 겹쳐 표시 ----
            RectTransform req = Img(UI("JoinRequestPanel", room), new Color(0.18f, 0.17f, 0.13f, 0.98f), uiSprite).rectTransform;
            Rect(req, new Vector2(0.55f, 1), new Vector2(1, 1), new Vector2(15, -555), new Vector2(0, -175));
            Text reqTitle = Txt(req, "Heading", "참가 요청 (승인 시 진행 중인 게임에 참가)", 30, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(reqTitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(30, -70), new Vector2(-30, -10));
            ScrollRect reqScroll = Scroll(req, "RequestList", out RectTransform reqContent);
            Rect(reqScroll.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(24, 20), new Vector2(-24, -76));
            r.joinRequestPanel = req.gameObject;
            r.joinRequestContent = reqContent;
        }

        private static void BuildOverlays(Transform root, Refs r)
        {
            // ---- 비밀번호 입력 ----
            RectTransform pw = Img(UI("PasswordPanel", root), DimColor, null).rectTransform;
            Stretch(pw);
            RectTransform pwBox = Img(UI("Box", pw), PanelColor, uiSprite).rectTransform;
            Rect(pwBox, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-380, -190), new Vector2(380, 190));
            VLayout(pwBox.gameObject, 36, 20);
            Heading(pwBox, "비밀번호가 필요한 로비입니다");
            r.passwordPanelInput = MakeInput(pwBox, "PasswordInput", "비밀번호", 30);
            r.passwordPanelInput.contentType = InputField.ContentType.Password;
            Pref(r.passwordPanelInput.gameObject, 70);
            RectTransform pwRow = UI("Buttons", pwBox);
            Pref(pwRow.gameObject, 80);
            var pwl = pwRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            pwl.spacing = 20; pwl.childControlWidth = true; pwl.childControlHeight = true; pwl.childForceExpandWidth = true; pwl.childForceExpandHeight = true;
            r.passwordSubmitButton = Btn(pwRow, "SubmitButton", "확인", GreenColor, 32, out _);
            r.passwordCancelButton = Btn(pwRow, "CancelButton", "나가기", RedColor, 32, out _);
            r.passwordPanel = pw.gameObject;

            // ---- 난입 승인 대기 ----
            RectTransform wait = Img(UI("WaitingPanel", root), DimColor, null).rectTransform;
            Stretch(wait);
            RectTransform waitBox = Img(UI("Box", wait), PanelColor, uiSprite).rectTransform;
            Rect(waitBox, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-380, -170), new Vector2(380, 170));
            VLayout(waitBox.gameObject, 36, 20);
            Heading(waitBox, "참가 승인 대기 중");
            r.waitingText = Txt(waitBox, "Message", "호스트가 승인하면 바로 접속합니다.", 28, TextColor, TextAnchor.MiddleCenter);
            Pref(r.waitingText.gameObject, 90);
            r.waitingCancelButton = Btn(waitBox, "CancelButton", "참가 취소", RedColor, 30, out _);
            Pref(r.waitingCancelButton.gameObject, 76);
            r.waitingPanel = wait.gameObject;
        }

        // =====================================================================
        // 프리팹
        // =====================================================================
        private static Button CreateLobbyItemPrefab()
        {
            GameObject go = new GameObject("LobbyListItem", typeof(RectTransform));
            Image bg = Img(go.GetComponent<RectTransform>(), ItemColor, uiSprite);
            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            SetButtonColors(btn);
            Pref(go, 104);

            Text name = Txt(go.transform, "Name", "로비 이름", 32, TextColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Rect(name.rectTransform, Vector2.zero, new Vector2(0.55f, 1), new Vector2(32, 0), Vector2.zero);
            Text info = Txt(go.transform, "Info", "1 / 4명   호스트", 26, SubTextColor, TextAnchor.MiddleRight);
            Rect(info.rectTransform, new Vector2(0.45f, 0), Vector2.one, Vector2.zero, new Vector2(-32, 0));

            return SavePrefab(go, "LobbyListItem").GetComponent<Button>();
        }

        private static GameObject CreateMemberItemPrefab()
        {
            GameObject go = new GameObject("LobbyMemberItem", typeof(RectTransform));
            Img(go.GetComponent<RectTransform>(), ItemColor, uiSprite).raycastTarget = false;
            Pref(go, 90);

            Text name = Txt(go.transform, "Name", "플레이어", 32, TextColor, TextAnchor.MiddleLeft);
            Rect(name.rectTransform, Vector2.zero, new Vector2(0.7f, 1), new Vector2(32, 0), Vector2.zero);
            Text status = Txt(go.transform, "Status", "대기 중", 28, AccentColor, TextAnchor.MiddleRight, FontStyle.Bold);
            Rect(status.rectTransform, new Vector2(0.65f, 0), Vector2.one, Vector2.zero, new Vector2(-32, 0));

            return SavePrefab(go, "LobbyMemberItem");
        }

        private static GameObject CreateJoinRequestItemPrefab()
        {
            GameObject go = new GameObject("JoinRequestItem", typeof(RectTransform));
            Img(go.GetComponent<RectTransform>(), ItemColor, uiSprite).raycastTarget = false;
            Pref(go, 80);

            Text name = Txt(go.transform, "Name", "플레이어", 30, TextColor, TextAnchor.MiddleLeft);
            Rect(name.rectTransform, Vector2.zero, new Vector2(0.55f, 1), new Vector2(28, 0), Vector2.zero);
            Button approve = Btn(go.transform, "Approve", "승인", GreenColor, 26, out _);
            Rect(approve.GetComponent<RectTransform>(), new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-330, -28), new Vector2(-180, 28));
            Button reject = Btn(go.transform, "Reject", "거절", RedColor, 26, out _);
            Rect(reject.GetComponent<RectTransform>(), new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-166, -28), new Vector2(-16, 28));

            return SavePrefab(go, "JoinRequestItem");
        }

        private static GameObject SavePrefab(GameObject go, string name)
        {
            if (!AssetDatabase.IsValidFolder(PrefabFolder))
            {
                Directory.CreateDirectory(PrefabFolder);
                AssetDatabase.Refresh();
            }
            string path = $"{PrefabFolder}/{name}.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        // =====================================================================
        // 컨트롤러 연결
        // =====================================================================
        private static void WireController(Refs r, SessionPreset preset, string backSceneName)
        {
            var controller = Object.FindAnyObjectByType<SessionLobbyController>(FindObjectsInactive.Include);
            if (controller == null)
            {
                var go = new GameObject("SessionLobbyController");
                controller = go.AddComponent<SessionLobbyController>();
            }

            var so = new SerializedObject(controller);
            void Set(string prop, Object value)
            {
                SerializedProperty p = so.FindProperty(prop);
                if (p == null) { Debug.LogWarning($"[스팀로비] SessionLobbyController 에 '{prop}' 필드가 없습니다."); return; }
                p.objectReferenceValue = value;
            }

            SerializedProperty presetsProp = so.FindProperty("presets");
            presetsProp.arraySize = 1;
            presetsProp.GetArrayElementAtIndex(0).objectReferenceValue = preset;
            so.FindProperty("backSceneName").stringValue = backSceneName ?? "";

            Set("browserRoot", r.browserRoot);
            Set("lobbyNameInput", r.lobbyNameInput);
            Set("createPasswordInput", r.createPasswordInput);
            Set("presetDropdown", r.presetDropdown);
            Set("unlistedToggle", r.unlistedToggle);
            Set("friendsOnlyToggle", r.friendsOnlyToggle);
            Set("createButton", r.createButton);
            Set("joinCodeInput", r.joinCodeInput);
            Set("joinPasswordInput", r.joinPasswordInput);
            Set("joinCodeButton", r.joinCodeButton);
            Set("refreshButton", r.refreshButton);
            Set("refreshButtonText", r.refreshButtonText);
            Set("lobbyListContent", r.lobbyListContent);
            Set("lobbyListItemPrefab", r.lobbyListItemPrefab);
            Set("emptyListText", r.emptyListText);

            Set("roomRoot", r.roomRoot);
            Set("roomTitleText", r.roomTitleText);
            Set("roomInfoText", r.roomInfoText);
            Set("roomRuleText", r.roomRuleText);
            Set("copyCodeButton", r.copyCodeButton);
            Set("memberListContent", r.memberListContent);
            Set("memberItemPrefab", r.memberItemPrefab);
            Set("readyButton", r.readyButton);
            Set("readyButtonText", r.readyButtonText);
            Set("startButton", r.startButton);
            Set("endGameButton", r.endGameButton);
            Set("inviteButton", r.inviteButton);
            Set("leaveButton", r.leaveButton);
            Set("leaveButtonText", r.leaveButtonText);
            Set("startHintText", r.startHintText);
            Set("chatInput", r.chatInput);
            Set("chatSendButton", r.chatSendButton);
            Set("chatLogText", r.chatLogText);
            Set("chatScroll", r.chatScroll);

            Set("joinRequestPanel", r.joinRequestPanel);
            Set("joinRequestContent", r.joinRequestContent);
            Set("joinRequestItemPrefab", r.joinRequestItemPrefab);

            Set("passwordPanel", r.passwordPanel);
            Set("passwordPanelInput", r.passwordPanelInput);
            Set("passwordSubmitButton", r.passwordSubmitButton);
            Set("passwordCancelButton", r.passwordCancelButton);
            Set("waitingPanel", r.waitingPanel);
            Set("waitingText", r.waitingText);
            Set("waitingCancelButton", r.waitingCancelButton);

            Set("statusText", r.statusText);
            Set("playerNameText", r.playerNameText);
            Set("backButton", r.backButton);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(controller);
        }

        // =====================================================================
        // 위젯 헬퍼
        // =====================================================================
        private static void LoadResources()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            uiSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            bgSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
            inputSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd");
            checkSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd");
            dropdownSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd");
            maskSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd");
        }

        private static RectTransform UI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        private static void Rect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        private static void Stretch(RectTransform rt) => Rect(rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        private static void TopBar(RectTransform rt, float height) =>
            Rect(rt, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -height), Vector2.zero);

        private static Image Img(RectTransform rt, Color color, Sprite sprite)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            if (sprite != null) { img.sprite = sprite; img.type = Image.Type.Sliced; }
            return img;
        }

        private static Text Txt(Transform parent, string name, string value, int size, Color color, TextAnchor align, FontStyle style = FontStyle.Normal)
        {
            RectTransform rt = UI(name, parent);
            Stretch(rt);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font;
            t.text = value;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        private static void Heading(Transform parent, string value)
        {
            Text t = Txt(parent, "Heading", value, 34, AccentColor, TextAnchor.MiddleLeft, FontStyle.Bold);
            Pref(t.gameObject, 56);
        }

        private static void Spacer(Transform parent, float height) => Pref(UI("Spacer", parent).gameObject, height);

        private static void Pref(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }

        private static void VLayout(GameObject go, int padding, float spacing)
        {
            var vl = go.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(padding, padding, padding, padding);
            vl.spacing = spacing;
            vl.childControlWidth = true;
            vl.childControlHeight = true;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;
        }

        private static void SetButtonColors(Selectable s)
        {
            ColorBlock cb = s.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.selectedColor = Color.white;
            cb.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            cb.colorMultiplier = 1.2f;
            s.colors = cb;
        }

        private static Button Btn(Transform parent, string name, string label, Color color, int fontSize, out Text labelText)
        {
            RectTransform rt = UI(name, parent);
            Image img = Img(rt, color, uiSprite);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            SetButtonColors(btn);
            labelText = Txt(rt, "Label", label, fontSize, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            return btn;
        }

        private static InputField MakeInput(Transform parent, string name, string placeholder, int fontSize)
        {
            RectTransform rt = UI(name, parent);
            Image bg = Img(rt, InputColor, inputSprite);
            var input = rt.gameObject.AddComponent<InputField>();
            input.targetGraphic = bg;

            Text text = Txt(rt, "Text", "", fontSize, TextColor, TextAnchor.MiddleLeft);
            Rect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(20, 6), new Vector2(-20, -6));
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;

            Text ph = Txt(rt, "Placeholder", placeholder, fontSize, new Color(1, 1, 1, 0.3f), TextAnchor.MiddleLeft, FontStyle.Italic);
            Rect(ph.rectTransform, Vector2.zero, Vector2.one, new Vector2(20, 6), new Vector2(-20, -6));

            input.textComponent = text;
            input.placeholder = ph;
            input.caretWidth = 3;
            input.customCaretColor = true;
            input.caretColor = AccentColor;
            return input;
        }

        private static Toggle MakeToggle(Transform parent, string name, string label)
        {
            RectTransform rt = UI(name, parent);
            var toggle = rt.gameObject.AddComponent<Toggle>();

            RectTransform box = UI("Box", rt);
            Rect(box, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, -20), new Vector2(40, 20));
            Image boxImg = Img(box, InputColor, bgSprite);

            RectTransform check = UI("Checkmark", box);
            Rect(check, Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-4, -4));
            Image checkImg = check.gameObject.AddComponent<Image>();
            checkImg.sprite = checkSprite;
            checkImg.color = AccentColor;

            Text text = Txt(rt, "Label", label, 26, TextColor, TextAnchor.MiddleLeft);
            Rect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(58, 0), Vector2.zero);
            text.raycastTarget = true; // 라벨 클릭으로도 토글

            toggle.targetGraphic = boxImg;
            toggle.graphic = checkImg;
            toggle.isOn = false;
            return toggle;
        }

        private static Dropdown MakeDropdown(Transform parent, string name)
        {
            var res = new DefaultControls.Resources
            {
                standard = uiSprite,
                background = bgSprite,
                inputField = inputSprite,
                checkmark = checkSprite,
                dropdown = dropdownSprite,
                mask = maskSprite,
            };
            GameObject go = DefaultControls.CreateDropdown(res);
            go.name = name;
            go.transform.SetParent(parent, false);
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = LayerMask.NameToLayer("UI");

            var dropdown = go.GetComponent<Dropdown>();
            go.GetComponent<Image>().color = InputColor;
            SetButtonColors(dropdown);
            foreach (Text t in go.GetComponentsInChildren<Text>(true))
            {
                t.font = font;
                t.fontSize = 26;
                t.color = TextColor;
            }
            foreach (Image img in dropdown.template.GetComponentsInChildren<Image>(true))
            {
                if (img.sprite == checkSprite) img.color = AccentColor;
                else if (img.gameObject.name == "Item Background") img.color = ItemColor;
                else if (img.gameObject.name == "Template") img.color = PanelColor;
            }
            dropdown.template.sizeDelta = new Vector2(0, 300);
            var itemRt = dropdown.template.Find("Viewport/Content/Item") as RectTransform;
            if (itemRt != null) itemRt.sizeDelta = new Vector2(itemRt.sizeDelta.x, 56);
            return dropdown;
        }

        private static ScrollRect Scroll(Transform parent, string name, out RectTransform content)
        {
            RectTransform rt = UI(name, parent);
            var scroll = rt.gameObject.AddComponent<ScrollRect>();

            RectTransform viewport = UI("Viewport", rt);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            // 빈 영역에서도 드래그/휠 스크롤이 되도록 투명 Image
            Img(viewport, new Color(0, 0, 0, 0), null);

            content = UI("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var vl = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 12;
            vl.childControlWidth = true;
            vl.childControlHeight = true;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40;
            return scroll;
        }
    }
}
