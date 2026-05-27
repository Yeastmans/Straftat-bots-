using System.Collections.Generic;
using UnityEngine;

namespace StraftatBots
{
    internal static class BotKillFeed
    {
        private static readonly Dictionary<int, int> RecentVictimFrames = new Dictionary<int, int>();

        internal static bool Write(PlayerHealth victim, GameObject killerRoot, string killerNameFallback,
            string weaponName, string action = "killed", bool dedupe = true)
        {
            if (victim == null || PauseManager.Instance == null) return false;
            if (dedupe && !TryMark(victim)) return false;

            BotController victimBot = victim.GetComponent<BotController>() ?? victim.GetComponentInParent<BotController>();
            BotController killerBot = killerRoot != null
                ? (killerRoot.GetComponent<BotController>() ?? killerRoot.GetComponentInParent<BotController>())
                : null;

            string victimName = ResolveVictimName(victim, victimBot);
            string killerName = ResolveKillerName(killerRoot, killerBot, killerNameFallback);
            string cleanWeaponName = CleanWeaponName(weaponName);
            string cleanAction = string.IsNullOrWhiteSpace(action) ? "killed" : action;
            string article = StartsWithVowel(cleanWeaponName) ? "an" : "a";

            PauseManager.Instance.WriteLog(
                $"<b><color={VictimColor(victimBot != null)}>{victimName}</color></b> was {cleanAction} with {article} <b><color=white>{cleanWeaponName}</color></b> by <b><color={KillerColor(killerBot != null)}>{killerName}</color></b>");
            return true;
        }

        private static bool TryMark(PlayerHealth victim)
        {
            int key = victim.GetInstanceID();
            int frame = Time.frameCount;
            if (RecentVictimFrames.TryGetValue(key, out int lastFrame) && frame - lastFrame < 10)
                return false;

            RecentVictimFrames[key] = frame;
            if (RecentVictimFrames.Count > 128)
            {
                var stale = new List<int>();
                foreach (var kv in RecentVictimFrames)
                {
                    if (frame - kv.Value > 600)
                        stale.Add(kv.Key);
                }
                foreach (int staleKey in stale)
                    RecentVictimFrames.Remove(staleKey);
            }
            return true;
        }

        private static string ResolveVictimName(PlayerHealth victim, BotController victimBot)
        {
            if (victimBot != null && !string.IsNullOrWhiteSpace(victimBot.BotName))
                return victimBot.BotName;

            PlayerValues pv = victim.playerValues ?? victim.GetComponent<PlayerValues>() ?? victim.GetComponentInParent<PlayerValues>();
            string name = pv?.playerClient?.PlayerNameTag;
            if (string.IsNullOrWhiteSpace(name))
                name = pv?.playerClient?.PlayerName;
            return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        }

        private static string ResolveKillerName(GameObject killerRoot, BotController killerBot, string fallback)
        {
            if (killerBot != null && !string.IsNullOrWhiteSpace(killerBot.BotName))
                return killerBot.BotName;

            if (killerRoot != null)
            {
                PlayerValues pv = killerRoot.GetComponent<PlayerValues>() ?? killerRoot.GetComponentInParent<PlayerValues>();
                string name = pv?.playerClient?.PlayerNameTag;
                if (string.IsNullOrWhiteSpace(name))
                    name = pv?.playerClient?.PlayerName;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }

            return string.IsNullOrWhiteSpace(fallback) ? "unknown" : fallback;
        }

        private static string CleanWeaponName(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName)) return "weapon";
            return weaponName.Replace("(Clone)", "").Trim();
        }

        private static bool StartsWithVowel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            char c = char.ToLowerInvariant(value.Trim()[0]);
            return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u';
        }

        private static string VictimColor(bool isBot)
        {
            if (isBot) return "orange";
            return NormalizeColor(PauseManager.Instance?.enemyNameLogColor, "orange");
        }

        private static string KillerColor(bool isBot)
        {
            if (isBot) return "orange";
            return NormalizeColor(PauseManager.Instance?.selfNameLogColor, "orange");
        }

        private static string NormalizeColor(string color, string fallback)
        {
            if (string.IsNullOrWhiteSpace(color)) return fallback;
            string trimmed = color.Trim();
            if (trimmed.StartsWith("#"))
                return trimmed;
            if (IsHexColor(trimmed))
                return "#" + trimmed;
            if (IsNamedColor(trimmed))
                return trimmed;
            return fallback;
        }

        private static bool IsHexColor(string value)
        {
            if (value.Length != 6 && value.Length != 8) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        private static bool IsNamedColor(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsLetter(value[i]))
                    return false;
            }
            return value.Length > 0;
        }
    }
}
