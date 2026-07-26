using UnityEngine;

namespace StraftatBots
{
    public static class TrainingUI
    {
        private static bool _expanded = true;
        private static bool _helpOpen = false;
        private static Vector2 _helpScrollPos = Vector2.zero;
        private static float _lastContentH = 430f; // measured each frame; sizes the panel

        // Dragging state
        private static Vector2 _panelPos = new Vector2(10f, 10f);
        private static bool _isDragging;
        private static Vector2 _dragOffset;

        // Cached styles
        private static GUIStyle _boxStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _activeButtonStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _miniButtonStyle;
        private static Texture2D _darkTex;
        private static Texture2D _accentTex;
        private static Texture2D _activeTex;
        private static Texture2D _dangerTex;
        private static Texture2D _dragBarTex;
        private static bool _stylesInit;

        private static void InitStyles()
        {
            if (_stylesInit) return;

            _darkTex = MakeTex(2, 2, new Color(0.05f, 0.05f, 0.08f, 0.92f));
            _accentTex = MakeTex(2, 2, new Color(0.2f, 0.4f, 0.7f, 0.9f));
            _activeTex = MakeTex(2, 2, new Color(0.15f, 0.6f, 0.3f, 0.9f));
            _dangerTex = MakeTex(2, 2, new Color(0.7f, 0.2f, 0.2f, 0.9f));
            _dragBarTex = MakeTex(2, 2, new Color(0.15f, 0.15f, 0.2f, 0.95f));

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = _darkTex;

            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.fontSize = 14;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.normal.textColor = new Color(0.4f, 0.8f, 1f);
            _headerStyle.alignment = TextAnchor.MiddleLeft;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 12;
            _labelStyle.normal.textColor = Color.white;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 11;
            _buttonStyle.fixedHeight = 24;
            _buttonStyle.wordWrap = true;

            _activeButtonStyle = new GUIStyle(GUI.skin.button);
            _activeButtonStyle.fontSize = 11;
            _activeButtonStyle.fixedHeight = 24;
            _activeButtonStyle.normal.background = _activeTex;
            _activeButtonStyle.normal.textColor = Color.white;
            _activeButtonStyle.fontStyle = FontStyle.Bold;
            _activeButtonStyle.wordWrap = true;

            _miniButtonStyle = new GUIStyle(GUI.skin.button);
            _miniButtonStyle.fontSize = 13;
            _miniButtonStyle.fixedHeight = 20;
            _miniButtonStyle.fixedWidth = 22;
            _miniButtonStyle.alignment = TextAnchor.MiddleCenter;

            _sectionStyle = new GUIStyle(GUI.skin.label);
            _sectionStyle.fontSize = 11;
            _sectionStyle.fontStyle = FontStyle.Bold;
            _sectionStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);

            // ---- Cached per-frame styles ----
            // Every one of these used to be `new GUIStyle(...)` inside the draw path.
            // OnGUI runs several times per frame, so that was steady GC churn — the
            // known "TrainingUI GC churn" perf item. Styles whose color varies get
            // mutated in place (alloc-free) instead of recreated.
            _minLabelStyle = new GUIStyle(_headerStyle) { fontSize = 11 };

            _dotStyle = new GUIStyle(_labelStyle);
            _dotStyle.fontSize = 10;
            _dotStyle.normal.textColor = new Color(0.4f, 0.4f, 0.5f);
            _dotStyle.alignment = TextAnchor.MiddleCenter;

            _titleStyle = new GUIStyle(_labelStyle);
            _titleStyle.fontSize = 13;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = new Color(0.45f, 0.85f, 1f);

            _settledStyle = new GUIStyle(_labelStyle);
            _settledStyle.fontStyle = FontStyle.Bold;
            _settledStyle.normal.textColor = new Color(0.45f, 1f, 0.55f);

            _wrapStyle = new GUIStyle(_labelStyle);
            _wrapStyle.wordWrap = true;
            _wrapStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);

