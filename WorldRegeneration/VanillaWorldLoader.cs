using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.IO;
using Terraria.Utilities;
using TShockAPI;

namespace WorldRegeneration
{
    /// <summary>
    /// Loads world data from a vanilla .wld file (tiles, chests, signs, tile entities, and pressure plates)
    /// while skipping protected region areas (Z >= MaxZRegion).
    /// </summary>
    public class VanillaWorldLoader
    {
        private const int ChestSlots = 40;

        private readonly WorldLoadSettings _settings;

        public VanillaWorldLoader(WorldLoadSettings settings = null)
        {
            _settings = settings ?? new WorldLoadSettings();
        }

        public void LoadWorld(string path)
        {
            Task.Factory.StartNew(() =>
            {
                using (var reader = new BinaryReader(new MemoryStream(FileUtilities.ReadAllBytes(path, false))))
                {
                    int fileVersion = reader.ReadInt32();
                    FileMetadata.Read(reader, FileType.World);

                    int[] positions = ReadSectionPositions(reader);
                    bool[] importance = ReadTileImportance(reader);

                    int tilePosition = positions[1];
                    int chestPosition = positions[2];
                    int signsPosition = positions[3];
                    int tileEntityPosition = positions[5];
                    int weightedPlatesPosition = positions[6];

                    if (_settings.LoadTiles)
                    {
                        reader.BaseStream.Seek(tilePosition, SeekOrigin.Begin);
                        LoadTiles(reader, importance);
                    }
                    if (_settings.LoadChests)
                    {
                        reader.BaseStream.Seek(chestPosition, SeekOrigin.Begin);
                        LoadChests(reader);
                    }
                    if (_settings.LoadSigns)
                    {
                        reader.BaseStream.Seek(signsPosition, SeekOrigin.Begin);
                        LoadSigns(reader);
                    }
                    if (_settings.LoadEnities)
                    {
                        reader.BaseStream.Seek(tileEntityPosition, SeekOrigin.Begin);
                        LoadEntities(reader, fileVersion);
                    }
                    if (_settings.LoadWeightetPlates)
                    {
                        reader.BaseStream.Seek(weightedPlatesPosition, SeekOrigin.Begin);
                        LoadWeightedPlates(reader);
                    }

                    Utilities.ResetSection(0, 0, Main.maxTilesX, Main.maxTilesY);
                    TSPlayer.All.SendInfoMessage("Successfully regenerated the world.");
                }
            });
        }

        #region Header

        private static int[] ReadSectionPositions(BinaryReader reader)
        {
            short count = reader.ReadInt16();
            var positions = new int[count];
            for (int i = 0; i < count; i++)
                positions[i] = reader.ReadInt32();
            return positions;
        }

        private static bool[] ReadTileImportance(BinaryReader reader)
        {
            short count = reader.ReadInt16();
            var importance = new bool[count];

            byte bits = 0;
            byte mask = 128;
            for (int i = 0; i < count; i++)
            {
                if (mask == 128)
                {
                    bits = reader.ReadByte();
                    mask = 1;
                }
                else
                {
                    mask = (byte)(mask << 1);
                }

                if ((bits & mask) == mask)
                    importance[i] = true;
            }
            return importance;
        }

        #endregion

        #region Weighted Pressure Plates

        private void LoadWeightedPlates(BinaryReader reader)
        {
            PressurePlateHelper.Reset();
            PressurePlateHelper.NeedsFirstUpdate = true;

            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var point = new Point(reader.ReadInt32(), reader.ReadInt32());
                if (RegionGuard.IsProtected(point.X / 16, point.Y / 16))
                    continue;

                PressurePlateHelper.PressurePlatesPressed[point] = new bool[255];
            }
        }

        #endregion

        #region Tile Entities

        private void LoadEntities(BinaryReader reader, int fileVersion)
        {
            TileEntity.ByID = TileEntity.ByID
                .Where(kv => RegionGuard.IsProtected(kv.Value.Position.X, kv.Value.Position.Y))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            TileEntity.ByPosition = TileEntity.ByPosition
                .Where(kv => RegionGuard.IsProtected(kv.Value.Position.X, kv.Value.Position.Y))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            int entityCount = reader.ReadInt32();
            int currentID = 0;
            for (int i = 0; i < entityCount; i++)
            {
                TileEntity entity = TileEntity.Read(reader, fileVersion, false);
                if (RegionGuard.IsProtected(entity.Position.X, entity.Position.Y))
                    continue;

                while (TileEntity.ByID.ContainsKey(currentID))
                    currentID++;

                entity.ID = currentID++;
                TileEntity.ByID[entity.ID] = entity;

                if (TileEntity.ByPosition.TryGetValue(entity.Position, out TileEntity old))
                    TileEntity.ByID.Remove(old.ID);

                TileEntity.ByPosition[entity.Position] = entity;
            }
            TileEntity.TileEntitiesNextID = currentID;

            RemoveInvalidEntities();
        }

