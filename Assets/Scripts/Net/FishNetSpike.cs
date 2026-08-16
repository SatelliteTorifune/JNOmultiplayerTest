using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Transporting.Tugboat;
using UnityEngine;

namespace Assets.Scripts.Net
{
    /// <summary>
    /// FishNet spike 最小验证：起一个 server + 本地 client 连自己，确认连接成功。
    /// 验证通过后删掉此文件。
    ///
    /// 注意：NetworkManager.Awake 需要 SpawnablePrefabs 非空，且 Transport 组件必须在同一 GameObject 上。
    /// 所以要先建"未激活"的 GameObject → 加 Tugboat + NetworkManager → 设 SpawnablePrefabs → 再激活。
    /// </summary>
    public class FishNetSpike : MonoBehaviour
    {
        private NetworkManager _nm;

        private void Start()
        {
            // 1. 先建未激活的 GameObject，防止 AddComponent<NetworkManager> 时立刻跑 Awake
            var go = new GameObject("FishNetSpikeNM");
            go.SetActive(false);

            // 2. 加 Tugboat transport（FishNet 默认 UDP 传输，LiteNetLib 实现）
            go.AddComponent<Tugboat>();

            // 3. 加 NetworkManager
            _nm = go.AddComponent<NetworkManager>();

            // 4. 设置 SpawnablePrefabs（spike 不需要真正生成 NetworkObject，空实例即可）
            _nm.SpawnablePrefabs = ScriptableObject.CreateInstance<DefaultPrefabObjects>();

            // 5. 激活 → Awake 用完整配置初始化，不再 NRE
            go.SetActive(true);

            // 6. 监听连接状态
            _nm.ServerManager.OnServerConnectionState += args =>
            {
                Mod.LogLobby("[FishNetSpike] Server state: " + args.ConnectionState);
            };
            _nm.ClientManager.OnClientConnectionState += args =>
            {
                Mod.LogLobby("[FishNetSpike] Client state: " + args.ConnectionState);
            };

            // 7. 先起 server（监听 25556，避开现有 TcpTransport 的 25555）
            bool serverOk = _nm.ServerManager.StartConnection(25556);
            Mod.LogLobby("[FishNetSpike] Server StartConnection returned " + serverOk + " (port 25556)");

            // 8. 延迟 0.5s 后 client 连自己
            Invoke(nameof(ConnectClient), 0.5f);
        }

        private void ConnectClient()
        {
            bool clientOk = _nm.ClientManager.StartConnection("127.0.0.1", 25556);
            Mod.LogLobby("[FishNetSpike] Client StartConnection returned " + clientOk + " (127.0.0.1:25556)");
        }

        private void OnDestroy()
        {
            if (_nm != null)
            {
                _nm.ServerManager.StopConnection(true);
                _nm.ClientManager.StopConnection();
                Destroy(_nm.gameObject);
            }
        }
    }
}