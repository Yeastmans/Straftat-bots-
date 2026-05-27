using UnityEngine;
using FishNet;

namespace StraftatBots
{
    public class BotUI : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Plugin.Log.LogInfo("BotUI created and set to DontDestroyOnLoad");
        }

        private void OnGUI()
        {
            // Only show when hosting a lobby
            if (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServer) return;
            if (PauseManager.Instance == null || !PauseManager.Instance.inMainMenu) return;
            if (LobbyController.Instance == null || LobbyController.Instance.LocalPlayerController == null) return;
            if (BotManager.Instance == null) return;

            int botCount = BotManager.Instance.LobbyBots.Count;

            float x = 10;
            float y = Screen.height - 120;

            GUI.Box(new Rect(x, y, 200, 110), "Bots");

            if (GUI.Button(new Rect(x + 10, y + 25, 180, 30), $"Add Bot ({botCount}/{Plugin.MaxBots.Value})"))
            {
                BotManager.Instance.AddBot();
            }

            if (botCount > 0)
            {
                if (GUI.Button(new Rect(x + 10, y + 60, 180, 30), "Remove Bot"))
                {
                    BotManager.Instance.RemoveLastBot();
                }
            }
        }
    }
}