        private static void RemoveInvalidEntities()
        {
            List<Point16> invalid = TileEntity.ByPosition.Values
                .Where(e => !WorldGen.InWorld(e.Position.X, e.Position.Y, 1) ||
                            !TileEntity.manager.CheckValidTile(e.type, e.Position.X, e.Position.Y))
                .Select(e => e.Position)
                .ToList();

            foreach (Point16 point in invalid)
            {
                if (TileEntity.ByPosition.TryGetValue(point, out TileEntity entity))
                {
                    TileEntity.ByID.Remove(entity.ID);
                    TileEntity.ByPosition.Remove(point);
                }
            }
        }

        #endregion

        #region Signs

        private void LoadSigns(BinaryReader reader)
        {
            short signCount = reader.ReadInt16();
            int pos = 0;

            for (int i = 0; i < signCount; i++)
            {
                string text = reader.ReadString();
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();

                ITile tile = Main.tile[x, y];
                Sign sign = null;
                if (tile.active() && Main.tileSign[tile.type])
                    sign = new Sign { text = text, x = x, y = y };

                if (sign != null && RegionGuard.IsProtected(sign.x, sign.y))
                    continue;

                while (pos < Sign.maxSigns && Main.sign[pos] != null &&
                       RegionGuard.IsProtected(Main.sign[pos].x, Main.sign[pos].y))
                    pos++;

                if (pos >= Sign.maxSigns)
                    break;

                Main.sign[pos++] = sign;
            }

            var seen = new HashSet<Point16>();
            for (int j = 0; j < Sign.maxSigns; j++)
            {
                if (Main.sign[j] != null && !seen.Add(new Point16(Main.sign[j].x, Main.sign[j].y)))
                    Main.sign[j] = null;
            }

            for (; pos < Sign.maxSigns; pos++)
            {
                if (Main.sign[pos] != null && RegionGuard.IsProtected(Main.sign[pos].x, Main.sign[pos].y))
                    continue;
                Main.sign[pos] = null;
            }
        }

        #endregion

        #region Tiles

        private void LoadTiles(BinaryReader reader, bool[] importance)
        {
            for (int x = 0; x < Main.maxTilesX; x++)
            {
                for (int y = 0; y < Main.maxTilesY; y++)
                {
                    ITile tile = ReadTile(reader, importance, out int tileType, out int repeat);

                    CountTiles(tileType, y, repeat);

                    if (!RegionGuard.IsProtected(x, y))
                        Main.tile[x, y].CopyFrom(tile);

                    while (repeat > 0)
                    {
                        y++;
                        if (!RegionGuard.IsProtected(x, y))
                            Main.tile[x, y].CopyFrom(tile);
                        repeat--;
                    }
                }
            }
        }

        private static void CountTiles(int tileType, int y, int repeat)
        {
            if (tileType == -1)
                return;

            if ((double)y <= Main.worldSurface)
            {
                if ((double)(y + repeat) <= Main.worldSurface)
                {
                    WorldGen.tileCounts[tileType] += (repeat + 1) * 5;
                }
                else
                {
                    int surfaceCount = (int)(Main.worldSurface - (double)y + 1.0);
                    int belowCount = repeat + 1 - surfaceCount;
                    WorldGen.tileCounts[tileType] += surfaceCount * 5 + belowCount;
                }
            }
            else
            {
                WorldGen.tileCounts[tileType] += repeat + 1;
            }
        }

