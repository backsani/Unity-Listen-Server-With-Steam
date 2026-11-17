#if STEAMWORKS_NET && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX) && !DISABLESTEAMWORKS
#define STEAM_ENABLED
#endif

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
#if STEAM_ENABLED
using System.Runtime.InteropServices;
using Steamworks;
#endif

namespace Magnetic.SteamLobby
{
    /// <summary>
    /// Netcode for GameObjects 용 Steam Networking Sockets(P2P) 트랜스포트.
    /// IP/포트 없이 SteamID 로 접속하며, NAT 통과는 Steam 릴레이(SDR)가 처리한다.
    ///
    /// - 호스트: StartHost() 시 P2P 리슨 소켓을 연다.
    /// - 클라이언트: <see cref="ConnectToSteamId"/> 에 호스트 SteamID 를 넣고 StartClient().
    /// <see cref="ServerManager"/> 를 쓰면 위 과정을 자동으로 처리한다.
    /// </summary>
    [AddComponentMenu("Magnetic/Steam Lobby/Steam Networking Sockets Transport")]
    public class SteamNetworkingSocketsTransport : NetworkTransport
    {
        [Tooltip("P2P 가상 포트. 호스트와 클라이언트가 같아야 한다.")]
        [SerializeField] private int virtualPort = 0;

        [Tooltip("한 번에 꺼내 오는 최대 메시지 수")]
        [SerializeField] private int maxMessagesPerReceive = 256;

        /// <summary>클라이언트로 시작할 때 접속할 호스트 SteamID</summary>
        public ulong ConnectToSteamId { get; set; }

        public override ulong ServerClientId => 0;

        public override bool IsSupported
        {
            get
            {
#if STEAM_ENABLED
                return true;
#else
                return false;
#endif
            }
        }

        private struct QueuedEvent
        {
            public NetworkEvent type;
            public ulong clientId;
            public byte[] data;
            public float time;
        }

        private readonly Queue<QueuedEvent> events = new Queue<QueuedEvent>();

#if STEAM_ENABLED
        private bool isServer;
        private HSteamListenSocket listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup pollGroup = HSteamNetPollGroup.Invalid;
        private HSteamNetConnection serverConnection = HSteamNetConnection.Invalid;

        private readonly Dictionary<ulong, HSteamNetConnection> clientToConnection = new Dictionary<ulong, HSteamNetConnection>();
        private readonly Dictionary<HSteamNetConnection, ulong> connectionToClient = new Dictionary<HSteamNetConnection, ulong>();
        private readonly Dictionary<ulong, ulong> clientToSteamId = new Dictionary<ulong, ulong>();
        private ulong nextClientId = 1;

        private Callback<SteamNetConnectionStatusChangedCallback_t> statusChangedCb;
        private IntPtr[] receiveBuffer;
        private static readonly SteamNetworkingConfigValue_t[] NoOptions = new SteamNetworkingConfigValue_t[0];
#endif

        /// <summary>서버 전용. NGO clientId 에 해당하는 SteamID (호스트 자신은 로컬 SteamID)</summary>
        public bool TryGetSteamId(ulong clientId, out ulong steamId)
        {
            if (clientId == ServerClientId && SteamManager.Instance != null)
            {
                steamId = SteamManager.Instance.LocalSteamId;
                return true;
            }
#if STEAM_ENABLED
            return clientToSteamId.TryGetValue(clientId, out steamId);
#else
            steamId = 0;
            return false;
#endif
        }

        // =====================================================================
        // NetworkTransport
        // =====================================================================

        public override void Initialize(NetworkManager networkManager = null)
        {
            events.Clear();
#if STEAM_ENABLED
            if (!SteamManager.Initialized)
            {
                SteamLog.Error(SteamLogCategory.Network, "트랜스포트 초기화 실패: Steam 이 초기화되지 않았습니다.");
                return;
            }
            receiveBuffer = new IntPtr[Mathf.Max(1, maxMessagesPerReceive)];
            statusChangedCb ??= Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            // 릴레이 네트워크 접속을 미리 시작해 첫 연결 지연을 줄인다
            SteamNetworkingUtils.InitRelayNetworkAccess();
#else
            SteamLog.Error(SteamLogCategory.Network, "이 플랫폼에서는 Steam 트랜스포트를 사용할 수 없습니다.");
#endif
        }

        public override bool StartServer()
        {
#if STEAM_ENABLED
            if (!SteamManager.Initialized) return false;
            isServer = true;
            nextClientId = 1;
            listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, 0, NoOptions);
            pollGroup = SteamNetworkingSockets.CreatePollGroup();
            bool ok = listenSocket != HSteamListenSocket.Invalid && pollGroup != HSteamNetPollGroup.Invalid;
            if (ok) SteamLog.Info(SteamLogCategory.Network, $"리슨 서버를 열었습니다 (가상 포트 {virtualPort}).");
            else SteamLog.Error(SteamLogCategory.Network, "리슨 소켓을 열지 못했습니다.");
            return ok;
#else
            return false;
#endif
        }

