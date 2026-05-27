using UnityEngine;

namespace StraftatBots
{
    public static class TrainingUI
    {
        private static bool _expanded = true;
        private static bool _helpOpen = false;
        private static Vector2 _scrollPos = Vector2.zero;
        private static Vector2 _helpScrollPos = Vector2.zero;
        private static float _lastContentH = 800f; // auto-sized from previous frame

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
        private static GUIStyle _toggleStyle;
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

            _toggleStyle = new GUIStyle(GUI.skin.toggle);
            _toggleStyle.fontSize = 12;
            _toggleStyle.normal.textColor = Color.white;
            _toggleStyle.onNormal.textColor = Color.white;

            _stylesInit = true;
        }

        public static void DrawAll()
        {
            InitStyles();

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

                var minLabel = new GUIStyle(_headerStyle);
                minLabel.fontSize = 11;
                GUI.Label(new Rect(x + 4, y + 3, minW - 30, 20), "BOTS", minLabel);
                if (GUI.Button(new Rect(x + minW - 24, y + 3, 22, 20), "+", _miniButtonStyle))
                    _expanded = true;
                return;
            }

            // Expanded panel
            float panelW = 250f;
            float panelH = 520f;
            Rect panel = new Rect(x, y, panelW, panelH);
            GUI.Box(panel, "", _boxStyle);

            // Drag bar at top
            float dragBarH = 22f;
            Rect dragBar = new Rect(x, y, panelW, dragBarH);
            GUI.DrawTexture(dragBar, _dragBarTex);
            HandleDrag(dragBar);

            // Drag hint dots
            var dotStyle = new GUIStyle(_labelStyle);
            dotStyle.fontSize = 10;
            dotStyle.normal.textColor = new Color(0.4f, 0.4f, 0.5f);
            dotStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(dragBar, ". . . . .", dotStyle);

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

            // Scrollable content area — height auto-sized from previous frame
            float scrollTop = y + dragBarH + 26f;
            Rect scrollViewRect = new Rect(x, scrollTop, panelW, panelH - dragBarH - 26f);
            _scrollPos = GUI.BeginScrollView(scrollViewRect, _scrollPos, new Rect(0, 0, panelW - 20, _lastContentH));

            float cx = 8f;
            float cy = 4f;

            // ---- Freecam (top row — quick access while watching bots train) ----
            bool freecam = FreeCam.Active;
            GUIStyle fcStyle = freecam ? _activeButtonStyle : _buttonStyle;
            string fcLabel = freecam ? "Freecam: ON (click to return)" : "Freecam: OFF (detach & fly)";
            if (GUI.Button(new Rect(cx, cy, cw, 24), fcLabel, fcStyle))
                FreeCam.Toggle();
            cy += 28f;

            // ---- Bot Behavior ----
            GUI.Label(new Rect(cx, cy, cw, 18), "BEHAVIOR", _sectionStyle);
            cy += 20f;

            string[] behaviors = { "None", "Explore" };
            string[] configValues = { "None", "Explore" };
            string current = Plugin.TrainingBehavior?.Value ?? "Explore";

            for (int i = 0; i < behaviors.Length; i++)
            {
                bool active = current == configValues[i];
                GUIStyle style = active ? _activeButtonStyle : _buttonStyle;
                float bw = cw / 2f - 2f;
                float bx = cx + i * (bw + 4f);

                if (GUI.Button(new Rect(bx, cy, bw, 24), behaviors[i], style))
                {
                    if (Plugin.TrainingBehavior != null)
                        Plugin.TrainingBehavior.Value = configValues[i];
                }
            }
            cy += 32f;

            // ---- Bots ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            GUI.Label(new Rect(cx, cy, cw, 18), "BOTS", _sectionStyle);
            cy += 20f;

            GUI.Label(new Rect(cx, cy, 80, 18), "Bot Count:", _labelStyle);
            int botCount = Plugin.MaxBots?.Value ?? 3;
            GUI.Label(new Rect(cx + cw - 25, cy, 25, 18), botCount.ToString(), _labelStyle);
            cy += 20f;
            float newBotCount = GUI.HorizontalSlider(new Rect(cx, cy, cw, 16), botCount, 0, 8);
            if (Plugin.MaxBots != null)
                Plugin.MaxBots.Value = Mathf.RoundToInt(newBotCount);
            cy += 22f;