            _counterStyle = new GUIStyle(_labelStyle);

            _warnStyle = new GUIStyle(_labelStyle);
            _warnStyle.wordWrap = true;
            _warnStyle.fontStyle = FontStyle.Bold;
            _warnStyle.normal.textColor = new Color(1f, 0.55f, 0.25f);

            _dangerBtnStyle = new GUIStyle(_buttonStyle);
            _dangerBtnStyle.normal.background = _dangerTex;
            _dangerBtnStyle.normal.textColor = Color.white;
            _dangerBtnStyle.fontStyle = FontStyle.Bold;

            _popupTitleStyle = new GUIStyle(_headerStyle);
            _popupTitleStyle.normal.textColor = new Color(1f, 0.6f, 0.3f);

            _popupTextStyle = new GUIStyle(_labelStyle) { wordWrap = true };

            _worldLabelStyle = new GUIStyle(GUI.skin.box);
            _worldLabelStyle.normal.textColor = Color.white;
            _worldLabelStyle.fontStyle = FontStyle.Bold;
            _worldLabelStyle.fontSize = 12;
            _worldLabelStyle.alignment = TextAnchor.MiddleCenter;
            _worldLabelStyle.wordWrap = true;

            _helpNameStyle = new GUIStyle(_labelStyle);
            _helpNameStyle.fontSize = 13;
            _helpNameStyle.fontStyle = FontStyle.Bold;
            _helpNameStyle.normal.textColor = new Color(0.5f, 0.9f, 1f);

            _helpDescStyle = new GUIStyle(_labelStyle);
            _helpDescStyle.fontSize = 11;
            _helpDescStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            _helpDescStyle.wordWrap = true;

