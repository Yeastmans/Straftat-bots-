using FishNet;
using TMPro;
using UnityEngine;

namespace StraftatBots
{
    public class BotCountUI : MonoBehaviour
    {
        private bool _created;
        private GameObject _dropdownObj;
        private TMP_Dropdown _dropdown;
        private float _checkTimer;

        private void Update()
        {
            if (_created)
            {
                // OnlyForHost: hide if not server
                if (_dropdownObj != null)
                {
                    bool isServer = InstanceFinder.NetworkManager != null && InstanceFinder.NetworkManager.IsServer;
                    _dropdownObj.transform.localScale = isServer ? Vector3.one : Vector3.zero;
                }
                return;
            }

            // Only check every 0.5s to reduce overhead
            _checkTimer += Time.deltaTime;
            if (_checkTimer < 0.5f) return;
            _checkTimer = 0f;

            if (SteamLobby.Instance == null) return;

            // Try to find a dropdown to clone
            TMP_Dropdown source = SteamLobby.Instance.GamemodeDropdown;
            if (source == null) source = SteamLobby.Instance.MaxPlayersDropdown;
            if (source == null)
            {
                Plugin.Log.LogWarning("[BotUI] No source dropdown found on SteamLobby");
                return;
            }

            Plugin.Log.LogInfo($"[BotUI] Found source dropdown: {source.name} parent: {source.transform.parent?.name}");
            CreateDropdown(source);
        }

        private void CreateDropdown(TMP_Dropdown source)
        {
            try
            {
                // Walk up to find the setting row container
                // Typical hierarchy: SettingsPanel > SettingRow > [Label, Dropdown]
                Transform sourceRow = source.transform.parent;
                Transform rowParent = sourceRow != null ? sourceRow.parent : source.transform.parent;

                Plugin.Log.LogInfo($"[BotUI] Source row: {sourceRow?.name}, Row parent: {rowParent?.name}");

                GameObject container;
                if (sourceRow != null && sourceRow.gameObject != source.gameObject)
                {
                    // Clone the entire row
                    container = Instantiate(sourceRow.gameObject, rowParent);
                    container.name = "BotCountSetting";
                    Plugin.Log.LogInfo($"[BotUI] Cloned row '{sourceRow.name}' under '{rowParent?.name}'");
                }
                else
                {
                    container = Instantiate(source.gameObject, source.transform.parent);
                    container.name = "BotCountDropdown";
                    Plugin.Log.LogInfo("[BotUI] Cloned dropdown directly");
                }

                container.SetActive(true);

                // Find dropdown in clone
                _dropdown = container.GetComponentInChildren<TMP_Dropdown>(true);
                if (_dropdown == null)
                {
                    Plugin.Log.LogError("[BotUI] No TMP_Dropdown in cloned object!");
                    Destroy(container);
                    return; // Don't set _created — allow retry next frame
                }

                // IMPORTANT: Remove all listeners BEFORE changing options
                // Otherwise the old listener fires and corrupts game settings
                _dropdown.onValueChanged.RemoveAllListeners();

                // Remove helper scripts from clone
                foreach (var c in container.GetComponentsInChildren<ChangeOtherDropdownValue>(true))
                    Destroy(c);

                // Set label text to "Bots"
                bool labelSet = false;
                foreach (var text in container.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    // Skip texts that are part of the dropdown itself (selected value, item labels)
                    if (text.transform.IsChildOf(_dropdown.transform)) continue;

                    text.text = "Bots";
                    labelSet = true;
                    Plugin.Log.LogInfo($"[BotUI] Set label: '{text.gameObject.name}' -> 'Bots'");
                    break;
                }
                if (!labelSet)
                    Plugin.Log.LogWarning("[BotUI] Could not find label text to rename");

                // Set options (0-8)
                _dropdown.ClearOptions();
                var options = new System.Collections.Generic.List<string>();
                for (int i = 0; i <= 8; i++)
                    options.Add(i.ToString());
                _dropdown.AddOptions(options);

                // Set value from config
                int currentBots = Mathf.Clamp(Plugin.MaxBots.Value, 0, 8);
                _dropdown.SetValueWithoutNotify(currentBots);
                _dropdown.RefreshShownValue();

                // NOW add our listener
                _dropdown.onValueChanged.AddListener(OnBotCountChanged);

                _dropdownObj = container;
                _created = true;

                Plugin.Log.LogInfo($"[BotUI] Dropdown created successfully (value={currentBots})");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[BotUI] Failed: {e.Message}\n{e.StackTrace}");
                // Don't set _created — allow retry next frame
            }
        }

        private void OnBotCountChanged(int value)
        {
            Plugin.MaxBots.Value = value;
            Plugin.Log.LogInfo($"[BotUI] Bot count set to {value}");

            if (BotManager.Instance != null)
            {
                while (BotManager.Instance.LobbyBots.Count > value)
                    BotManager.Instance.RemoveLastBot();
                while (BotManager.Instance.LobbyBots.Count < value)
                    BotManager.Instance.AddBot();
            }
        }

        private void OnDestroy()
        {
            if (_dropdownObj != null)
                Destroy(_dropdownObj);
        }
    }
}