            if (GUI.Button(new Rect(cx, cy, cw, 24), "Respawn All Bots", _buttonStyle))
            {
                if (BotManager.Instance != null)
                {
                    BotManager.Instance.DespawnAllBots();
                    BotManager.Instance.LobbyBots.Clear();
                    int count = Plugin.MaxBots?.Value ?? 3;
                    for (int bi = 0; bi < count; bi++)
                        BotManager.Instance.AddBot();
                    BotManager.Instance.SpawnAllBots();
                }
            }
            cy += 28f;

            // ---- Teach (Watch Me) ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            GUI.Label(new Rect(cx, cy, cw, 18), "TEACH", _sectionStyle);
            cy += 20f;

            bool watching = PlayerRecorder.WatchMeActive;
            string wmLabel = watching
                ? $"Stop Watching ({PlayerRecorder.WatchMeSampleCount} nodes)"
                : "Watch Me: Start Recording";
            GUIStyle wmStyle = watching ? _activeButtonStyle : _buttonStyle;
            if (GUI.Button(new Rect(cx, cy, cw, 24), wmLabel, wmStyle))
            {
                if (watching) PlayerRecorder.StopWatchMe();
                else          PlayerRecorder.StartWatchMe();
            }
            cy += 28f;

            if (watching)
            {
                if (GUI.Button(new Rect(cx, cy, cw, 24), "Cancel (discard)", _buttonStyle))
                    PlayerRecorder.CancelWatchMe();
                cy += 28f;

                GUI.Label(new Rect(cx, cy, cw, 16),
                    $"Recording: {PlayerRecorder.WatchMeName}", _sectionStyle);
                cy += 18f;
            }
            else
            {
                int routeCount = NavGraph.Instance != null ? NavGraph.Instance.ProvenRoutes.Count : 0;
                GUI.Label(new Rect(cx, cy, cw, 16),
                    $"Saved routes: {routeCount}", _sectionStyle);
                cy += 18f;

                if (routeCount > 0 && GUI.Button(new Rect(cx, cy, cw, 24), "Clear Proven Routes", _buttonStyle))
                {
                    if (NavGraph.Instance != null)
                    {
                        NavGraph.Instance.ClearProvenRoutes();
                        NavGraph.Instance.Save();
                    }
                }
                if (routeCount > 0) cy += 28f;
            }

            // ---- Graph Settings ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            GUI.Label(new Rect(cx, cy, cw, 18), "GRAPH", _sectionStyle);
            cy += 20f;

            cy = DrawToggle(cx, cy, cw, "Freeze Map Data", Plugin.LockGraph);

            // ---- Debug ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            GUI.Label(new Rect(cx, cy, cw, 18), "DEBUG VISUALS", _sectionStyle);
            cy += 20f;

            cy = DrawToggle(cx, cy, cw, "Show Overlay", Plugin.ShowOverlay);

            // ---- Map Data ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            GUI.Label(new Rect(cx, cy, cw, 18), "MAP DATA", _sectionStyle);
            cy += 20f;

            // Danger zone
            var dangerStyle = new GUIStyle(_buttonStyle);
            dangerStyle.normal.background = _dangerTex;
            dangerStyle.normal.textColor = Color.white;
            if (GUI.Button(new Rect(cx, cy, cw, 24), "CLEAR ALL MAP DATA", dangerStyle))
            {
                if (NavGraph.Instance != null && !string.IsNullOrEmpty(NavGraph.Instance.CurrentMap))
                {
                    string map = NavGraph.Instance.CurrentMap;
                    string pluginDir = System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string path = System.IO.Path.Combine(pluginDir, "NavData", $"{map}.bin");
                    try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
                    Plugin.CustomPatrolLocations.Clear();
                    NavGraph.Instance.LoadForMap(map);
                    NavGraph.Instance.RegisterMapLocations();
                    Plugin.Log.LogInfo($"[NavGraph] Cleared all data for {map} — weapon nodes restored");
                }
            }
            cy += 32f;

