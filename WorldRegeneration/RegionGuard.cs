using System.Linq;
using TShockAPI;

namespace WorldRegeneration
{
    public static class RegionGuard
    {
        /// <summary>True if the tile is within a protected region (Z >= MaxZRegion).</summary>
        public static bool IsProtected(int x, int y) =>
            TShock.Regions.InAreaRegion(x, y)
                  .Any(r => r != null && r.Z >= WorldRegeneration.Config.MaxZRegion);
    }
}