        /// <summary>
        /// read one tile in vanila format (header 1-4 byte, data tile, lalu RLE count).
        /// </summary>
        private static ITile ReadTile(BinaryReader reader, bool[] importance, out int tileType, out int repeat)
        {
            tileType = -1;

            byte header4 = 0; // header 4 (invisible / fullbright)
            byte header3 = 0; // header 3 (actuator, wire4, wall tinggi, color)
            byte header2 = 0; // header 2 (wire, slope)
            byte header1 = reader.ReadByte();

            ITile tile = new Tile();

            bool hasHeader2 = (header1 & 1) == 1;
            if (hasHeader2)
                header2 = reader.ReadByte();

            bool hasHeader3 = hasHeader2 && (header2 & 1) == 1;
            if (hasHeader3)
                header3 = reader.ReadByte();

            if (hasHeader3 && (header3 & 1) == 1)
                header4 = reader.ReadByte();

            // Tile aktif
            if ((header1 & 2) == 2)
            {
                tile.active(true);

                if ((header1 & 32) == 32)
                {
                    byte low = reader.ReadByte();
                    tileType = (reader.ReadByte() << 8) | low;
                }
                else
                {
                    tileType = reader.ReadByte();
                }
                tile.type = (ushort)tileType;

                if (importance[tileType])
                {
                    tile.frameX = reader.ReadInt16();
                    tile.frameY = reader.ReadInt16();
                    if (tile.type == 144)
                        tile.frameY = 0;
                }
                else
                {
                    tile.frameX = -1;
                    tile.frameY = -1;
                }

                if ((header3 & 8) == 8)
                    tile.color(reader.ReadByte());
            }

            // Wall
            if ((header1 & 4) == 4)
            {
                tile.wall = reader.ReadByte();
                if (tile.wall >= WallID.Count)
                    tile.wall = 0;

                if ((header3 & 16) == 16)
                    tile.wallColor(reader.ReadByte());
            }

            // Liquid
            byte liquidType = (byte)((header1 & 24) >> 3);
            if (liquidType != 0)
            {
                tile.liquid = reader.ReadByte();
                if ((header3 & 128) == 128)
                {
                    tile.shimmer(true);
                }
                else if (liquidType > 1)
                {
                    if (liquidType == 2)
                        tile.lava(true);
                    else
                        tile.honey(true);
                }
            }

            // Wire & slope
            if (header2 > 1)
            {
                if ((header2 & 2) == 2) tile.wire(true);
                if ((header2 & 4) == 4) tile.wire2(true);
                if ((header2 & 8) == 8) tile.wire3(true);

                byte slope = (byte)((header2 & 112) >> 4);
                if (slope != 0 && (Main.tileSolid[tile.type] || TileID.Sets.NonSolidSaveSlopes[tile.type]))
                {
                    if (slope == 1)
                        tile.halfBrick(true);
                    else
                        tile.slope((byte)(slope - 1));
                }
            }

            // Actuator, wire4, wall (byte tinggi)
            if (header3 > 1)
            {
                if ((header3 & 2) == 2) tile.actuator(true);
                if ((header3 & 4) == 4) tile.inActive(true);
                if ((header3 & 32) == 32) tile.wire4(true);

                if ((header3 & 64) == 64)
                {
                    byte high = reader.ReadByte();
                    tile.wall = (ushort)((high << 8) | tile.wall);
                    if (tile.wall >= WallID.Count)
                        tile.wall = 0;
                }
            }

            // Invisible / fullbright
            if (header4 > 1)
            {
                if ((header4 & 2) == 2) tile.invisibleBlock(true);
                if ((header4 & 4) == 4) tile.invisibleWall(true);
                if ((header4 & 8) == 8) tile.fullbrightBlock(true);
                if ((header4 & 16) == 16) tile.fullbrightWall(true);
            }

            // RLE count
            byte repeatType = (byte)((header1 & 192) >> 6);
            if (repeatType == 0)
                repeat = 0;
            else if (repeatType == 1)
                repeat = reader.ReadByte();
            else
                repeat = reader.ReadInt16();

            return tile;
        }

        #endregion

        #region Chests

        private void LoadChests(BinaryReader reader)
        {
            if (WorldRegeneration.Config.IgnoreChests)
                return;

            int chestCount = reader.ReadInt16();
            int worldCapacity = reader.ReadInt16();
            int capacity = Math.Min(worldCapacity, ChestSlots);
            int overCapacity = Math.Max(0, worldCapacity - ChestSlots);

            int pos = 0;
            for (int i = 0; i < chestCount; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                var chest = new Chest(x, y) { name = reader.ReadString() };

                for (int j = 0; j < capacity; j++)
                {
                    short stack = reader.ReadInt16();
                    var item = new Item();
                    if (stack > 0)
                    {
                        item.netDefaults(reader.ReadInt32());
                        item.stack = stack;
                        item.Prefix(reader.ReadByte());
                    }
                    else if (stack < 0)
                    {
                        item.netDefaults(reader.ReadInt32());
                        item.Prefix(reader.ReadByte());
                        item.stack = 1;
                    }
                    chest.item[j] = item;
                }

                for (int k = 0; k < overCapacity; k++)
                {
                    if (reader.ReadInt16() > 0)
                    {
                        reader.ReadInt32();
                        reader.ReadByte();
                    }
                }

                if (RegionGuard.IsProtected(x, y))
                    continue;

                while (pos < Main.chest.Length && Main.chest[pos] != null &&
                       RegionGuard.IsProtected(Main.chest[pos].x, Main.chest[pos].y))
                    pos++;

                if (pos >= Main.chest.Length)
                    break;

                Main.chest[pos++] = chest;
            }

            var seen = new HashSet<Point16>();
            for (int l = 0; l < Math.Min(pos, Main.chest.Length); l++)
            {
                if (Main.chest[l] != null && !seen.Add(new Point16(Main.chest[l].x, Main.chest[l].y)))
                    Main.chest[l] = null;
            }

            for (; pos < Main.chest.Length; pos++)
            {
                if (Main.chest[pos] != null && RegionGuard.IsProtected(Main.chest[pos].x, Main.chest[pos].y))
                    continue;
                Main.chest[pos] = null;
            }

            if (WorldFile._versionNumber < 115)
                WorldFile.FixDresserChests();
        }

        #endregion
    }
}