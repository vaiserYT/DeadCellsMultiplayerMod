namespace DeadCellsMultiplayerMod.Tools
{
    public static class MultiColor
    {
        public static int CreateColor(int r, int g, int b, int a = 255)
        {
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        public static int ColorFromHex(string hex)
        {
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 6) hex = "FF" + hex;
            return Convert.ToInt32(hex, 16);
        }
    }
}
