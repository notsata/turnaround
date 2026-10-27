using System.Text;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Turnaround.Multiplayer
{
    // The only place that knows about the transport. Swapping to a Steam transport means changing
    // ConfigureTransport (and the NetworkManager's transport component), nothing else.
    public static class ConnectionManager
    {
        public const int MaxPlayers = 4;
        public const ushort Port = 7777;

        public static string LastDisconnectReason;

        static NetworkManager Net => NetworkManager.Singleton;

        public static bool StartHost()
        {
            ConfigureTransport("127.0.0.1", listenAll: true);
            Net.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version);
            Net.ConnectionApprovalCallback = ApproveConnection;
            return Net.StartHost();
        }

        public static bool StartClient(string address)
        {
            ConfigureTransport(string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim(), listenAll: false);
            Net.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(Application.version); // the host turns away other versions
            return Net.StartClient();
        }

        // NetworkManager survives scene loads, so destroy it and reload to get a clean ramp.
        public static void Leave(string reason = null)
        {
            LastDisconnectReason = reason;
            if (Net != null)
            {
                Net.Shutdown();
                Object.Destroy(Net.gameObject);
            }
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        static void ConfigureTransport(string address, bool listenAll)
        {
            var transport = Net.GetComponent<UnityTransport>();
            transport.SetConnectionData(address, Port, listenAll ? "0.0.0.0" : null);
        }

        static void ApproveConnection(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            string theirVersion = request.Payload != null ? Encoding.UTF8.GetString(request.Payload) : "";
            bool sameVersion = request.ClientNetworkId == NetworkManager.ServerClientId || theirVersion == Application.version;
            response.Approved = sameVersion && Net.ConnectedClientsIds.Count < MaxPlayers;
            response.CreatePlayerObject = response.Approved;
            if (!sameVersion) response.Reason = $"Version mismatch: the host has v{Application.version}, you have v{theirVersion}. Restart the game to update.";
            else if (!response.Approved) response.Reason = "Ramp is full. Four people is already a safety concern.";
        }
    }
}
