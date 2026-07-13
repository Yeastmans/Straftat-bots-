using UnityEngine;

namespace StraftatBots
{
    public class BotData
    {
        private static readonly string[] BotNames = new string[]
        {
            "Bot Alpha", "Bot Bravo", "Bot Charlie", "Bot Delta",
            "Bot Echo", "Bot Foxtrot", "Bot Golf", "Bot Hotel"
        };

        public int BotId;
        // Stable config slot 0-7 (lobby position). BotId keeps incrementing across
        // respawns/re-adds, so BotId%8 drifted off the "Bot N Name/Skill" settings.
        public int SlotIndex;
        public string Name;
        public int SuitIndex;
        public int CigIndex;
        public int HatIndex;
        public int TeamId;
        public int PlayerId; // Real player slot (1, 2, 3 — same range as real players)

        // Runtime reference to the spawned bot controller
        public BotController Controller;
        public GameObject PlayerObject;

        // Time.time of the last ApplyAllCosmetics pass — the hat probe logs full
        // render-state for a window after each dress.
        public float LastDressTime = -999f;

        public static BotData CreateRandom(int botId, int slot = -1)
        {
            if (slot < 0) slot = botId % 8;
            int maxSuits = 1;
            int maxHats = 0;
            int maxCigs = 1;

            if (CosmeticsManager.Instance != null)
            {
                if (CosmeticsManager.Instance.mats != null)
                    maxSuits = CosmeticsManager.Instance.mats.Length;
                maxHats = BotManager.GetAvailableHatCount();
                if (CosmeticsManager.Instance.cigs != null)
                    maxCigs = CosmeticsManager.Instance.cigs.Length;
            }

            // Use custom name from config if set, otherwise default
            string name = BotNames[slot % BotNames.Length];
            if (Plugin.BotNames != null && slot < Plugin.BotNames.Length && Plugin.BotNames[slot] != null)
            {
                string custom = Plugin.BotNames[slot].Value;
                if (!string.IsNullOrEmpty(custom))
                    name = custom;
            }

            return new BotData
            {
                BotId = botId,
                SlotIndex = slot,
                Name = name,
                SuitIndex = Random.Range(0, maxSuits),
                HatIndex = maxHats > 0 ? Random.Range(0, maxHats) : -1,
                CigIndex = Random.Range(0, maxCigs),
                TeamId = -1, // Will be set to PlayerId for FFA (each bot = own team)
                PlayerId = -1 // assigned during registration
            };
        }

        /// <summary>Re-randomize suit, hat, and cig for a new round.</summary>
        public void RandomizeCosmetics()
        {
            int maxSuits = 1, maxHats = 0, maxCigs = 1;
            if (CosmeticsManager.Instance != null)
            {
                if (CosmeticsManager.Instance.mats != null) maxSuits = CosmeticsManager.Instance.mats.Length;
                maxHats = BotManager.GetAvailableHatCount();
                if (CosmeticsManager.Instance.cigs != null) maxCigs = CosmeticsManager.Instance.cigs.Length;
            }
            SuitIndex = Random.Range(0, maxSuits);
            HatIndex = maxHats > 0 ? Random.Range(0, maxHats) : -1;
            CigIndex = Random.Range(0, maxCigs);
        }

        public void EnsureCosmeticsValid()
        {
            int maxSuits = 1, maxHats = 0, maxCigs = 1;
            if (CosmeticsManager.Instance != null)
            {
                if (CosmeticsManager.Instance.mats != null) maxSuits = CosmeticsManager.Instance.mats.Length;
                maxHats = BotManager.GetAvailableHatCount();
                if (CosmeticsManager.Instance.cigs != null) maxCigs = CosmeticsManager.Instance.cigs.Length;
            }

            if (SuitIndex < 0 || SuitIndex >= maxSuits)
                SuitIndex = Random.Range(0, maxSuits);

            if (maxHats > 0)
            {
                if (HatIndex < 0 || HatIndex >= maxHats)
                    HatIndex = Random.Range(0, maxHats);
            }
            else
            {
                HatIndex = -1;
            }

            if (CigIndex < 0 || CigIndex >= maxCigs)
                CigIndex = Random.Range(0, maxCigs);
        }
    }
}