            _stylesInit = true;
        }

        private static GUIStyle _minLabelStyle, _dotStyle, _titleStyle, _settledStyle,
            _wrapStyle, _counterStyle, _warnStyle, _dangerBtnStyle,
            _popupTitleStyle, _popupTextStyle, _worldLabelStyle,
            _helpNameStyle, _helpDescStyle;

        // Untrained-map popup state (Play mode, per map)
        private static string _lastSeenMap;
        private static bool _untrainedWarnDismissed;
        private static bool _untrainedEvaluated;

        // Stage-1 coverage stall tracking
        private static int _lastProgressStat = -1;
        private static float _lastProgressTime;
        private static bool _coverageStallWarning;

        /// <summary>Play-mode overlay: ONLY the untrained-map popup. DrawAll is gated
        /// to Training mode by TrainingUIBehaviour, which made the popup unreachable —
        /// it internally requires Mode == Play (it warns players heading into a match
        /// on a map with no learned data).</summary>
        public static void DrawPlayModePopups()
        {
            if (NavGraph.Instance == null || NavGraph.Instance.Mode != NavMode.Play) return;
            // Skip all work (incl. the certification report) once the popup is
            // resolved for the current map.
            if (NavGraph.Instance.CurrentMap == _lastSeenMap && _untrainedWarnDismissed) return;
            InitStyles();
            DrawUntrainedMapPopup(NavGraph.Instance.GetCertificationReport());
        }

        /// <summary>The camera the player is actually looking through. While ALIVE the
        /// first-person camera is a child of the local FPC and is NOT tagged
        /// MainCamera — Camera.main only resolves once the death/spectator camera
        /// takes over, which is why world markers used to appear only when dead.</summary>
        private static Camera GetViewCamera()
        {
            var fcCam = FreeCam.CurrentCamera;
            if (fcCam != null && fcCam.isActiveAndEnabled) return fcCam;
            try
            {
                var ci = ClientInstance.Instance;
                var fpc = ci != null && ci.PlayerSpawner != null ? ci.PlayerSpawner.player : null;
                if (fpc != null)
                {
                    var cam = fpc.GetComponentInChildren<Camera>();
                    if (cam != null && cam.isActiveAndEnabled) return cam;
                }
            }
            catch { }
            return Camera.main;
        }

        public static void DrawAll()
        {
            InitStyles();
            var liveCert = NavGraph.Instance != null ? NavGraph.Instance.GetCertificationReport() : null;

            DrawUntrainedMapPopup(liveCert);

            // World markers: every weapon no bot has visited yet gets a label.
            // Gated behind the Coverage Map toggle — one switch controls all the
            // "what's left to train" world drawing.
            bool inTraining = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
            if (inTraining && liveCert != null
                && (Plugin.ShowCoverageMap?.Value ?? false)
                && liveCert.UnconnectedWeaponPositions != null)
            {
                Camera cam = GetViewCamera();
                if (cam != null)
                {
                    int shown = 0;
                    foreach (var pos in liveCert.UnconnectedWeaponPositions)
                    {
                        DrawWorldLabel(cam, pos, "REACH THIS WEAPON", _dangerTex);
                        if (++shown >= 8) break;
                    }
                }
            }

            float x = _panelPos.x;
            float y = _panelPos.y;

            if (!_expanded)
            {
                // Minimized bar — wide enough for text + button
                float minW = 80f;
                float minH = 26f;
                Rect minBar = new Rect(x, y, minW, minH);
                GUI.Box(minBar, "", _boxStyle);

                // Drag the minimized bar
                Rect dragArea = new Rect(x, y, minW - 26, minH);
                HandleDrag(dragArea);

                GUI.Label(new Rect(x + 4, y + 3, minW - 30, 20), "BOTS", _minLabelStyle);
                if (GUI.Button(new Rect(x + minW - 24, y + 3, 22, 20), "+", _miniButtonStyle))
                    _expanded = true;
                return;
            }

            // Expanded panel — height fits the content exactly (measured last frame,
            // the standard IMGUI one-frame lag); no scroll view, no scroll bar.
            float panelW = 280f;
            float panelH = Mathf.Min(22f + 26f + _lastContentH + 8f, Screen.height - y - 8f);
            Rect panel = new Rect(x, y, panelW, panelH);
            GUI.Box(panel, "", _boxStyle);

            // Drag bar at top
            float dragBarH = 22f;
            Rect dragBar = new Rect(x, y, panelW, dragBarH);
            GUI.DrawTexture(dragBar, _dragBarTex);
            HandleDrag(dragBar);

            // Drag hint dots
            GUI.Label(dragBar, ". . . . .", _dotStyle);

            float cw = panelW - 16f;
            float headerY = y + dragBarH + 2f;

            // Header + help + minimize buttons
            GUI.Label(new Rect(x + 8, headerY, cw - 55, 22), "BOT TRAINING", _headerStyle);
            if (GUI.Button(new Rect(x + panelW - 55, headerY + 1, 22, 20), "?", _miniButtonStyle))
                _helpOpen = !_helpOpen;
            if (GUI.Button(new Rect(x + panelW - 30, headerY + 1, 22, 20), "-", _miniButtonStyle))
            {
                _expanded = false;
                return;
            }

            // Help overlay
            if (_helpOpen)
            {
                DrawHelpPage(x, y + dragBarH);
                return;
            }

            // Content drawn directly on the panel (no scroll view)
            float contentTop = y + dragBarH + 26f;
            float cx = x + 8f;
            float cy = contentTop + 4f;

            // ---- Freecam (top row — quick access while watching bots train) ----
            bool freecam = FreeCam.Active;
            GUIStyle fcStyle = freecam ? _activeButtonStyle : _buttonStyle;
            string fcLabel = freecam ? "Freecam: ON (click to drop in here)" : "Freecam: OFF (detach & fly)";
            if (GUI.Button(new Rect(cx, cy, cw, 24), fcLabel, fcStyle))
                FreeCam.Toggle();
            cy += 28f;

            // ---- Bot count ----
            GUI.Label(new Rect(cx, cy, 80, 18), "Bot Count:", _labelStyle);
            int botCount = Plugin.MaxBots?.Value ?? 3;
            GUI.Label(new Rect(cx + cw - 25, cy, 25, 18), botCount.ToString(), _labelStyle);
            cy += 20f;
            float newBotCount = GUI.HorizontalSlider(new Rect(cx, cy, cw, 16), botCount, 0, 8);
            if (Plugin.MaxBots != null)
                Plugin.MaxBots.Value = Mathf.RoundToInt(newBotCount);
            cy += 22f;

            // ---- Pause (the one manual control besides the stage button) ----
            string pauseLabel = Plugin.TrainingPaused ? "Resume bots" : "Pause bots (walk it yourself)";
            if (GUI.Button(new Rect(cx, cy, cw, 24), pauseLabel,
                    Plugin.TrainingPaused ? _activeButtonStyle : _buttonStyle))
                Plugin.TrainingPaused = !Plugin.TrainingPaused;
            cy += 28f;

            // ---- Get To Me — every bot comes to where you're standing ----
            bool getToMe = Plugin.GetToMe != null && Plugin.GetToMe.Value;
            if (GUI.Button(new Rect(cx, cy, cw, 24),
                    getToMe ? "Get To Me: ON (bots are coming)" : "Get To Me: OFF",
                    getToMe ? _activeButtonStyle : _buttonStyle))
            {
                if (Plugin.GetToMe != null) Plugin.GetToMe.Value = !getToMe;
            }
            cy += 32f;

            // ---- Stage panel ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            if (NavGraph.Instance != null)
            {
                string map = NavGraph.Instance.CurrentMap ?? "?";
                GUI.Label(new Rect(cx, cy, cw, 18), $"Map: {map}", _labelStyle);
                cy += 20f;
                GUI.Label(new Rect(cx, cy, cw, 18), $"Ground nav: {BotNavMesh.Status}", _labelStyle);
                cy += 20f;
                var cert = liveCert ?? NavGraph.Instance.GetCertificationReport();
                cy = DrawStagePanel(cx, cy, cw, cert);
            }

            // Content height for next frame's panel sizing
            _lastContentH = cy - contentTop + 10f;
        }

        private static float DrawStagePanel(float x, float y, float w, MapCertificationReport cert)
        {
            bool inPlay = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Play;

            GUI.Label(new Rect(x, y, w, 20), cert.StageName ?? "Training", _titleStyle);
            y += 22f;

            y = DrawProgressBar(x, y, w, "Progress", cert.StageProgress,
                cert.StageProgress >= 0.99f || cert.StageSettled ? _activeTex : _accentTex);
            y += 4f;

            // Metric stopped moving with bots actively training: say so honestly
            // instead of leaving a bar frozen at an arbitrary percentage.
            if (cert.StageSettled)
            {
                GUI.Label(new Rect(x, y, w, 18),
                    "Nothing new being learned — ready to finish.", _settledStyle);
                y += 20f;
            }

            GUI.Label(new Rect(x, y, w, 34), cert.StageInstruction ?? "", _wrapStyle);
            y += 38f;

            // Weapons no bot has stood at yet (marked in the world too)
            {
                int unvisited = cert.UnconnectedWeaponPositions?.Count ?? 0;
                _counterStyle.normal.textColor = unvisited > 0 ? new Color(1f, 0.7f, 0.3f) : new Color(0.45f, 1f, 0.55f);
                GUI.Label(new Rect(x, y, w, 18),
                    unvisited > 0 ? $"Weapons left to visit: {unvisited} (marked in world)" : "All weapons visited!",
                    _counterStyle);
                y += 22f;
            }

            // Coverage stall warning + manual cleanup of unreachable junk
            if (!inPlay)
            {
                UpdateStallTracking(cert);
                if (_coverageStallWarning)
                {
                    GUI.Label(new Rect(x, y, w, 34),
                        "No new ground reached in 30s — the rest may be\nunreachable. Clear unreachable areas or advance.", _warnStyle);
                    y += 38f;
                }
                if (GUI.Button(new Rect(x, y, w, 24), "Clear Unreachable Areas", _dangerBtnStyle))
                {
                    // Graph side: drop nodes with no route from spawn.
                    NavGraph.Instance?.PruneDisconnectedFromSpawn();
                    // Coverage side: drop mesh islands not connected to walked ground,
                    // graph nodes or spawns by any known means — unwalked-but-legit
                    // areas (jump platforms etc.) survive. Rebuilds the mesh.
                    int dropped = BotNavMesh.PruneAllUnwalked();
                    Plugin.Log.LogInfo($"[Training] Clear Unreachable Areas: {dropped} island cells removed");
                }
                y += 30f;
            }

            if (inPlay)
            {
                if (GUI.Button(new Rect(x, y, w, 26), "Back To Training", _buttonStyle))
                {
                    if (Plugin.NavGraphMode != null) Plugin.NavGraphMode.Value = "Training";
                }
                y += 32f;
            }
            else
            {
                if (GUI.Button(new Rect(x, y, w, 26), cert.NextButtonLabel ?? "Finish: Switch To Play", _activeButtonStyle))
                    NavGraph.Instance?.AdvanceTrainingStage();
                y += 32f;

                // Coverage tint toggle — green walked / orange unwalked ground,
                // plus the REACH THIS WEAPON markers.
                bool covOn = Plugin.ShowCoverageMap != null && Plugin.ShowCoverageMap.Value;
                if (GUI.Button(new Rect(x, y, w, 24), covOn ? "Coverage Map: ON" : "Coverage Map: OFF",
                        covOn ? _activeButtonStyle : _buttonStyle))
                {
                    if (Plugin.ShowCoverageMap != null) Plugin.ShowCoverageMap.Value = !covOn;
                }
                y += 30f;

                // Navmesh wireframe only — lighter than the full bot overlay.
                bool meshOn = Plugin.ShowMeshDebug != null && Plugin.ShowMeshDebug.Value;
                if (GUI.Button(new Rect(x, y, w, 24), meshOn ? "Mesh Debug: ON" : "Mesh Debug: OFF",
                        meshOn ? _activeButtonStyle : _buttonStyle))
                {
                    if (Plugin.ShowMeshDebug != null) Plugin.ShowMeshDebug.Value = !meshOn;
                }
                y += 30f;

                // Debug overlay toggle — same setting as the lobby Bots box button.
                bool overlayOn = Plugin.ShowOverlay != null && Plugin.ShowOverlay.Value;
                if (GUI.Button(new Rect(x, y, w, 24), overlayOn ? "Debug: ON" : "Debug: OFF",
                        overlayOn ? _activeButtonStyle : _buttonStyle))
                {
                    if (Plugin.ShowOverlay != null) Plugin.ShowOverlay.Value = !overlayOn;
                }
                y += 30f;
            }

            return y;
        }

        /// <summary>Top-center popup when joining a map in Play mode that has no learned
        /// data. "Start Training" flips to Training mode at stage 1, unpaused — the
        /// behavior mapper takes it from there.</summary>
        private static void DrawUntrainedMapPopup(MapCertificationReport cert)
        {
            string curMap = NavGraph.Instance != null ? NavGraph.Instance.CurrentMap : null;
            if (curMap != _lastSeenMap)
            {
                _lastSeenMap = curMap;
                _untrainedWarnDismissed = false;
                _untrainedEvaluated = false;
            }
            if (_untrainedWarnDismissed || string.IsNullOrEmpty(curMap) || cert == null) return;
            if (NavGraph.Instance == null || NavGraph.Instance.Mode != NavMode.Play) return;
            // Trained-or-not is decided ONCE per map, right after its data loads
            // (CurrentMap is set by LoadForMap, so cert reflects this map's data).
            // A map that STARTS play untrained keeps the prompt up until the user
            // answers — bots learning nodes during play used to auto-dismiss it
            // mid-round before anyone could click.
            if (!_untrainedEvaluated)
            {
                _untrainedEvaluated = true;
                if (cert.ActiveNodes >= 30) { _untrainedWarnDismissed = true; return; }
            }

            float w = 430f, h = 100f;
            float px = (Screen.width - w) * 0.5f, py = 56f;
            GUI.Box(new Rect(px, py, w, h), "", _boxStyle);
            GUI.DrawTexture(new Rect(px, py, w, 4), _dangerTex);

            GUI.Label(new Rect(px + 10, py + 8, w - 20, 20), "UNTRAINED MAP", _popupTitleStyle);

            GUI.Label(new Rect(px + 10, py + 27, w - 20, 36),
                "Bots have no training data for this map. They can walk the ground but " +
                "won't know jumps, ladders or weapon routes.", _popupTextStyle);

            if (GUI.Button(new Rect(px + 10, py + h - 32, 140, 24), "Start Training", _activeButtonStyle))
            {
                if (NavGraph.Instance != null) NavGraph.Instance.TrainingStage = 1;
                Plugin.TrainingPaused = false;
                if (Plugin.NavGraphMode != null) Plugin.NavGraphMode.Value = "Training";
                _untrainedWarnDismissed = true;
                Plugin.Log.LogInfo("[Training] Untrained-map popup: training started at stage 1");
            }
            if (GUI.Button(new Rect(px + 160, py + h - 32, 100, 24), "Keep Playing", _buttonStyle))
                _untrainedWarnDismissed = true;
        }

        /// <summary>Stage-1 stall detector: warn when neither walked coverage nor the
        /// node count has grown for 30 seconds (bots probably can't reach anything new).</summary>
        private static void UpdateStallTracking(MapCertificationReport cert)
        {
            int stat = BotNavMesh.WalkedCellCount + cert.ActiveNodes;
            if (stat != _lastProgressStat || Plugin.TrainingPaused)
            {
                _lastProgressStat = stat;
                _lastProgressTime = Time.time;
                _coverageStallWarning = false;
                return;
            }
            _coverageStallWarning = Time.time - _lastProgressTime >= 30f;
        }

        private static float DrawProgressBar(float x, float y, float w, string label, float value, Texture2D fill)
        {
            value = Mathf.Clamp01(value);
            GUI.Label(new Rect(x, y, w, 16), $"{label}: {value * 100f:F0}%", _labelStyle);
            y += 15f;
            GUI.DrawTexture(new Rect(x, y, w, 8), _dragBarTex);
            GUI.DrawTexture(new Rect(x, y, Mathf.Max(2f, w * value), 8), fill);
            return y + 14f;
        }

        private static void DrawWorldLabel(Camera cam, Vector3 worldPos, string label, Texture2D background)
        {
            Vector3 screen = cam.WorldToScreenPoint(worldPos + Vector3.up * 1.4f);
            if (screen.z <= 0f) return;

            float x = screen.x;
            float y = Screen.height - screen.y;
            _worldLabelStyle.normal.background = background; // mutate, don't allocate

            GUI.Box(new Rect(x - 85f, y - 26f, 170f, 38f), label, _worldLabelStyle);
            GUI.Label(new Rect(x - 10f, y + 9f, 20f, 22f), "v", _worldLabelStyle);
        }

        private static void DrawHelpPage(float x, float y)
        {
            float w = 420f;
            float h = 480f;
            Rect bg = new Rect(x, y, w, h);
            GUI.Box(bg, "", _boxStyle);

            float cx = x + 10f;
            float cy = y + 6f;
            float cw = w - 20f;

            // Title
            GUI.Label(new Rect(cx, cy, cw - 30, 22), "TRAINING GUIDE", _headerStyle);
            if (GUI.Button(new Rect(x + w - 30, cy + 1, 22, 20), "X", _miniButtonStyle))
                _helpOpen = false;
            cy += 28f;
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 4f;

            // Scrollable content
            Rect scrollView = new Rect(cx, cy, cw, h - (cy - y) - 8f);
            float contentH = 760f;
            _helpScrollPos = GUI.BeginScrollView(scrollView, _helpScrollPos, new Rect(0, 0, cw - 20, contentH));

            float ty = 0f;
            float tw = cw - 24f;

            ty = HelpSection(ty, tw, "HOW TRAINING WORKS");
            ty = HelpEntry(ty, tw, "Walking is automatic",
                "A ground navigation mesh is generated for every map at load\n" +
                "(cyan wireframe in the overlay). Bots can walk anywhere\n" +
                "immediately — training only teaches jumps, ladders and\n" +
                "special routes the mesh can't walk.");
            ty = HelpEntry(ty, tw, "Stage 1 — Explore",
                "Bots and you run around the map. Every area you or the\n" +
                "bots reach gets connected. Your own routes are trusted\n" +
                "instantly, so walking tricky jumps yourself teaches fastest.");
            ty = HelpEntry(ty, tw, "Stage 2 — Weapons",
                "Pressing Next Stage first deletes every node and path\n" +
                "that isn't connected to the map. Weapons without a\n" +
                "working route get a red world marker — bots (and you)\n" +
                "focus on reaching them until all weapons are linked.");
            ty = HelpEntry(ty, tw, "Stage 3 — Confirmation",
                "Bots run routes all over the map and confirm everything\n" +
                "is walkable. Routes they complete become trusted.\n" +
                "When it looks good, hit Finish to switch to Play.");
            ty = HelpEntry(ty, tw, "Next Stage button",
                "The single control: it advances 1 -> 2 -> 3 -> Play.\n" +
                "Everything inside a stage runs by itself.");
            ty = HelpEntry(ty, tw, "Pause bots",
                "Freezes the bots so you can walk routes yourself\n" +
                "without them in the way. Resume when done.");

            ty += 6f;
            ty = HelpSection(ty, tw, "TIPS");
            ty = HelpEntry(ty, tw, "Freecam",
                "Detach the camera and fly around to watch bots train.\n" +
                "Turning it off drops your player at the camera's position.");
            ty = HelpEntry(ty, tw, "Advanced settings",
                "Bot count, overlay, freeze/clear map data live in the\n" +
                "mod menu (F1) — kept out of this panel on purpose.");

            GUI.EndScrollView();
        }

        private static float HelpSection(float y, float w, string title)
        {
            y += 4f; // padding above section
            GUI.DrawTexture(new Rect(4, y, w, 1), _accentTex);
            y += 6f;
            GUI.Label(new Rect(4, y, w, 20), title, _sectionStyle);
            return y + 24f;
        }

        private static float HelpEntry(float y, float w, string name, string desc)
        {
            GUI.Label(new Rect(8, y, w, 20), name, _helpNameStyle);
            y += 22f;

            int lines = desc.Split('\n').Length;
            float descH = lines * 15f + 6f;
            GUI.Label(new Rect(12, y, w - 8, descH), desc, _helpDescStyle);
            return y + descH + 8f;
        }

        private static void HandleDrag(Rect dragArea)
        {
            Event e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (dragArea.Contains(e.mousePosition))
                    {
                        _isDragging = true;
                        _dragOffset = e.mousePosition - _panelPos;
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isDragging)
                    {
                        _panelPos = e.mousePosition - _dragOffset;
                        // Clamp to screen
                        _panelPos.x = Mathf.Clamp(_panelPos.x, 0, Screen.width - 80);
                        _panelPos.y = Mathf.Clamp(_panelPos.y, 0, Screen.height - 30);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isDragging)
                    {
                        _isDragging = false;
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }
        }

        private static Texture2D MakeTex(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            var tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
