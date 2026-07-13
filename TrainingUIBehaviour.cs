using UnityEngine;

namespace StraftatBots
{
    /// <summary>
    /// Dedicated MonoBehaviour on DontDestroyOnLoad GameObject for IMGUI rendering.
    /// BepInEx plugin OnGUI doesn't fire reliably in STRAFTAT.
    /// </summary>
    public class TrainingUIBehaviour : MonoBehaviour
    {
        private void OnGUI()
        {
            try
            {
                if (NavGraph.Instance == null) return;
                // No bot UI in the main menu — the Training panel used to linger there
                // because the mode config persists across scenes. It appears only once
                // a map is loaded (and training is actually running).
                if (PauseManager.Instance == null || PauseManager.Instance.inMainMenu) return;
                if (NavGraph.Instance.Mode != NavMode.Training)
                {
                    // Play mode still gets the untrained-map popup (Start Training /
                    // Keep Playing) — it was unreachable behind the Training-only gate.
                    TrainingUI.DrawPlayModePopups();
                    return;
                }
                TrainingUI.DrawAll();
            }
            catch (System.Exception e)
            {
                GUI.Label(new Rect(10, 120, 600, 25), $"[BOT UI ERROR] {e.Message}");
            }
        }
    }
}
