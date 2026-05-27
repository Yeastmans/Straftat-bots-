using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    /// <summary>
    /// OnGUI proxy — attaches to active camera so text persists through host death.
    /// </summary>
    public class BotVizTextProxy : MonoBehaviour
    {
        private static BotVizTextProxy _active;
        private void OnGUI()
        {
            if (_active != null && _active != this && _active.isActiveAndEnabled)
            {
                Destroy(this);
                return;
            }
            _active = this;
            try { BotDebugVisualizer.DrawText(GetComponent<Camera>()); }
            catch { }
        }
        private void OnDestroy() { if (_active == this) _active = null; }
    }

    /// <summary>
    /// Debug visualizer. All rendering controlled by Plugin config toggles.
    /// </summary>
    public static class BotDebugVisualizer
    {
        private static Material _glMat;
        private static bool _registered;
        private static bool _textProxyAttached;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            CreateMaterials();
            Camera.onPostRender -= OnPostRender; // Remove first in case of hot-reload
            Camera.onPostRender += OnPostRender;
            Plugin.Log.LogInfo("[BotViz] Registered");
        }

        public static void Unregister()
        {
            Camera.onPostRender -= OnPostRender;
            _registered = false;
            _textProxyAttached = false;
        }

        static void CreateMaterials()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                _glMat = new Material(shader);
                _glMat.hideFlags = HideFlags.HideAndDontSave;
                _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _glMat.SetInt("_ZWrite", 0);
                _glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
        }

        // ============ GL RENDERING ============

        static void OnPostRender(Camera cam)
        {
            if (cam == null || _glMat == null) return;

            if (cam.targetTexture != null) return;
            foreach (var c in Camera.allCameras)
            {
                if (c.targetTexture != null) continue;
                if (c.depth < cam.depth) return;
            }

            // Single overlay toggle — covers nodes, edges, paths, markers, and bot info text.
            bool showOverlay = Plugin.ShowOverlay?.Value ?? true;
            bool showText = showOverlay;
            if (showText && (!_textProxyAttached || cam.GetComponent<BotVizTextProxy>() == null))
            {
                foreach (var old in Object.FindObjectsOfType<BotVizTextProxy>())
                    Object.Destroy(old);
                cam.gameObject.AddComponent<BotVizTextProxy>();
                _textProxyAttached = true;
            }

            bool showNodes = showOverlay;
            bool showEdges = showOverlay;
            bool showPaths = showOverlay;
            bool showIndicators = showOverlay;

            if (!showOverlay) return;

            _glMat.SetPass(0);
            GL.PushMatrix();
            try
            {
                GL.LoadProjectionMatrix(GL.GetGPUProjectionMatrix(cam.projectionMatrix, false));
                GL.modelview = cam.worldToCameraMatrix;

                if (showNodes) DrawGraphNodes(cam);
                if (showEdges) DrawGraphEdges(cam);
                if (showPaths) DrawBotPaths();
                if (showIndicators) DrawBotIndicators();

                // Draw red orbs on blacklisted weapons
                if (Plugin.BlacklistedWeaponNodes.Count > 0 && NavGraph.Instance != null)
                    DrawBlacklistedWeapons();
            }
            finally
            {
                GL.PopMatrix();
            }
        }

        // ============ BLACKLISTED WEAPONS ============

        static void DrawBlacklistedWeapons()
        {
            foreach (var (pos, label, nodeId) in NavGraph.Instance.MapLocations)
            {
                if (!Plugin.BlacklistedWeaponNodes.Contains(nodeId)) continue;

                // Red pulsing orb
                float pulse = 0.7f + 0.3f * Mathf.Sin(Time.time * 3f);
                GL.Color(new Color(1f, 0f, 0f, pulse));
                Vector3 p = pos + Vector3.up * 0.5f;
                float r = 1.5f;
                int seg = 16;

                // Draw 3 circles (XZ, XY, YZ) to form a sphere outline
                GL.Begin(GL.LINES);
                for (int i = 0; i < seg; i++)
                {
                    float a1 = (i / (float)seg) * Mathf.PI * 2f;
                    float a2 = ((i + 1) / (float)seg) * Mathf.PI * 2f;
                    // XZ circle
                    GL.Vertex3(p.x + Mathf.Cos(a1) * r, p.y, p.z + Mathf.Sin(a1) * r);
                    GL.Vertex3(p.x + Mathf.Cos(a2) * r, p.y, p.z + Mathf.Sin(a2) * r);
                    // XY circle
                    GL.Vertex3(p.x + Mathf.Cos(a1) * r, p.y + Mathf.Sin(a1) * r, p.z);
                    GL.Vertex3(p.x + Mathf.Cos(a2) * r, p.y + Mathf.Sin(a2) * r, p.z);
                    // YZ circle
                    GL.Vertex3(p.x, p.y + Mathf.Cos(a1) * r, p.z + Mathf.Sin(a1) * r);
                    GL.Vertex3(p.x, p.y + Mathf.Cos(a2) * r, p.z + Mathf.Sin(a2) * r);
                }
                GL.End();
            }
        }

        // ============ GRAPH VISUALIZATION ============

        static void DrawGraphNodes(Camera cam)
        {
            if (NavGraph.Instance == null) return;
            var nodes = NavGraph.Instance.Nodes;
            if (nodes == null || nodes.Count == 0) return;

            Vector3 camPos = cam.transform.position;
            float maxDrawDist = 50f;

            GL.Begin(GL.LINES);
            foreach (var node in nodes)
            {
                if (node == null || node.Confidence <= 0) continue;
                float dist = Vector3.Distance(node.Position, camPos);
                if (dist > maxDrawDist) continue;

                float alpha = Mathf.Lerp(0.15f, 0.8f, 1f - dist / maxDrawDist);

                // Check if this node is an endpoint of special edges
                bool isJumpNode = false, isLadderNode = false, isSlideNode = false, isWallJumpNode = false;
                bool isMapLocation = false;
                bool isPatrolPoint = false;
                bool isTeleporter = false;
                if (NavGraph.Instance != null)
                {
                    var edgesFrom = NavGraph.Instance.GetEdgesFrom(node.Id);
                    foreach (var e in edgesFrom)
                    {
                        if (e.Type == EdgeType.Jump) isJumpNode = true;
                        else if (e.Type == EdgeType.Ladder) isLadderNode = true;
                        else if (e.Type == EdgeType.Slide) isSlideNode = true;
                        else if (e.Type == EdgeType.WallJump) isWallJumpNode = true;
                        else if (e.Type == EdgeType.Teleporter) isTeleporter = true;
                    }
                    foreach (var (pos2, label, nodeId) in NavGraph.Instance.MapLocations)
                    {
                        if (nodeId == node.Id)
                        {
                            isMapLocation = true;
                            if (label == "PatrolPoint") isPatrolPoint = true;
                            if (label == "Teleporter") isTeleporter = true;
                            break;
                        }
                    }
                }

                // Color by node type — most specific wins
                Color col;
                float s;
                if (isTeleporter)
                {
                    col = new Color(1f, 0.4f, 0.9f, Mathf.Min(alpha * 1.8f, 1f)); // PINK = teleporter
                    s = 0.5f;
                }
                else if (isPatrolPoint)
                {
                    col = new Color(1f, 0.1f, 0.8f, Mathf.Min(alpha * 1.8f, 1f)); // HOT PINK = player patrol point
                    s = 0.5f;
                }
                else if (isMapLocation)
                {
                    col = new Color(1f, 1f, 1f, alpha);     // WHITE = weapon/spawn location
                    s = 0.4f;
                }
                else if (isLadderNode)
                {
                    col = new Color(0f, 1f, 0.8f, Mathf.Min(alpha * 1.3f, 1f)); // TURQUOISE = ladder
                    s = 0.3f;
                }
                else if (isWallJumpNode)
                {
                    col = new Color(1f, 0.4f, 0f, Mathf.Min(alpha * 1.3f, 1f)); // BRIGHT ORANGE = wall jump
                    s = 0.3f;
                }
                else if (isSlideNode)
                {
                    col = new Color(0.8f, 0f, 1f, Mathf.Min(alpha * 1.3f, 1f)); // PURPLE = slide
                    s = 0.3f;
                }
                else if (isJumpNode)
                {
                    col = new Color(0f, 0.4f, 1f, Mathf.Min(alpha * 1.3f, 1f)); // BRIGHT BLUE = jump endpoint
                    s = 0.3f;
                }
                else if (node.PlayerSourced)
                {
                    col = new Color(0f, 0.8f, 0.3f, alpha); // GREEN = player-sourced walk
                    s = 0.12f;
                }
                else if (node.Confidence > 0.7f)
                {
                    col = new Color(0.5f, 0.7f, 0f, alpha * 0.7f); // DIM LIME = high conf bot
                    s = 0.08f;
                }
                else if (node.Confidence > 0.3f)
                {
                    col = new Color(0.7f, 0.7f, 0f, alpha * 0.5f); // DIM YELLOW = medium
                    s = 0.06f;
                }
                else
                {
                    col = new Color(0.6f, 0.15f, 0.15f, alpha * 0.4f); // DIM RED = low confidence
                    s = 0.05f;
                }

                GL.Color(col);
                Vector3 p = node.Position + Vector3.up * 0.1f;

                // Special nodes: 3D shapes to stand out from walk nodes
                if (isTeleporter)
                {
                    // Pink circle at ground level + vertical ring
                    float r = 0.6f;
                    int segs = 16;
                    for (int ci = 0; ci < segs; ci++)
                    {
                        float a1 = ci * Mathf.PI * 2f / segs;
                        float a2 = (ci + 1) * Mathf.PI * 2f / segs;
                        // Horizontal circle
                        GL.Vertex3(p.x + Mathf.Cos(a1) * r, p.y, p.z + Mathf.Sin(a1) * r);
                        GL.Vertex3(p.x + Mathf.Cos(a2) * r, p.y, p.z + Mathf.Sin(a2) * r);
                        // Vertical ring
                        GL.Vertex3(p.x + Mathf.Cos(a1) * r, p.y + Mathf.Sin(a1) * r, p.z);
                        GL.Vertex3(p.x + Mathf.Cos(a2) * r, p.y + Mathf.Sin(a2) * r, p.z);
                    }
                    // Center vertical line
                    GL.Vertex3(p.x, p.y - 0.2f, p.z); GL.Vertex3(p.x, p.y + 1.2f, p.z);
                }
                else if (isJumpNode || isWallJumpNode || isLadderNode || isSlideNode)
                {
                    // Vertical spike — visible from distance
                    GL.Vertex3(p.x, p.y, p.z); GL.Vertex3(p.x, p.y + s * 2f, p.z);
                    // Double diamond (horizontal + slightly raised) for 3D look
                    float d = s * 0.7f;
                    GL.Vertex3(p.x, p.y, p.z - d); GL.Vertex3(p.x + d, p.y, p.z);
                    GL.Vertex3(p.x + d, p.y, p.z); GL.Vertex3(p.x, p.y, p.z + d);
                    GL.Vertex3(p.x, p.y, p.z + d); GL.Vertex3(p.x - d, p.y, p.z);
                    GL.Vertex3(p.x - d, p.y, p.z); GL.Vertex3(p.x, p.y, p.z - d);
                    // Upper diamond
                    float u = s * 0.5f;
                    float uy = p.y + s;
                    GL.Vertex3(p.x, uy, p.z - u); GL.Vertex3(p.x + u, uy, p.z);
                    GL.Vertex3(p.x + u, uy, p.z); GL.Vertex3(p.x, uy, p.z + u);
                    GL.Vertex3(p.x, uy, p.z + u); GL.Vertex3(p.x - u, uy, p.z);
                    GL.Vertex3(p.x - u, uy, p.z); GL.Vertex3(p.x, uy, p.z - u);
                }
                else if (node.NearEdge)
                {
                    // Diamond shape — 4 lines forming a rotated square
                    GL.Vertex3(p.x, p.y, p.z - s); GL.Vertex3(p.x + s, p.y, p.z);
                    GL.Vertex3(p.x + s, p.y, p.z); GL.Vertex3(p.x, p.y, p.z + s);
                    GL.Vertex3(p.x, p.y, p.z + s); GL.Vertex3(p.x - s, p.y, p.z);
                    GL.Vertex3(p.x - s, p.y, p.z); GL.Vertex3(p.x, p.y, p.z - s);
                }
                else
                {
                    // Cross shape — small for walk nodes
                    GL.Vertex3(p.x - s, p.y, p.z); GL.Vertex3(p.x + s, p.y, p.z);
                    GL.Vertex3(p.x, p.y, p.z - s); GL.Vertex3(p.x, p.y, p.z + s);
                }
                // Patrol points: thick square base + vertical pole + floating 3D diamond at 2m
                if (isPatrolPoint)
                {
                    float sq = 0.4f;
                    // Thick square base (draw 3 offset squares for thickness)
                    for (float yo = 0f; yo <= 0.04f; yo += 0.02f)
                    {
                        GL.Vertex3(p.x - sq, p.y + yo, p.z - sq); GL.Vertex3(p.x + sq, p.y + yo, p.z - sq);
                        GL.Vertex3(p.x + sq, p.y + yo, p.z - sq); GL.Vertex3(p.x + sq, p.y + yo, p.z + sq);
                        GL.Vertex3(p.x + sq, p.y + yo, p.z + sq); GL.Vertex3(p.x - sq, p.y + yo, p.z + sq);
                        GL.Vertex3(p.x - sq, p.y + yo, p.z + sq); GL.Vertex3(p.x - sq, p.y + yo, p.z - sq);
                    }
                    // Vertical pole to diamond
                    GL.Vertex3(p.x, p.y, p.z); GL.Vertex3(p.x, p.y + 2f, p.z);
                    // 3D diamond at 2m above — 4 lines from top to middle, 4 from middle to bottom
                    float dy = 0.3f; float dx = 0.2f;
                    float dmY = p.y + 2f;
                    // Top point to 4 middle points
                    GL.Vertex3(p.x, dmY + dy, p.z); GL.Vertex3(p.x + dx, dmY, p.z);
                    GL.Vertex3(p.x, dmY + dy, p.z); GL.Vertex3(p.x - dx, dmY, p.z);
                    GL.Vertex3(p.x, dmY + dy, p.z); GL.Vertex3(p.x, dmY, p.z + dx);
                    GL.Vertex3(p.x, dmY + dy, p.z); GL.Vertex3(p.x, dmY, p.z - dx);
                    // 4 middle points to bottom point
                    GL.Vertex3(p.x + dx, dmY, p.z); GL.Vertex3(p.x, dmY - dy, p.z);
                    GL.Vertex3(p.x - dx, dmY, p.z); GL.Vertex3(p.x, dmY - dy, p.z);
                    GL.Vertex3(p.x, dmY, p.z + dx); GL.Vertex3(p.x, dmY - dy, p.z);
                    GL.Vertex3(p.x, dmY, p.z - dx); GL.Vertex3(p.x, dmY - dy, p.z);
                    // Middle ring
                    GL.Vertex3(p.x + dx, dmY, p.z); GL.Vertex3(p.x, dmY, p.z + dx);
                    GL.Vertex3(p.x, dmY, p.z + dx); GL.Vertex3(p.x - dx, dmY, p.z);
                    GL.Vertex3(p.x - dx, dmY, p.z); GL.Vertex3(p.x, dmY, p.z - dx);
                    GL.Vertex3(p.x, dmY, p.z - dx); GL.Vertex3(p.x + dx, dmY, p.z);
                }
                else if (isMapLocation)
                {
                    GL.Vertex3(p.x, p.y, p.z); GL.Vertex3(p.x, p.y + 0.5f, p.z);
                }
            }
            GL.End();
        }

        static void DrawGraphEdges(Camera cam)
        {
            if (NavGraph.Instance == null) return;
            var edges = NavGraph.Instance.Edges;
            var nodes = NavGraph.Instance.Nodes;
            if (edges == null || nodes == null) return;

            Vector3 camPos = cam.transform.position;
            float maxDrawDist = 40f;

            GL.Begin(GL.LINES);
            foreach (var edge in edges)
            {
                if (edge.Confidence <= 0) continue;
                if (edge.Type == EdgeType.Teleporter) continue; // Don't draw lines across map

                NavNode fromNode = GetNodeById(nodes, edge.From);
                NavNode toNode = GetNodeById(nodes, edge.To);
                if (fromNode == null || toNode == null) continue;

                float dist = Vector3.Distance(fromNode.Position, camPos);
                if (dist > maxDrawDist) continue;

                float alpha = Mathf.Lerp(0.05f, 0.4f, 1f - dist / maxDrawDist);
                Vector3 from = fromNode.Position + Vector3.up * 0.1f;
                Vector3 to = toNode.Position + Vector3.up * 0.1f;

                switch (edge.Type)
                {
                    case EdgeType.Walk:
                        // Skip teleporter edges (walk edges with near-zero cost spanning large distances)
                        if (edge.Cost < 0.5f && Vector3.Distance(from, to) > 5f) break;
                        float confAlpha = alpha * edge.Confidence * 0.4f; // Dim walk edges
                        GL.Color(new Color(0.5f, 0.5f, 0.5f, confAlpha));
                        GL.Vertex3(from.x, from.y, from.z);
                        GL.Vertex3(to.x, to.y, to.z);
                        break;

                    case EdgeType.Jump:
                        // BRIGHT BLUE — triple-thick arc, has trajectory = solid, no trajectory = dashed
                        bool hasTraj = edge.AirPositions != null && edge.AirSampleCount > 2;
                        float ja = Mathf.Min(alpha * 1.5f, 1f);
                        GL.Color(new Color(0f, 0.4f, 1f, ja));
                        DrawArc(from, to, 1.5f);
                        GL.Color(new Color(0.3f, 0.6f, 1f, ja * 0.7f));
                        DrawArc(from + Vector3.up * 0.04f, to + Vector3.up * 0.04f, 1.5f);
                        DrawArc(from + Vector3.right * 0.03f, to + Vector3.right * 0.03f, 1.5f);
                        if (hasTraj)
                        {
                            // Green tint overlay = has recorded trajectory
                            GL.Color(new Color(0f, 1f, 0.3f, ja * 0.3f));
                            DrawArc(from + Vector3.up * 0.08f, to + Vector3.up * 0.08f, 1.5f);
                        }
                        break;

                    case EdgeType.Ladder:
                        // TURQUOISE — double thick, dashed look via segments
                        float la = Mathf.Min(alpha * 1.3f, 1f);
                        GL.Color(new Color(0f, 1f, 0.8f, la));
                        GL.Vertex3(from.x, from.y, from.z); GL.Vertex3(to.x, to.y, to.z);
                        GL.Vertex3(from.x + 0.03f, from.y, from.z); GL.Vertex3(to.x + 0.03f, to.y, to.z);
                        GL.Vertex3(from.x, from.y, from.z + 0.03f); GL.Vertex3(to.x, to.y, to.z + 0.03f);
                        break;

                    case EdgeType.Fall:
                        // RED-ORANGE dashed — triple draw for thickness
                        float fa = Mathf.Min(alpha * 1.2f, 1f);
                        GL.Color(new Color(1f, 0.3f, 0f, fa));
                        GL.Vertex3(from.x, from.y, from.z); GL.Vertex3(to.x, to.y, to.z);
                        GL.Color(new Color(1f, 0.3f, 0f, fa * 0.5f));
                        GL.Vertex3(from.x + 0.04f, from.y, from.z); GL.Vertex3(to.x + 0.04f, to.y, to.z);
                        GL.Vertex3(from.x, from.y, from.z + 0.04f); GL.Vertex3(to.x, to.y, to.z + 0.04f);
                        break;

                    case EdgeType.Slide:
                        // PURPLE — triple thick, low arc
                        float sa = Mathf.Min(alpha * 1.3f, 1f);
                        GL.Color(new Color(0.8f, 0f, 1f, sa));
                        for (float yo = 0f; yo <= 0.08f; yo += 0.04f)
                        {
                            GL.Vertex3(from.x, from.y + yo, from.z); GL.Vertex3(to.x, to.y + yo, to.z);
                        }
                        // Side offset for extra thickness
                        GL.Vertex3(from.x + 0.03f, from.y, from.z); GL.Vertex3(to.x + 0.03f, to.y, to.z);
                        break;

                    case EdgeType.WallJump:
                        // BRIGHT ORANGE — quad-thick high arc
                        float wa = Mathf.Min(alpha * 1.5f, 1f);
                        GL.Color(new Color(1f, 0.4f, 0f, wa));
                        DrawArc(from, to, 2.5f);
                        GL.Color(new Color(1f, 0.6f, 0.1f, wa * 0.7f));
                        DrawArc(from + Vector3.up * 0.05f, to + Vector3.up * 0.05f, 2.5f);
                        DrawArc(from + Vector3.right * 0.04f, to + Vector3.right * 0.04f, 2.5f);
                        DrawArc(from + Vector3.forward * 0.04f, to + Vector3.forward * 0.04f, 2.5f);
                        break;
                }
            }
            GL.End();
        }

        // ============ BOT PATHS (separate from indicators) ============

        static void DrawBotPaths()
        {
            var bots = BotManager.ActiveBots;
            if (bots == null) return;

            foreach (var bot in bots)
            {
                if (bot == null || bot.IsDead) continue;
                Vector3 pos = bot.transform.position + Vector3.up * 0.2f;

                // Graph path (yellow, showing A* route)
                var path = bot.DbgGraphPath;
                int pathIdx = bot.DbgGraphPathIndex;
                if (path != null && path.Count > 0 && pathIdx < path.Count)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(1f, 1f, 0f, 0.8f));
                    Vector3 nextPos = path[pathIdx].Position + Vector3.up * 0.2f;
                    GL.Vertex3(pos.x, pos.y, pos.z);
                    GL.Vertex3(nextPos.x, nextPos.y, nextPos.z);

                    for (int i = pathIdx; i < path.Count - 1; i++)
                    {
                        Vector3 a = path[i].Position + Vector3.up * 0.2f;
                        Vector3 b = path[i + 1].Position + Vector3.up * 0.2f;
                        float t = (float)(i - pathIdx) / Mathf.Max(1, path.Count - pathIdx);
                        GL.Color(Color.Lerp(new Color(1f, 1f, 0f, 0.8f), new Color(1f, 0.3f, 0f, 0.3f), t));
                        GL.Vertex3(a.x, a.y, a.z);
                        GL.Vertex3(b.x, b.y, b.z);
                    }
                    GL.End();
                }

                // Target (dashed line)
                Vector3 targetPos = Vector3.zero;
                Color targetColor = Color.white;
                bool hasTarget = false;
                if (bot.DbgPlayerTarget != null)
                    { targetPos = bot.DbgPlayerTarget.position + Vector3.up * 0.3f; targetColor = new Color(1f, 0f, 0f, 0.9f); hasTarget = true; }
                else if (bot.DbgWeaponTarget != null)
                    { targetPos = bot.DbgWeaponTarget.position + Vector3.up * 0.3f; targetColor = new Color(0f, 0.5f, 1f, 0.9f); hasTarget = true; }
                else if (bot.DbgHasWanderTarget)
                {
                    targetPos = bot.DbgWanderTarget + Vector3.up * 0.3f;
                    targetColor = new Color(0.5f, 1f, 0.5f, 0.6f);
                    // Only draw wander target line if within 30m — avoids map-spanning lines
                    hasTarget = Vector3.Distance(pos, targetPos) < 30f;
                }

                if (hasTarget)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(targetColor);
                    float totalDist = Vector3.Distance(pos, targetPos);
                    int dashes = Mathf.Max(1, (int)(totalDist / 1.5f));
                    for (int i = 0; i < dashes; i++)
                    {
                        float t0 = (float)i / dashes;
                        float t1 = (i + 0.6f) / dashes;
                        Vector3 p0 = Vector3.Lerp(pos, targetPos, t0);
                        Vector3 p1 = Vector3.Lerp(pos, targetPos, Mathf.Min(t1, 1f));
                        GL.Vertex3(p0.x, p0.y, p0.z);
                        GL.Vertex3(p1.x, p1.y, p1.z);
                    }
                    GL.End();
                }
            }
        }

        // ============ BOT INDICATORS ============

        static void DrawBotIndicators()
        {
            var bots = BotManager.ActiveBots;
            if (bots == null) return;

            foreach (var bot in bots)
            {
                if (bot == null || bot.IsDead) continue;
                Vector3 pos = bot.transform.position + Vector3.up * 0.2f;

                // Movement direction arrow (magenta)
                Vector3 moveDir = bot.DbgMoveDir;
                if (moveDir.sqrMagnitude > 0.01f)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(1f, 0f, 1f, 0.9f));
                    Vector3 lineEnd = pos + moveDir * 3f;
                    GL.Vertex3(pos.x, pos.y, pos.z);
                    GL.Vertex3(lineEnd.x, lineEnd.y, lineEnd.z);
                    Vector3 right = Vector3.Cross(Vector3.up, moveDir) * 0.2f;
                    Vector3 ab = lineEnd - moveDir * 0.4f;
                    GL.Vertex3(lineEnd.x, lineEnd.y, lineEnd.z);
                    GL.Vertex3(ab.x + right.x, ab.y, ab.z + right.z);
                    GL.Vertex3(lineEnd.x, lineEnd.y, lineEnd.z);
                    GL.Vertex3(ab.x - right.x, ab.y, ab.z - right.z);
                    GL.End();
                }

                // State ring
                Vector3 headPos = pos + Vector3.up * 2.2f;
                Color sc;
                switch (bot.State)
                {
                    case BotState.FindWeapon: sc = new Color(0.3f, 0.3f, 1f, 0.8f); break;
                    case BotState.GoToWeapon: sc = new Color(0f, 0.7f, 1f, 0.8f); break;
                    case BotState.PickUpWeapon: sc = new Color(0f, 1f, 0.5f, 0.8f); break;
                    case BotState.Hunt: sc = new Color(1f, 0.2f, 0.2f, 0.8f); break;
                    default: sc = new Color(0.5f, 0.5f, 0.5f, 0.5f); break;
                }
                GL.Begin(GL.LINES); GL.Color(sc); DrawRing(headPos, 0.35f, 12); GL.End();

                // Ladder
                if (bot.DbgOnLadder)
                {
                    GL.Begin(GL.LINES); GL.Color(new Color(0f, 1f, 1f, 1f));
                    GL.Vertex3(pos.x, pos.y + 0.5f, pos.z); GL.Vertex3(pos.x, pos.y + 3f, pos.z);
                    GL.End();
                }

                // Sliding indicator (magenta ground streak)
                if (bot.DbgIsSliding)
                {
                    GL.Begin(GL.LINES);
                    GL.Color(new Color(1f, 0f, 1f, 0.9f));
                    Vector3 slideStart = bot.transform.position + Vector3.up * 0.1f;
                    Vector3 slideEnd = slideStart + bot.transform.forward * 2f;
                    GL.Vertex3(slideStart.x, slideStart.y, slideStart.z);
                    GL.Vertex3(slideEnd.x, slideEnd.y, slideEnd.z);
                    GL.End();
                }

                // Stuck
                if (bot.DbgStuckTimer > 1f)
                {
                    GL.Begin(GL.LINES);
                    float pulse = Mathf.PingPong(Time.time * 3f, 1f);
                    GL.Color(new Color(1f, 0f, 0f, 0.5f + pulse * 0.5f));
                    float xs = 0.5f; Vector3 xp = headPos + Vector3.up * 0.5f;
                    GL.Vertex3(xp.x - xs, xp.y - xs, xp.z); GL.Vertex3(xp.x + xs, xp.y + xs, xp.z);
                    GL.Vertex3(xp.x + xs, xp.y - xs, xp.z); GL.Vertex3(xp.x - xs, xp.y + xs, xp.z);
                    GL.End();
                }
            }
        }

        // ============ OnGUI TEXT ============

        public static void DrawText(Camera cam)
        {
            if (cam == null) return;
            if (!(Plugin.ShowOverlay?.Value ?? true)) return;

            var bots = BotManager.ActiveBots;
            if (bots == null || bots.Count == 0) return;

            foreach (var bot in bots)
            {
                if (bot == null || bot.IsDead) continue;
                Vector3 screenPos = cam.WorldToScreenPoint(bot.transform.position + Vector3.up * 2.5f);
                if (screenPos.z < 0) continue;

                float x = screenPos.x;
                float y = Screen.height - screenPos.y;

                // Activity string — shows what the bot is actually doing
                string activity = bot.DbgActivity;
                string flags = "";
                if (bot.DbgOnLadder) flags += " [LADDER]";
                if (bot.DbgIsSliding) flags += " [SLIDE]";
                if (bot.DbgStuckTimer > 0.5f) flags += $" [STUCK {bot.DbgStuckTimer:F1}s E{bot.DbgStuckEscalation}]";
                if (bot.DbgIsCrouching) flags += " [CROUCH]";

                int graphNodes = NavGraph.Instance?.NodeCount ?? 0;
                int graphEdges = NavGraph.Instance?.EdgeCount ?? 0;
                string pathStr;
                if (bot.DbgGraphPath.Count > 0)
                {
                    pathStr = $"Path: {bot.DbgGraphPathIndex}/{bot.DbgGraphPath.Count} ({graphNodes}n/{graphEdges}e)";
                }
                else
                {
                    pathStr = $"No path ({graphNodes}n/{graphEdges}e)";
                }

                string label = $"{bot.BotName}: {activity}{flags}\n{pathStr}";
                GUI.color = Color.black;
                GUI.Label(new Rect(x - 79, y - 19, 300, 50), label);
                GUI.Label(new Rect(x - 81, y - 21, 300, 50), label);

                Color textCol;
                string act = activity.ToUpper();
                if (act.Contains("POI")) textCol = new Color(1f, 0.5f, 0f);
                else if (act.Contains("FOLLOW")) textCol = new Color(0f, 1f, 0.5f);
                else if (act.Contains("TRAIN")) textCol = new Color(0.5f, 0.8f, 1f);
                else if (act.Contains("HUNT")) textCol = new Color(1f, 0.3f, 0.3f);
                else if (act.Contains("SCATTER")) textCol = Color.yellow;
                else if (act.Contains("GAVE UP")) textCol = Color.grey;
                else if (act.Contains("DONE")) textCol = Color.green;
                else switch (bot.State)
                {
                    case BotState.FindWeapon: textCol = Color.cyan; break;
                    case BotState.GoToWeapon: textCol = new Color(0.3f, 0.7f, 1f); break;
                    case BotState.Hunt: textCol = new Color(1f, 0.3f, 0.3f); break;
                    default: textCol = Color.white; break;
                }
                GUI.color = textCol;
                GUI.Label(new Rect(x - 80, y - 20, 300, 40), label);
                GUI.color = Color.white;
            }
        }

        // ============ HELPERS ============

        static void DrawArc(Vector3 from, Vector3 to, float height)
        {
            int segments = 6;
            Vector3 prev = from;
            for (int i = 1; i <= segments; i++)
            {
                float t = (float)i / segments;
                Vector3 p = Vector3.Lerp(from, to, t);
                p.y += Mathf.Sin(t * Mathf.PI) * height;
                GL.Vertex3(prev.x, prev.y, prev.z);
                GL.Vertex3(p.x, p.y, p.z);
                prev = p;
            }
        }

        static NavNode GetNodeById(List<NavNode> nodes, int id)
        {
            // After Compact, Id == list index — O(1) lookup
            if (id >= 0 && id < nodes.Count && nodes[id].Id == id)
                return nodes[id];
            return null; // Skip O(N) fallback — stale IDs just return null
        }

        static void DrawRing(Vector3 center, float radius, int segments)
        {
            float step = 360f / segments;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * step * Mathf.Deg2Rad;
                float a1 = (i + 1) * step * Mathf.Deg2Rad;
                GL.Vertex3(center.x + Mathf.Cos(a0) * radius, center.y, center.z + Mathf.Sin(a0) * radius);
                GL.Vertex3(center.x + Mathf.Cos(a1) * radius, center.y, center.z + Mathf.Sin(a1) * radius);
            }
        }
    }
}