        public override bool StartClient()
        {
#if STEAM_ENABLED
            if (!SteamManager.Initialized) return false;
            if (ConnectToSteamId == 0)
            {
                SteamLog.Error(SteamLogCategory.Network, "접속할 호스트 SteamID 가 지정되지 않았습니다 (ConnectToSteamId).");
                return false;
            }
            isServer = false;
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(ConnectToSteamId);
            serverConnection = SteamNetworkingSockets.ConnectP2P(ref identity, virtualPort, 0, NoOptions);
            bool ok = serverConnection != HSteamNetConnection.Invalid;
            if (ok) SteamLog.Info(SteamLogCategory.Network, $"호스트({ConnectToSteamId})에 접속을 시도합니다.");
            else SteamLog.Error(SteamLogCategory.Network, $"호스트({ConnectToSteamId}) 접속을 시작하지 못했습니다.");
            return ok;
#else
            return false;
#endif
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
#if STEAM_ENABLED
            if (!TryResolveConnection(clientId, out HSteamNetConnection conn))
            {
                SteamLog.Verbose(SteamLogCategory.Network, $"전송 대상 연결이 없습니다 (clientId={clientId}).");
                return;
            }

            int flags = networkDelivery == NetworkDelivery.Unreliable || networkDelivery == NetworkDelivery.UnreliableSequenced
                ? Constants.k_nSteamNetworkingSend_UnreliableNoNagle
                : Constants.k_nSteamNetworkingSend_ReliableNoNagle;

            GCHandle handle = GCHandle.Alloc(payload.Array, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject() + payload.Offset;
                EResult result = SteamNetworkingSockets.SendMessageToConnection(conn, ptr, (uint)payload.Count, flags, out long _);
                if (result != EResult.k_EResultOK)
                    SteamLog.Warn(SteamLogCategory.Network, $"메시지 전송 실패: {result} (clientId={clientId}, {payload.Count}바이트)");
            }
            finally
            {
                handle.Free();
            }
#endif
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
#if STEAM_ENABLED
            if (events.Count == 0) ReceiveMessages();
#endif
            if (events.Count == 0)
            {
                clientId = 0;
                payload = default;
                receiveTime = Time.realtimeSinceStartup;
                return NetworkEvent.Nothing;
            }

            QueuedEvent e = events.Dequeue();
            clientId = e.clientId;
            payload = e.data != null ? new ArraySegment<byte>(e.data) : default;
            receiveTime = e.time;
            return e.type;
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
#if STEAM_ENABLED
            if (!clientToConnection.TryGetValue(clientId, out HSteamNetConnection conn)) return;
            // linger=true: 연결 종료 사유 같은 마지막 신뢰성 메시지를 보낸 뒤 닫는다
            SteamNetworkingSockets.CloseConnection(conn, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "서버가 연결을 종료했습니다", true);
            RemoveClient(conn);
#endif
        }

        public override void DisconnectLocalClient()
        {
#if STEAM_ENABLED
            if (serverConnection == HSteamNetConnection.Invalid) return;
            SteamNetworkingSockets.CloseConnection(serverConnection, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "클라이언트가 연결을 종료했습니다", true);
            serverConnection = HSteamNetConnection.Invalid;
#endif
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
#if STEAM_ENABLED
            if (!TryResolveConnection(clientId, out HSteamNetConnection conn)) return 0;
            var status = default(SteamNetConnectionRealTimeStatus_t);
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes) == EResult.k_EResultOK)
                return (ulong)Mathf.Max(0, status.m_nPing);
