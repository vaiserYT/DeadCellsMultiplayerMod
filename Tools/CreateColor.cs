namespace DeadCellsMultiplayerMod.Tools
{
    public static class MultiColor
    {
        public static int ColorFromHex(string hex)
        {
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 6) hex = "FF" + hex;
            return Convert.ToInt32(hex, 16);
        }
    }
}