            // ---- Stats ----
            GUI.DrawTexture(new Rect(cx, cy, cw, 1), _accentTex);
            cy += 6f;
            GUI.Label(new Rect(cx, cy, cw, 18), "STATUS", _sectionStyle);
            cy += 20f;
            if (NavGraph.Instance != null)
            {
                int nodes = NavGraph.Instance.NodeCount;
                int edges = NavGraph.Instance.EdgeCount;
                string map = NavGraph.Instance.CurrentMap ?? "?";
                GUI.Label(new Rect(cx, cy, cw, 18), $"{map}: {nodes} nodes, {edges} edges", _labelStyle);
                cy += 20f;
                string behavior = Plugin.TrainingBehavior?.Value ?? "?";
                GUI.Label(new Rect(cx, cy, cw, 18), $"Mode: {behavior}", _labelStyle);
                cy += 20f;
            }

            // Store content height for next frame so scroll area auto-sizes
            _lastContentH = cy + 40f;

            GUI.EndScrollView();
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
            float contentH = 1900f;
            _helpScrollPos = GUI.BeginScrollView(scrollView, _helpScrollPos, new Rect(0, 0, cw - 20, contentH));

            float ty = 0f;
            float tw = cw - 24f;

            ty = HelpSection(ty, tw, "BEHAVIOR MODES");
            ty = HelpEntry(ty, tw, "None",
                "Bots freeze in place. Use when you want to walk the map\n" +
                "yourself and train paths manually without bots moving.");
            ty = HelpEntry(ty, tw, "Explore",
                "Bots autonomously explore the map. They seek ladders,\n" +
                "ramps, jump onto ledges, probe gaps, and walk along edges\n" +
                "to discover routes. All movement is recorded as proven paths.");
            ty += 6f;
            ty = HelpSection(ty, tw, "RECORDING OPTIONS");
            ty = HelpEntry(ty, tw, "Special Edges Only",
                "Only record jumps, falls, slides, wall jumps, and ladders.\n" +
                "Walk nodes are NOT created. Use to map trick jumps first.");
            ty = HelpEntry(ty, tw, "Walk Nodes Only",
                "Only create walk nodes and edges. No special movement\n" +
                "recorded. Use to fill in walkable terrain.");
            ty = HelpEntry(ty, tw, "Player Data Only",
                "Only YOUR movement creates navigation data. Bots follow\n" +
                "but don't add nodes. For precise manual training.");
            ty = HelpEntry(ty, tw, "Pause Recording",
                "Stop recording your movement. Existing data is kept.\n" +
                "Walk around without adding more paths.");
            ty = HelpEntry(ty, tw, "Erase Mode",
                "Walk around to DELETE nearby nodes instead of creating\n" +
                "them. Use to clean up bad areas of the graph.");

            ty += 6f;
            ty = HelpSection(ty, tw, "SCANNING");
            ty = HelpEntry(ty, tw, "Player/Bot Ground Scan",
                "Scan ground in 8 directions around you/bots as you move.\n" +
                "Adds nodes to nearby walkable surfaces without walking\n" +
                "directly on them. Range set in mod menu (Graph section).");

            ty += 6f;
            ty = HelpSection(ty, tw, "BOTS");
            ty = HelpEntry(ty, tw, "Bot Count",
                "Number of bots to spawn (0-8). Change and respawn to apply.");
            ty = HelpEntry(ty, tw, "Respawn All Bots",
                "Despawn all bots and spawn fresh with the current count.\n" +
                "Use mid-match to reset stuck bots.");
            ty += 6f;
            ty = HelpSection(ty, tw, "DEBUG VISUALS");
            ty = HelpEntry(ty, tw, "Show Nodes",
                "Draw colored markers at nav nodes. Green=player,\n" +
                "yellow/red=bot confidence. Special nodes get unique shapes.");
            ty = HelpEntry(ty, tw, "Show Edges",
                "Draw lines between connected nodes. White=walk, blue=jump,\n" +
                "green=ladder, purple=slide, orange=wall jump, red=fall.");
            ty = HelpEntry(ty, tw, "Show Bot Paths / Markers / Info",
                "Yellow path lines, state rings above bots, text overlays\n" +
                "showing name, state, path progress, and flags.");

