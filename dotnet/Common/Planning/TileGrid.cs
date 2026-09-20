using System;

namespace WhereTheyGo
{
    // The rasters this mod lays over the map: how many cells cover a size, which cell
    // a world position falls in (clamped to the grid), and a cell's centre. The 32 m
    // heat-map tiles and the 256 m demand zones both come through here.
    internal static class TileGrid
    {
        public static int2Like GridDims(float2Like size, float cellSize)
        {
            return new int2Like(
                Math.Max(1, (int)Math.Ceiling(size.x / cellSize)),
                Math.Max(1, (int)Math.Ceiling(size.y / cellSize)));
        }

        public static int2Like WorldToCell(float2Like pos, float2Like worldMin, float cellSize, int2Like gridSize)
        {
            float2Like rel = (pos - worldMin) / cellSize;
            int x = (int)Math.Floor(rel.x);
            int y = (int)Math.Floor(rel.y);
            return new int2Like(
                Math.Max(0, Math.Min(gridSize.x - 1, x)),
                Math.Max(0, Math.Min(gridSize.y - 1, y)));
        }

        // Centre of a cell in world space: the inverse of WorldToCell, to within the
        // half-cell the floor above threw away.
        public static float2Like CellCentre(int2Like cell, float2Like worldMin, float cellSize)
        {
            return worldMin + (new float2Like(cell.x + 0.5f, cell.y + 0.5f) * cellSize);
        }
    }
}
