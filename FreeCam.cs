using UnityEngine;

namespace StraftatBots
{
    /// <summary>
    /// Freecam: detach the player's camera and fly it around with WASD + mouse.
    /// While active, the player body freezes (FirstPersonController disabled).
    /// Toggle from TrainingUI. No global hotkey.
    /// </summary>
    public class FreeCam : MonoBehaviour
    {
        public static bool Active { get; private set; }
        private static FreeCam _instance;

        // Cached targets
        private FirstPersonController _fpc;
        private bool _fpcWasEnabled;
        private Camera _cam;
        private Transform _camTf;
        private Transform _originalParent;
        private Vector3 _originalLocalPos;
        private Quaternion _originalLocalRot;

        // Freecam state
        private Vector3 _camPos;
        private float _yaw;
        private float _pitch;

        // Tuning
        private const float BASE_SPEED   = 12f;   // m/s
        private const float FAST_MULT    = 3f;    // Shift
        private const float SLOW_MULT    = 0.25f; // Ctrl
        private const float MOUSE_SENS_X = 2.5f;
        private const float MOUSE_SENS_Y = 2.0f;
        private const float PITCH_LIMIT  = 89f;

        /// <summary>Toggle freecam on/off. Called from TrainingUI button.</summary>
        public static void Toggle()
        {
            if (_instance == null)
            {
                var go = new GameObject("StraftatBots_FreeCam");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<FreeCam>();
            }
            if (Active) _instance.Disable();
            else        _instance.Enable();
        }

        private void Enable()
        {
            // Find local FPC — prefer the one tied to the local ClientInstance
            _fpc = FindLocalFPC();
            if (_fpc == null)
            {
                Plugin.Log.LogWarning("[FreeCam] No local FirstPersonController found — cannot enable");
                return;
            }

            _cam = Camera.main;
            if (_cam == null)
            {
                // Fallback: any enabled camera under the player
                var anyCam = _fpc.GetComponentInChildren<Camera>();
                if (anyCam != null) _cam = anyCam;
            }
            if (_cam == null)
            {
                Plugin.Log.LogWarning("[FreeCam] No active camera found — cannot enable");
                return;
            }

            _camTf = _cam.transform;
            _originalParent   = _camTf.parent;
            _originalLocalPos = _camTf.localPosition;
            _originalLocalRot = _camTf.localRotation;

            // Freeze the player body: disabling FPC stops input-driven movement + camera rotation
            _fpcWasEnabled = _fpc.enabled;
            _fpc.enabled = false;

            // Detach camera so FPC (even if something re-enables it) can't drive its transform
            _camTf.SetParent(null, true);

            // Seed yaw/pitch from current camera orientation
            Vector3 e = _camTf.eulerAngles;
            _yaw   = e.y;
            _pitch = NormalizePitch(e.x);
            _camPos = _camTf.position;

            Active = true;
            Plugin.Log.LogInfo("[FreeCam] ENABLED — WASD move, mouse look, Shift=fast, Ctrl=slow. Click button again to return.");
        }

        private void Disable()
        {
            Active = false;

            if (_camTf != null && _originalParent != null)
            {
                _camTf.SetParent(_originalParent, false);
                _camTf.localPosition = _originalLocalPos;
                _camTf.localRotation = _originalLocalRot;
            }

            if (_fpc != null)
                _fpc.enabled = _fpcWasEnabled;

            _fpc = null;
            _cam = null;
            _camTf = null;
            _originalParent = null;
            Plugin.Log.LogInfo("[FreeCam] DISABLED");
        }

        private void LateUpdate()
        {
            if (!Active) return;
            if (_camTf == null || _fpc == null) { Disable(); return; }

            // Safety: if the player or camera got destroyed (map change, respawn, etc.), bail out cleanly
            if (_fpc.gameObject == null) { Disable(); return; }

            // --- Mouse look ---
            float mx = Input.GetAxisRaw("Mouse X");
            float my = Input.GetAxisRaw("Mouse Y");
            _yaw   += mx * MOUSE_SENS_X;
            _pitch -= my * MOUSE_SENS_Y;
            _pitch  = Mathf.Clamp(_pitch, -PITCH_LIMIT, PITCH_LIMIT);

            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);

            // --- WASD movement (camera-relative, so Q/E aren't needed for up/down -- use Space/Ctrl) ---
            Vector3 fwd   = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;
            Vector3 move  = Vector3.zero;

            if (Input.GetKey(KeyCode.W)) move += fwd;
            if (Input.GetKey(KeyCode.S)) move -= fwd;
            if (Input.GetKey(KeyCode.D)) move += right;
            if (Input.GetKey(KeyCode.A)) move -= right;
            if (Input.GetKey(KeyCode.Space))      move += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl)) move += Vector3.down;

            float speed = BASE_SPEED;
            if (Input.GetKey(KeyCode.LeftShift)) speed *= FAST_MULT;
            else if (Input.GetKey(KeyCode.LeftAlt)) speed *= SLOW_MULT;

            if (move.sqrMagnitude > 1f) move.Normalize();
            _camPos += move * speed * Time.deltaTime;

            // Apply
            _camTf.SetPositionAndRotation(_camPos, rot);
        }

        private static float NormalizePitch(float x)
        {
            // Unity eulerAngles comes back in [0,360). Convert 350° -> -10°, etc.
            if (x > 180f) x -= 360f;
            return Mathf.Clamp(x, -PITCH_LIMIT, PITCH_LIMIT);
        }

        /// <summary>Best-effort search for the local player's FirstPersonController.</summary>
        private static FirstPersonController FindLocalFPC()
        {
            // Preferred: via the game's own client instance
            try
            {
                var ci = ClientInstance.Instance;
                if (ci != null && ci.PlayerSpawner != null && ci.PlayerSpawner.player != null)
                    return ci.PlayerSpawner.player;
            }
            catch { /* fall through */ }

            // Fallback: scan the scene — pick a non-bot FPC
            var all = Object.FindObjectsOfType<FirstPersonController>();
            foreach (var fpc in all)
            {
                if (fpc == null) continue;
                if (fpc.GetComponent<BotController>() != null) continue; // skip bots
                return fpc;
            }
            return null;
        }
    }
}
