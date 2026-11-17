using System.Collections.Generic;
using System.Text;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Magnetic.SteamLobby.Samples
{
    /// <summary>
    /// 게임 화면에 한국어 스팀 로비 로그를 보여주는 IMGUI 콘솔.
    /// F1: 열기/닫기, F2/F3: 글자 크기. 수준·분류별 필터, 복사, 지우기 지원.
    /// 아무 오브젝트에 붙이면 되며, 씬이 바뀌어도 유지된다 (중복 생성 방지).
    /// </summary>
    [DisallowMultipleComponent]
    public class SteamLogConsole : MonoBehaviour
    {
        [SerializeField] private bool visibleOnStart = false;
        [Tooltip("릴리즈 빌드(Development Build 아님)에서도 동작할지")]
        [SerializeField] private bool enableInReleaseBuild = false;
        [SerializeField] private float scale = 1f;
        [SerializeField] private int maxLines = 300;

        private static SteamLogConsole instance;

        private readonly List<SteamLogEntry> lines = new List<SteamLogEntry>();
        private readonly bool[] levelEnabled = { false, true, true, true }; // 상세는 기본 숨김
        private readonly bool[] categoryEnabled = { true, true, true, true };
        private bool visible;
        private bool autoScroll = true;
        private Vector2 scroll;
        private GUIStyle lineStyle;

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            if (!Debug.isDebugBuild && !enableInReleaseBuild) { Destroy(this); return; }
            instance = this;
            if (transform.parent == null) DontDestroyOnLoad(gameObject);

            visible = visibleOnStart;
            lines.AddRange(SteamLog.Recent);
            SteamLog.OnLogged += HandleLog;
        }

        private void OnDestroy()
        {
            if (instance != this) return;
            instance = null;
            SteamLog.OnLogged -= HandleLog;
        }

        private void HandleLog(SteamLogEntry entry)
        {
            lines.Add(entry);
            if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
            if (autoScroll) scroll.y = float.MaxValue;
        }

        private void Update()
        {
            if (KeyDown(0)) visible = !visible;
            if (KeyDown(1)) scale = Mathf.Max(0.6f, scale - 0.1f);
            if (KeyDown(2)) scale = Mathf.Min(2.5f, scale + 0.1f);
        }

        /// <summary>0=F1, 1=F2, 2=F3</summary>
        private static bool KeyDown(int index)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            switch (index)
            {
                case 0: return kb.f1Key.wasPressedThisFrame;
                case 1: return kb.f2Key.wasPressedThisFrame;
                default: return kb.f3Key.wasPressedThisFrame;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            switch (index)
            {
                case 0: return Input.GetKeyDown(KeyCode.F1);
                case 1: return Input.GetKeyDown(KeyCode.F2);
                default: return Input.GetKeyDown(KeyCode.F3);
            }
#else
            return false;
#endif
        }

        private void OnGUI()
        {
            if (!visible) return;

            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale, h = Screen.height / scale;
            Rect area = new Rect(10, 10, Mathf.Min(760, w - 20), Mathf.Min(460, h - 20));

            lineStyle ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 13 };

            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"<b>스팀 로비 로그</b>   (F1 닫기 / F2·F3 크기 x{scale:0.0})", lineStyle);

            GUILayout.BeginHorizontal();
            for (int i = 0; i < levelEnabled.Length; i++)
                levelEnabled[i] = GUILayout.Toggle(levelEnabled[i], SteamLog.LevelLabel((SteamLogLevel)i), GUILayout.Width(60));
            GUILayout.Space(16);
            for (int i = 0; i < categoryEnabled.Length; i++)
                categoryEnabled[i] = GUILayout.Toggle(categoryEnabled[i], SteamLog.CategoryLabel((SteamLogCategory)i), GUILayout.Width(80));
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var e in lines)
            {
                if (!levelEnabled[(int)e.Level] || !categoryEnabled[(int)e.Category]) continue;
                GUILayout.Label($"<color={ColorOf(e.Level)}>{Escape(e.ToString())}</color>", lineStyle);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            autoScroll = GUILayout.Toggle(autoScroll, "자동 스크롤", GUILayout.Width(100));
            if (GUILayout.Button("복사", GUILayout.Width(80))) GUIUtility.systemCopyBuffer = BuildText();
            if (GUILayout.Button("지우기", GUILayout.Width(80))) lines.Clear();
            GUILayout.FlexibleSpace();
            GUILayout.Label(BuildStatusLine(), lineStyle);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            GUI.matrix = old;
        }

        private static string BuildStatusLine()
        {
            string steam = SteamManager.Initialized ? $"Steam: {SteamManager.Instance.LocalPersonaName}" : "Steam: 연결 안 됨";
            string state = ServerManager.Instance != null ? $"세션: {ServerManager.Instance.State}" : "세션: 없음";
            return $"{steam}  |  {state}";
        }

        private string BuildText()
        {
            var sb = new StringBuilder();
            foreach (var e in lines)
                if (levelEnabled[(int)e.Level] && categoryEnabled[(int)e.Category]) sb.AppendLine(e.ToString());
            return sb.ToString();
        }

        private static string ColorOf(SteamLogLevel level)
        {
            switch (level)
            {
                case SteamLogLevel.Error: return "#FF7A6E";
                case SteamLogLevel.Warning: return "#FFD36E";
                case SteamLogLevel.Verbose: return "#9AA0A6";
                default: return "#E8E6E0";
            }
        }

        // IMGUI 리치 텍스트 태그가 메시지 안의 < > 때문에 깨지지 않도록
        private static string Escape(string s) => s.Replace("<", "‹").Replace(">", "›");
    }
}
