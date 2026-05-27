using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace StraftatBots
{
    public partial class NavGraph
    {
        // ========== SAVE / LOAD ==========

        private string GetFilePath(string mapName)
        {
            string pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(pluginDir, "NavData", $"{mapName}.bin");
        }

        private void SaveToFile(string path)
        {
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(FILE_VERSION);
                w.Write(_nextNodeId);

                w.Write(Nodes.Count);
                foreach (var n in Nodes)
                {
                    w.Write(n.Id);
                    w.Write(n.Position.x);
                    w.Write(n.Position.y);
                    w.Write(n.Position.z);
                    w.Write(n.Confidence);
                    w.Write(n.VisitCount);
                    w.Write(n.PlayerSourced);
                }

                w.Write(Edges.Count);
                foreach (var e in Edges)
                {
                    w.Write(e.From);
                    w.Write(e.To);
                    w.Write((byte)e.Type);
                    w.Write(e.Confidence);
                    w.Write(e.SuccessCount);
                    w.Write(e.FailCount);
                    w.Write(e.Cost);

                    // V3: trajectory data for jump/fall/walljump edges
                    w.Write(e.TakeoffDir.x); w.Write(e.TakeoffDir.y); w.Write(e.TakeoffDir.z);
                    w.Write(e.TakeoffSpeed);
                    w.Write(e.LockedSpeed);
                    w.Write(e.LockedAirTime);
                    int sampleCount = e.AirPositions != null ? e.AirSampleCount : 0;
                    w.Write(sampleCount);
                    for (int i = 0; i < sampleCount; i++)
                    {
                        w.Write(e.AirPositions[i].x);
                        w.Write(e.AirPositions[i].y);
                        w.Write(e.AirPositions[i].z);
                        w.Write(e.AirTimestamps[i]);
                    }
                }

                // V3: patrol protected node IDs
                w.Write(_patrolVisitedNodes.Count);
                foreach (int id in _patrolVisitedNodes)
                    w.Write(id);

                // V4: demonstrated routes ("Watch Me")
                w.Write(ProvenRoutes.Count);
                foreach (var r in ProvenRoutes)
                {
                    w.Write(r.Name ?? string.Empty);
                    w.Write(r.TotalTime);
                    w.Write(r.UseCount);
                    int len = r.NodeIds?.Length ?? 0;
                    w.Write(len);
                    for (int i = 0; i < len; i++)
                        w.Write(r.NodeIds[i]);
                }
            }
        }

        private void LoadFromFile(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var r = new BinaryReader(fs))
            {
                int version = r.ReadInt32();
                if (version == 1)
                {
                    Plugin.Log.LogInfo($"[NavGraph] Upgrading v1 -> v{FILE_VERSION}");
                    LoadFromFileV1(r);
                    _dirty = true;
                    return;
                }
                if (version != 2 && version != 3 && version != 4)
                {
                    Plugin.Log.LogWarning($"[NavGraph] File version {version} unknown, skipping");
                    return;
                }
                bool hasTrajectory = (version >= 3);
                bool hasProvenRoutes = (version >= 4);

                _nextNodeId = r.ReadInt32();

                int nodeCount = r.ReadInt32();
                for (int i = 0; i < nodeCount; i++)
                {
                    int id = r.ReadInt32();
                    float x = r.ReadSingle();
                    float y = r.ReadSingle();
                    float z = r.ReadSingle();
                    float conf = r.ReadSingle();
                    int visits = r.ReadInt32();
                    bool playerSourced = r.ReadBoolean();

                    var node = new NavNode(id, new Vector3(x, y, z));
                    node.Confidence = conf;
                    node.VisitCount = visits;
                    node.PlayerSourced = playerSourced;
                    Nodes.Add(node);
                    AddToSpatialGrid(node);
                }

                int edgeCount = r.ReadInt32();
                for (int i = 0; i < edgeCount; i++)
                {
                    int from = r.ReadInt32();
                    int to = r.ReadInt32();
                    EdgeType type = (EdgeType)r.ReadByte();
                    float conf = r.ReadSingle();
                    int success = r.ReadInt32();
                    int fail = r.ReadInt32();
                    float cost = r.ReadSingle();

                    var edge = new NavEdge(from, to, type, cost);
                    edge.Confidence = conf;
                    edge.SuccessCount = success;
                    edge.FailCount = fail;

                    // V3: trajectory data
                    if (hasTrajectory)
                    {
                        edge.TakeoffDir = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        edge.TakeoffSpeed = r.ReadSingle();
                        edge.LockedSpeed = r.ReadSingle();
                        edge.LockedAirTime = r.ReadSingle();
                        int sampleCount = r.ReadInt32();
                        if (sampleCount > 0)
                        {
                            edge.AirPositions = new Vector3[sampleCount];
                            edge.AirTimestamps = new float[sampleCount];
                            for (int s = 0; s < sampleCount; s++)
                            {
                                edge.AirPositions[s] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                edge.AirTimestamps[s] = r.ReadSingle();
                            }
                            edge.AirSampleCount = sampleCount;
                        }
                    }

                    int edgeIdx = Edges.Count;
                    Edges.Add(edge);

                    if (!_edgesByFrom.ContainsKey(from))
                        _edgesByFrom[from] = new List<int>();
                    _edgesByFrom[from].Add(edgeIdx);

                    if (!_edgesByTo.ContainsKey(to))
                        _edgesByTo[to] = new List<int>();
                    _edgesByTo[to].Add(edgeIdx);
                }

                // V3: load patrol protected node IDs (if present at end of file)
                if (hasTrajectory)
                {
                    try
                    {
                        int patrolCount = r.ReadInt32();
                        for (int i = 0; i < patrolCount; i++)
                            _patrolVisitedNodes.Add(r.ReadInt32());
                        if (patrolCount > 0)
                            Plugin.Log.LogInfo($"[NavGraph] Loaded {patrolCount} patrol-protected nodes");
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                        // V3 file saved before patrol protection was added — no data at end, that's fine
                    }
                }

                // V4: load demonstrated routes
                if (hasProvenRoutes)
                {
                    try
                    {
                        int routeCount = r.ReadInt32();
                        for (int i = 0; i < routeCount; i++)
                        {
                            string name = r.ReadString();
                            float totalTime = r.ReadSingle();
                            int useCount = r.ReadInt32();
                            int len = r.ReadInt32();
                            if (len < 0 || len > 100000)
                            {
                                Plugin.Log.LogWarning($"[NavGraph] ProvenRoute '{name}' has invalid length {len}, skipping file tail");
                                break;
                            }
                            var ids = new int[len];
                            for (int j = 0; j < len; j++) ids[j] = r.ReadInt32();
                            var route = new ProvenRoute(name, ids, totalTime);
                            route.UseCount = useCount;
                            ProvenRoutes.Add(route);
                        }
                        if (ProvenRoutes.Count > 0)
                            Plugin.Log.LogInfo($"[NavGraph] Loaded {ProvenRoutes.Count} ProvenRoutes");
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                        // v4 tag present but truncated — tolerate
                    }
                }
                RebuildProvenEdgeSet();

                if (version < FILE_VERSION)
                {
                    Plugin.Log.LogInfo($"[NavGraph] Upgrading v{version} -> v{FILE_VERSION}");
                    _dirty = true;
                }
            }
        }

        /// <summary>
        /// Load v1 format (no PlayerSourced field).
        /// </summary>
        private void LoadFromFileV1(BinaryReader r)
        {
            _nextNodeId = r.ReadInt32();

            int nodeCount = r.ReadInt32();
            for (int i = 0; i < nodeCount; i++)
            {
                int id = r.ReadInt32();
                float x = r.ReadSingle();
                float y = r.ReadSingle();
                float z = r.ReadSingle();
                float conf = r.ReadSingle();
                int visits = r.ReadInt32();

                var node = new NavNode(id, new Vector3(x, y, z));
                node.Confidence = conf;
                node.VisitCount = visits;
                node.PlayerSourced = false; // Unknown source in v1
                Nodes.Add(node);
                AddToSpatialGrid(node);
            }

            int edgeCount = r.ReadInt32();
            for (int i = 0; i < edgeCount; i++)
            {
                int from = r.ReadInt32();
                int to = r.ReadInt32();
                EdgeType type = (EdgeType)r.ReadByte();
                float conf = r.ReadSingle();
                int success = r.ReadInt32();
                int fail = r.ReadInt32();
                float cost = r.ReadSingle();

                var edge = new NavEdge(from, to, type, cost);
                edge.Confidence = conf;
                edge.SuccessCount = success;
                edge.FailCount = fail;

                int edgeIdx = Edges.Count;
                Edges.Add(edge);

                if (!_edgesByFrom.ContainsKey(from))
                    _edgesByFrom[from] = new List<int>();
                _edgesByFrom[from].Add(edgeIdx);

                if (!_edgesByTo.ContainsKey(to))
                    _edgesByTo[to] = new List<int>();
                _edgesByTo[to].Add(edgeIdx);
            }
        }

        // ========== SERIALIZATION FOR MYCELIUM SYNC ==========

        public byte[] SerializeToBytes()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(_nextNodeId);
                w.Write(Nodes.Count);
                foreach (var n in Nodes)
                {
                    w.Write(n.Id);
                    w.Write(n.Position.x);
                    w.Write(n.Position.y);
                    w.Write(n.Position.z);
                    w.Write(n.Confidence);
                    w.Write(n.VisitCount);
                    w.Write(n.PlayerSourced);
                }
                w.Write(Edges.Count);
                foreach (var e in Edges)
                {
                    w.Write(e.From);
                    w.Write(e.To);
                    w.Write((byte)e.Type);
                    w.Write(e.Confidence);
                    w.Write(e.SuccessCount);
                    w.Write(e.FailCount);
                    w.Write(e.Cost);
                    w.Write(e.TakeoffDir.x); w.Write(e.TakeoffDir.y); w.Write(e.TakeoffDir.z);
                    w.Write(e.TakeoffSpeed);
                    w.Write(e.LockedSpeed);
                    w.Write(e.LockedAirTime);
                    int sc = e.AirPositions != null ? e.AirSampleCount : 0;
                    w.Write(sc);
                    for (int j = 0; j < sc; j++)
                    {
                        w.Write(e.AirPositions[j].x);
                        w.Write(e.AirPositions[j].y);
                        w.Write(e.AirPositions[j].z);
                        w.Write(e.AirTimestamps[j]);
                    }
                }
                return ms.ToArray();
            }
        }

        public void MergeFromBytes(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                {
                    int remoteNextId = r.ReadInt32();
                    int nodeCount = r.ReadInt32();

                    // Validate incoming data — reject unreasonable sizes
                    if (nodeCount < 0 || nodeCount > 50000 || data.Length < nodeCount * 10)
                    {
                        Plugin.Log.LogWarning($"[NavGraph] Merge rejected: invalid nodeCount={nodeCount}");
                        return;
                    }

                    var remoteToLocal = new Dictionary<int, NavNode>();

                    for (int i = 0; i < nodeCount; i++)
                    {
                        int id = r.ReadInt32();
                        float x = r.ReadSingle();
                        float y = r.ReadSingle();
                        float z = r.ReadSingle();
                        float conf = r.ReadSingle();
                        int visits = r.ReadInt32();
                        bool playerSourced = r.ReadBoolean();

                        var localNode = AddPosition(new Vector3(x, y, z), playerSourced);
                        if (localNode != null)
                        {
                            localNode.Confidence = Mathf.Max(localNode.Confidence, conf);
                            remoteToLocal[id] = localNode;
                        }
                    }

                    int edgeCount = r.ReadInt32();
                    if (edgeCount < 0 || edgeCount > 200000)
                    {
                        Plugin.Log.LogWarning($"[NavGraph] Merge rejected: invalid edgeCount={edgeCount}");
                        return;
                    }
                    for (int i = 0; i < edgeCount; i++)
                    {
                        int from = r.ReadInt32();
                        int to = r.ReadInt32();
                        byte typeByte = r.ReadByte();
                        EdgeType type = typeByte <= 6 ? (EdgeType)typeByte : EdgeType.Walk;
                        float conf = r.ReadSingle();
                        int success = r.ReadInt32();
                        int fail = r.ReadInt32();
                        float cost = r.ReadSingle();

                        // Read trajectory data
                        Vector3 takeoffDir = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float takeoffSpeed = r.ReadSingle();
                        float lockedSpeed = r.ReadSingle();
                        float lockedAirTime = r.ReadSingle();
                        int sc = r.ReadInt32();
                        Vector3[] airPos = null;
                        float[] airTs = null;
                        if (sc > 0 && sc <= 60)
                        {
                            airPos = new Vector3[sc];
                            airTs = new float[sc];
                            for (int j = 0; j < sc; j++)
                            {
                                airPos[j] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                                airTs[j] = r.ReadSingle();
                            }
                        }
                        else if (sc > 60)
                        {
                            // Skip bad data
                            for (int j = 0; j < sc; j++) { r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); }
                            sc = 0;
                        }

                        if (remoteToLocal.TryGetValue(from, out var localFrom) &&
                            remoteToLocal.TryGetValue(to, out var localTo))
                        {
                            var edge = AddEdge(localFrom.Id, localTo.Id, type, cost);
                            if (edge != null)
                            {
                                edge.Confidence = Mathf.Max(edge.Confidence, conf);
                                // Keep trajectory from whichever source has data
                                if (sc > 0 && edge.AirSampleCount == 0)
                                {
                                    edge.TakeoffDir = takeoffDir;
                                    edge.TakeoffSpeed = takeoffSpeed;
                                    edge.LockedSpeed = lockedSpeed;
                                    edge.LockedAirTime = lockedAirTime;
                                    edge.AirPositions = airPos;
                                    edge.AirTimestamps = airTs;
                                    edge.AirSampleCount = sc;
                                }
                            }
                        }
                    }

                    Plugin.Log.LogInfo($"[NavGraph] Merged remote data: {nodeCount} nodes, {edgeCount} edges");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[NavGraph] Merge failed: {ex.Message}");
            }
        }
    }
}
