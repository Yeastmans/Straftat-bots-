using UnityEngine;

namespace StraftatBots
{
    /// <summary>Lightweight frame/mod-time profiler. One summary line every 10s:
    /// where frame time goes (game vs bot updates) and GC pressure. Toggled by the
    /// "Log Perf Stats" config; near-zero cost when off.</summary>
    internal static class BotPerfStats
    {
        const float WindowSeconds = 10f;

        static int _frames;
        static float _windowStart = -1f;
        static float _worstFrameDt;
        static double _dtSum;
        static double _botMsTotal;
        static double _botMsWorst;
        static int _botSamples;
        static double _mgrMsTotal;
        static double _mgrMsWorst;
        static int _gc0AtStart;

        public static bool Enabled => Plugin.LogPerfStats != null && Plugin.LogPerfStats.Value;

        /// <summary>Call once per frame (Plugin.Update). Closes and logs the window.</summary>
        public static void FrameTick(float unscaledDt)
        {
            if (!Enabled) { _windowStart = -1f; return; }
            float now = Time.unscaledTime;
            if (_windowStart < 0f) { Reset(now); return; }

            _frames++;
            _dtSum += unscaledDt;
            if (unscaledDt > _worstFrameDt) _worstFrameDt = unscaledDt;

            if (now - _windowStart < WindowSeconds || _frames == 0 || _dtSum <= 0) return;

            int gc0 = System.GC.CollectionCount(0) - _gc0AtStart;
            double avgFps = _frames / _dtSum;
            double avgBotMsPerFrame = _botMsTotal / _frames;
            double avgMgrMsPerFrame = _mgrMsTotal / _frames;
            Plugin.Log.LogInfo(
                $"[Perf] {avgFps:F0} fps avg, worst frame {_worstFrameDt * 1000f:F1} ms | " +
                $"bot updates {avgBotMsPerFrame:F2} ms/frame (worst single {_botMsWorst:F2} ms, {_botSamples} calls) | " +
                $"manager {avgMgrMsPerFrame:F2} ms/frame (worst {_mgrMsWorst:F2} ms) | GC gen0: {gc0}");
            Reset(now);
        }

        static void Reset(float now)
        {
            _windowStart = now;
            _frames = 0;
            _dtSum = 0;
            _worstFrameDt = 0f;
            _botMsTotal = 0; _botMsWorst = 0; _botSamples = 0;
            _mgrMsTotal = 0; _mgrMsWorst = 0;
            _gc0AtStart = System.GC.CollectionCount(0);
        }

        public static void AddBotUpdate(double ms)
        {
            _botMsTotal += ms;
            _botSamples++;
            if (ms > _botMsWorst) _botMsWorst = ms;
        }

        public static void AddManagerUpdate(double ms)
        {
            _mgrMsTotal += ms;
            if (ms > _mgrMsWorst) _mgrMsWorst = ms;
        }
    }
}