            ty += 6f;
            ty = HelpSection(ty, tw, "GRAPH");
            ty = HelpEntry(ty, tw, "Player / Bot Density",
                "How close together nodes are placed (1-10).\n" +
                "1 = very detailed, many nodes. 10 = sparse, few nodes.\n" +
                "5 is default. Lower = more accurate paths but more data.");
            ty = HelpEntry(ty, tw, "Scan Range",
                "How far ground scanning reaches in meters (2-30).\n" +
                "Only active when a Ground Scan toggle is on.\n" +
                "Also controls Erase Mode deletion radius.");
            ty = HelpEntry(ty, tw, "Max Player / Bot Nodes",
                "Maximum nodes per map. When exceeded, oldest and\n" +
                "lowest-confidence nodes are pruned. Bot nodes pruned\n" +
                "first. Ignored when Freeze Map Data is on.");
            ty = HelpEntry(ty, tw, "Auto-Save Interval",
                "How often the graph saves to disk (seconds).\n" +
                "Lower = safer against crashes but more disk writes.");
            ty = HelpEntry(ty, tw, "Freeze Map Data",
                "Completely freeze the navigation graph. Nothing is\n" +
                "created, deleted, or modified. Use when you're happy\n" +
                "with trained data and want to preserve it exactly.");
            ty = HelpEntry(ty, tw, "Auto-Optimize",
                "Allow graph cleanup in Play mode (merge nearby nodes,\n" +
                "remove bad edges). Normally Play preserves all data.\n" +
                "Enable if the graph feels bloated.");

            ty += 6f;
            ty = HelpSection(ty, tw, "PATROL LOCATIONS");
            ty = HelpEntry(ty, tw, "Place Patrol Location",
                "Drop a custom patrol point at your current position.\n" +
                "Bots in Connect mode will path to it like a weapon spawn.\n" +
                "Max 20 locations per map.");

            ty += 6f;
            ty = HelpSection(ty, tw, "MAP DATA");
            ty = HelpEntry(ty, tw, "Remove Orphan Nodes",
                "Delete nodes with zero connections. Cleans up stray\n" +
                "dots that bots can't reach.");
            ty = HelpEntry(ty, tw, "Clear Player / Bot Nodes",
                "Delete only player-created or bot-created nodes.\n" +
                "Useful for selective cleanup without losing all data.");
            ty = HelpEntry(ty, tw, "Clear Special Edges",
                "Delete all jump, fall, slide, wall jump, and ladder edges.\n" +
                "Walk nodes and edges are kept. Resets trick jumps.");
            ty = HelpEntry(ty, tw, "Keep Connected Routes Only",
                "Delete everything except proven routes between locations.\n" +
                "Keeps patrol routes + weapon/spawn nodes. Run after\n" +
                "Connect mode completes. Clean starting point for training.");
            ty = HelpEntry(ty, tw, "CLEAR ALL MAP DATA",
                "Delete ALL navigation data for this map. Cannot undo.\n" +
                "Starts completely fresh. The button is red for a reason.");

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
            var nameStyle = new GUIStyle(_labelStyle);
            nameStyle.fontSize = 13;
            nameStyle.fontStyle = FontStyle.Bold;
            nameStyle.normal.textColor = new Color(0.5f, 0.9f, 1f);
            GUI.Label(new Rect(8, y, w, 20), name, nameStyle);
            y += 22f;

            var descStyle = new GUIStyle(_labelStyle);
            descStyle.fontSize = 11;
            descStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            descStyle.wordWrap = true;

            int lines = desc.Split('\n').Length;
            float descH = lines * 15f + 6f;
            GUI.Label(new Rect(12, y, w - 8, descH), desc, descStyle);
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

        private static float DrawToggle(float x, float y, float w, string label,
            BepInEx.Configuration.ConfigEntry<bool> config)
        {
            bool val = config?.Value ?? false;
            bool newVal = GUI.Toggle(new Rect(x, y, w, 20), val, " " + label, _toggleStyle);
            if (newVal != val && config != null)
                config.Value = newVal;
            return y + 22f;
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
