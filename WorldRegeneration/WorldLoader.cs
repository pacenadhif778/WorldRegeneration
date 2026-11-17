using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using TShockAPI;

namespace WorldRegeneration
{
    /// <summary>
    /// load world data from custom format (GZip).
    /// </summary>
    public static class WorldLoader
    {
        private const int ChestSlots = 40;

        public static void LoadWorldSection(string path, Rectangle rect, bool useRect = false, bool informPlayers = true)
        {
            Task.Factory.StartNew(() =>
            {
                using (var reader = new BinaryReader(new GZipStream(new FileStream(path, FileMode.Open), CompressionMode.Decompress)))
                {
                    LoadWorldInfo(reader);
                    LoadTilesInArea(rect, useRect, reader);

                    if (useRect)
                        return;

                    LoadChests(reader);
                    LoadSigns(reader);
                    LoadTileEntity(reader);

                    if (WorldRegeneration.Config.ResetWorldGenStatus)
                        ResetWorldData();

                    if (informPlayers)
                        TSPlayer.All.SendInfoMessage("Successfully regenerated the world.");
                }
            });
        }

        public static void RegenerateWorld(string path) =>
            LoadWorldSection(path, Rectangle.Empty, false, true);

        #region World Info

        private static void LoadWorldInfo(BinaryReader reader)
        {
            Main.worldSurface = reader.ReadDouble();
            Main.rockLayer = reader.ReadDouble();
            Main.dungeonX = reader.ReadInt32();
            Main.dungeonY = reader.ReadInt32();
            WorldGen.crimson = reader.ReadBoolean();

            WorldGen.SavedOreTiers.Copper = reader.ReadInt32();
            WorldGen.SavedOreTiers.Silver = reader.ReadInt32();
            WorldGen.SavedOreTiers.Iron = reader.ReadInt32();
            WorldGen.SavedOreTiers.Gold = reader.ReadInt32();

            WorldGen.SavedOreTiers.Cobalt = reader.ReadInt32();
            WorldGen.SavedOreTiers.Mythril = reader.ReadInt32();
            WorldGen.SavedOreTiers.Adamantite = reader.ReadInt32();
        }

        #endregion

        #region Tiles

        private static void LoadTilesInArea(Rectangle rect, bool useRect, BinaryReader reader, bool informPlayers = true)
        {
            reader.ReadInt32();
            reader.ReadInt32();

            const int x = 0;
            const int y = 0;
            int x2 = reader.ReadInt32();
            int y2 = reader.ReadInt32();

            for (int i = x; i <= x2; i++)
            {
                for (int j = y; j <= y2; j++)
                {
                    Tile tile = reader.ReadTile();

                    if (i < 0 || j < 0 || i >= Main.maxTilesX || j >= Main.maxTilesY)
                        continue;
                    if (RegionGuard.IsProtected(i, j))
                        continue;
                    if (useRect && !rect.Contains(i, j))
                        continue;

                    Main.tile[i, j] = tile;
                }
            }

            if (informPlayers)
                TSPlayer.All.SendInfoMessage("Tile Data Loaded...");

            Utilities.ResetSection(x, y, x2, y2);
        }

        #endregion

        #region Tile Entities

        private static void LoadTileEntity(BinaryReader reader, bool informPlayers = true)
        {
            int total = reader.ReadInt32();
            int loaded = 0;

            for (int i = 0; i < total; i++)
            {
                TileEntity entity = TileEntity.Read(reader, Main.curRelease);

                if (TileEntity.ByPosition.ContainsKey(entity.Position))
                    continue;

                int id = 0;
                while (TileEntity.ByID.ContainsKey(id))
                    id++;

                entity.ID = id;
                TileEntity.ByID[id] = entity;
                TileEntity.ByPosition[entity.Position] = entity;
                TileEntity.TileEntitiesNextID = Math.Max(TileEntity.TileEntitiesNextID, id + 1);
                loaded++;
            }

            if (informPlayers)
                TSPlayer.All.SendInfoMessage("{0} of {1} Tile Entity Data Loaded...", loaded, total);
        }

        #endregion

        #region Signs

