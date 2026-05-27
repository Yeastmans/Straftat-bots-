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
        public string Name;
        public int SuitIndex;
        public int CigIndex;
        public int HatIndex;
        public int TeamId;
        public int PlayerId; // Real player slot (1, 2, 3 — same range as real players)

        // Runtime reference to the spawned bot controller
        public BotController Controller;
        public GameObject PlayerObject;

        public static BotData CreateRandom(int botId)
        {
            int maxSuits = 1;
            int maxHats = 0;
            int maxCigs = 1;

            if (CosmeticsManager.Instance != null)
            {
                if (CosmeticsManager.Instance.mats != null)
                    maxSuits = CosmeticsManager.Instance.mats.Length;
                if (CosmeticsManager.Instance.hats != null)
                    maxHats = CosmeticsManager.Instance.hats.Length;
                if (CosmeticsManager.Instance.cigs != null)
                    maxCigs = CosmeticsManager.Instance.cigs.Length;
            }

            // Use custom name from config if set, otherwise default
            string name = BotNames[botId % BotNames.Length];
            int slot = botId % 8;
            if (Plugin.BotNames != null && slot < Plugin.BotNames.Length && Plugin.BotNames[slot] != null)
            {
                string custom = Plugin.BotNames[slot].Value;
                if (!string.IsNullOrEmpty(custom))
                    name = custom;
            }

            return new BotData
            {
                BotId = botId,
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
                if (CosmeticsManager.Instance.hats != null) maxHats = CosmeticsManager.Instance.hats.Length;
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
                if (CosmeticsManager.Instance.hats != null) maxHats = CosmeticsManager.Instance.hats.Length;
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
