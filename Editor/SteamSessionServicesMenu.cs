using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Magnetic.SteamLobby.EditorTools
{
    /// <summary>
    /// Steam 세션에 필요한 컴포넌트를 한 오브젝트에 묶어 생성한다.
    /// SteamManager + SteamLobbyService + SteamNetworkingSocketsTransport + NetworkManager + ServerManager
    /// </summary>
    public static class SteamSessionServicesMenu
    {
        public const string DefaultObjectName = "SteamSessionServices";

        [MenuItem("GameObject/Magnetic/Steam Session Services", false, 10)]
        private static void CreateFromMenu(MenuCommand command)
        {
            if (Object.FindAnyObjectByType<ServerManager>() != null &&
                !EditorUtility.DisplayDialog("Steam Session Services", "씬에 이미 ServerManager 가 있습니다. 하나 더 만들까요?", "만들기", "취소"))
                return;

            GameObject go = CreateServices(null);
            Undo.RegisterCreatedObjectUndo(go, "Create Steam Session Services");
            Selection.activeGameObject = go;
        }

        /// <summary>
        /// 서비스 오브젝트 생성.
        /// </summary>
        /// <param name="defaultPreset">ServerManager 기본 프리셋 (null 이면 비워 둠)</param>
        /// <param name="gameId">로비 검색용 게임 식별자 (null 이면 Product Name)</param>
        /// <param name="gameVersion">null 이면 Application.version</param>
        public static GameObject CreateServices(SessionPreset defaultPreset, string gameId = null, string gameVersion = null)
        {
            var go = new GameObject(DefaultObjectName);

            go.AddComponent<SteamManager>();
            var lobby = go.AddComponent<SteamLobbyService>();
            var transport = go.AddComponent<SteamNetworkingSocketsTransport>();
            var network = go.AddComponent<NetworkManager>();
            if (network.NetworkConfig == null) network.NetworkConfig = new NetworkConfig();
            network.NetworkConfig.NetworkTransport = transport;
            network.NetworkConfig.ConnectionApproval = true;
            var server = go.AddComponent<ServerManager>();

            var lobbySo = new SerializedObject(lobby);
            lobbySo.FindProperty("gameId").stringValue = string.IsNullOrEmpty(gameId) ? PlayerSettings.productName : gameId;
            lobbySo.FindProperty("gameVersion").stringValue = string.IsNullOrEmpty(gameVersion) ? Application.version : gameVersion;
            lobbySo.ApplyModifiedPropertiesWithoutUndo();

            var serverSo = new SerializedObject(server);
            serverSo.FindProperty("defaultPreset").objectReferenceValue = defaultPreset;
            serverSo.FindProperty("lobbyService").objectReferenceValue = lobby;
            serverSo.FindProperty("networkManager").objectReferenceValue = network;
            serverSo.FindProperty("transport").objectReferenceValue = transport;
            serverSo.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(network);
            return go;
        }

        /// <summary>path 에 프리셋이 없으면 기본값으로 만들고, 있으면 불러온다.</summary>
        public static SessionPreset LoadOrCreatePreset(string path, string displayName = "기본 2~4인", int minPlayers = 2, int maxPlayers = 4)
        {
            var preset = AssetDatabase.LoadAssetAtPath<SessionPreset>(path);
            if (preset != null) return preset;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            preset = ScriptableObject.CreateInstance<SessionPreset>();
            preset.displayName = displayName;
            preset.minPlayers = minPlayers;
            preset.maxPlayers = maxPlayers;
            AssetDatabase.CreateAsset(preset, path);
            AssetDatabase.SaveAssets();
            return preset;
        }
    }
}