        private static void LoadSigns(BinaryReader reader, bool informPlayers = true)
        {
            int total = reader.ReadInt32();
            int loaded = 0;
            int index = 0;

            for (int a = 0; a < total; a++)
            {
                Sign sign = reader.ReadSign();

                if (RegionGuard.IsProtected(sign.x, sign.y))
                    continue;

                while (index < Main.sign.Length && Main.sign[index] != null &&
                       RegionGuard.IsProtected(Main.sign[index].x, Main.sign[index].y))
                    index++;

                if (index >= Main.sign.Length)
                    continue;

                Main.sign[index++] = sign;
                loaded++;
            }

            if (informPlayers)
                TSPlayer.All.SendInfoMessage("{0} of {1} Signs Data Loaded...", loaded, total);
        }

        #endregion

        #region Chests

        private static void LoadChests(BinaryReader reader, bool informPlayers = true)
        {
            int total = reader.ReadInt32();

            if (WorldRegeneration.Config.IgnoreChests)
            {
                for (int a = 0; a < total; a++)
                    reader.ReadChest();

                if (informPlayers)
                    TSPlayer.All.SendInfoMessage("{0} Chest Data Ignored...", total);
                return;
            }

            int loaded = 0;
            int index = 0;

            for (int a = 0; a < total; a++)
            {
                Chest chest = reader.ReadChest();

                if (RegionGuard.IsProtected(chest.x, chest.y))
                    continue;

                while (index < Main.chest.Length && Main.chest[index] != null &&
                       RegionGuard.IsProtected(Main.chest[index].x, Main.chest[index].y))
                    index++;

                if (index >= Main.chest.Length)
                    continue;

                Main.chest[index++] = chest;
                loaded++;
            }

            if (informPlayers)
                TSPlayer.All.SendInfoMessage("{0} of {1} Chest Data Loaded...", loaded, total);
        }

        #endregion

        #region Binary Readers

        public static Tile ReadTile(this BinaryReader reader)
        {
            var tile = new Tile();
            tile.sTileHeader = reader.ReadUInt16();
            tile.bTileHeader = reader.ReadByte();
            tile.bTileHeader2 = reader.ReadByte();

            if (tile.active())
            {
                tile.type = reader.ReadUInt16();
                if (Main.tileFrameImportant[tile.type])
                {
                    tile.frameX = reader.ReadInt16();
                    tile.frameY = reader.ReadInt16();
                }
            }

            tile.wall = reader.ReadUInt16();
            tile.liquid = reader.ReadByte();
            return tile;
        }

        public static Chest ReadChest(this BinaryReader reader)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            var chest = new Chest(x, y) { name = "World Chest" };

            for (int l = 0; l < ChestSlots; l++)
            {
                var item = new Item();
                int stack = reader.ReadInt16();
                if (stack > 0)
                {
                    int netID = reader.ReadInt32();
                    byte prefix = reader.ReadByte();
                    item.netDefaults(netID);
                    item.stack = stack;
                    item.Prefix(prefix);
                }
                chest.item[l] = item;
            }
            return chest;
        }

        public static Sign ReadSign(this BinaryReader reader)
        {
            var sign = new Sign();
            sign.text = reader.ReadString();
            sign.x = reader.ReadInt32();
            sign.y = reader.ReadInt32();
            return sign;
        }

        #endregion

        #region Reset

        public static void ResetWorldData()
        {
            Main.hardMode = false;

            NPC.downedBoss1 = false;
            NPC.downedBoss2 = false;
            NPC.downedBoss3 = false;
            NPC.downedQueenBee = false;
            NPC.downedSlimeKing = false;
            NPC.downedMechBossAny = false;
            NPC.downedMechBoss1 = false;
            NPC.downedMechBoss2 = false;
            NPC.downedMechBoss3 = false;
            NPC.downedFishron = false;
            NPC.downedMartians = false;
            NPC.downedAncientCultist = false;
            NPC.downedMoonlord = false;
            NPC.downedHalloweenKing = false;
            NPC.downedHalloweenTree = false;
            NPC.downedChristmasIceQueen = false;
            NPC.downedChristmasSantank = false;
            NPC.downedChristmasTree = false;
            NPC.downedPlantBoss = false;
            NPC.downedGolemBoss = false;
            NPC.downedGoblins = false;
            NPC.downedClown = false;
            NPC.downedFrost = false;
            NPC.downedPirates = false;

            NPC.savedStylist = false;
            NPC.savedGoblin = false;
            NPC.savedWizard = false;
            NPC.savedMech = false;
            NPC.savedAngler = false;
            NPC.savedTaxCollector = false;

            WorldGen.shadowOrbSmashed = false;
            WorldGen.altarCount = 0;
            WorldGen.shadowOrbCount = 0;
        }

        #endregion
    }
}