#endif
            return 0;
        }

        public override void Shutdown()
        {
#if STEAM_ENABLED
            if (SteamManager.Initialized)
            {
                foreach (var conn in connectionToClient.Keys)
                    SteamNetworkingSockets.CloseConnection(conn, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "서버 종료", true);
                if (serverConnection != HSteamNetConnection.Invalid)
                    SteamNetworkingSockets.CloseConnection(serverConnection, (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic, "클라이언트 종료", true);
                if (listenSocket != HSteamListenSocket.Invalid)
                    SteamNetworkingSockets.CloseListenSocket(listenSocket);
                if (pollGroup != HSteamNetPollGroup.Invalid)
                    SteamNetworkingSockets.DestroyPollGroup(pollGroup);
            }

            if (isServer || serverConnection != HSteamNetConnection.Invalid || listenSocket != HSteamListenSocket.Invalid)
                SteamLog.Info(SteamLogCategory.Network, "트랜스포트를 종료했습니다.");

            serverConnection = HSteamNetConnection.Invalid;
            listenSocket = HSteamListenSocket.Invalid;
            pollGroup = HSteamNetPollGroup.Invalid;
            connectionToClient.Clear();
            clientToConnection.Clear();
            clientToSteamId.Clear();
            isServer = false;
            statusChangedCb?.Dispose();
            statusChangedCb = null;
#endif
            events.Clear();
        }

        private void OnDestroy()
        {
#if STEAM_ENABLED
            statusChangedCb?.Dispose();
            statusChangedCb = null;
#endif
        }

#if STEAM_ENABLED
        // =====================================================================
        // 내부 처리
        // =====================================================================

        private bool TryResolveConnection(ulong clientId, out HSteamNetConnection conn)
        {
            if (!isServer)
            {
                conn = serverConnection;
                return conn != HSteamNetConnection.Invalid;
            }
            return clientToConnection.TryGetValue(clientId, out conn);
        }

        private void Enqueue(NetworkEvent type, ulong clientId, byte[] data = null)
        {
            events.Enqueue(new QueuedEvent { type = type, clientId = clientId, data = data, time = Time.realtimeSinceStartup });
        }

        private void RemoveClient(HSteamNetConnection conn)
        {
            if (!connectionToClient.TryGetValue(conn, out ulong clientId)) return;
            connectionToClient.Remove(conn);
            clientToConnection.Remove(clientId);
            clientToSteamId.Remove(clientId);
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t data)
        {
            HSteamNetConnection conn = data.m_hConn;
            SteamNetConnectionInfo_t info = data.m_info;
            ulong remoteSteamId = info.m_identityRemote.GetSteamID64();

            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (isServer && info.m_hListenSocket == listenSocket && listenSocket != HSteamListenSocket.Invalid)
                    {
                        EResult r = SteamNetworkingSockets.AcceptConnection(conn);
                        if (r != EResult.k_EResultOK)
                        {
                            SteamLog.Warn(SteamLogCategory.Network, $"접속 수락 실패: {r} (SteamID={remoteSteamId})");
                            SteamNetworkingSockets.CloseConnection(conn, 0, "수락 실패", false);
                        }
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (isServer)
                    {
                        if (connectionToClient.ContainsKey(conn)) break;
                        ulong newClientId = nextClientId++;
                        connectionToClient[conn] = newClientId;
                        clientToConnection[newClientId] = conn;
                        clientToSteamId[newClientId] = remoteSteamId;
                        SteamNetworkingSockets.SetConnectionPollGroup(conn, pollGroup);
                        SteamLog.Info(SteamLogCategory.Network, $"플레이어가 접속했습니다: SteamID={remoteSteamId}, clientId={newClientId}");
                        Enqueue(NetworkEvent.Connect, newClientId);
                    }
                    else if (conn == serverConnection)
                    {
                        SteamLog.Info(SteamLogCategory.Network, $"호스트에 연결되었습니다 (SteamID={remoteSteamId}).");
                        Enqueue(NetworkEvent.Connect, ServerClientId);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    bool byPeer = info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
                    string reason = string.IsNullOrEmpty(info.m_szEndDebug) ? (byPeer ? "상대가 연결을 닫음" : "연결 문제 감지") : info.m_szEndDebug;
                    SetDisconnectEvent(byPeer ? DisconnectEvents.ClosedByRemote : DisconnectEvents.ProtocolTimeout, reason);

                    if (isServer)
                    {
                        if (connectionToClient.TryGetValue(conn, out ulong closedClientId))
                        {
                            SteamLog.Info(SteamLogCategory.Network, $"플레이어 연결이 끊겼습니다: SteamID={remoteSteamId}, 사유={reason}");
                            RemoveClient(conn);
                            Enqueue(NetworkEvent.Disconnect, closedClientId);
                        }
                        SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    }
                    else if (conn == serverConnection)
                    {
                        SteamLog.Warn(SteamLogCategory.Network, $"호스트와의 연결이 끊겼습니다: 사유={reason}");
                        SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                        serverConnection = HSteamNetConnection.Invalid;
                        Enqueue(NetworkEvent.Disconnect, ServerClientId);
                    }
                    else
                    {
                        SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    }
                    break;
            }
        }

        private void ReceiveMessages()
        {
            if (receiveBuffer == null) return;

            int count;
            if (isServer)
            {
                if (pollGroup == HSteamNetPollGroup.Invalid) return;
                count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, receiveBuffer, receiveBuffer.Length);
            }
            else
            {
                if (serverConnection == HSteamNetConnection.Invalid) return;
                count = SteamNetworkingSockets.ReceiveMessagesOnConnection(serverConnection, receiveBuffer, receiveBuffer.Length);
            }

            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = receiveBuffer[i];
                try
                {
                    SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                    ulong clientId = ServerClientId;
                    if (isServer && !connectionToClient.TryGetValue(msg.m_conn, out clientId)) continue;

                    var data = new byte[msg.m_cbSize];
                    if (msg.m_cbSize > 0) Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                    Enqueue(NetworkEvent.Data, clientId, data);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(ptr);
                }
            }
        }
#endif
    }
}
