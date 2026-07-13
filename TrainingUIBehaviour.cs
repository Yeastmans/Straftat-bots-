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
