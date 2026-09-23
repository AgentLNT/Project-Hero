namespace ProjectHero.Core.Grid
{
    // 12-Direction System
    // Even numbers (0, 2, 4...) are Vertex-Aligned (Edge directions) - 0, 60, 120...
    // Odd numbers (1, 3, 5...) are Face-Aligned (Corner directions) - 30, 90, 150...
    public enum GridDirection
    {
        East = 0,           // 0 deg
        EastNorth = 1,      // 30 deg
        NorthEast = 2,      // 60 deg
        North = 3,          // 90 deg
        NorthWest = 4,      // 120 deg
        WestNorth = 5,      // 150 deg
        West = 6,           // 180 deg
        WestSouth = 7,      // 210 deg
        SouthWest = 8,      // 240 deg
        South = 9,          // 270 deg
        SouthEast = 10,     // 300 deg
        EastSouth = 11      // 330 deg
    }
}
