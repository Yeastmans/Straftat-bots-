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
                // Only show in Training mode
                if (NavGraph.Instance == null || NavGraph.Instance.Mode != NavMode.Training) return;
                TrainingUI.DrawAll();
            }
            catch (System.Exception e)
            {
                GUI.Label(new Rect(10, 120, 600, 25), $"[BOT UI ERROR] {e.Message}");
            }
        }
    }
}
