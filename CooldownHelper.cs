
namespace CooldownHelper
{
    public static class Cooldown
    {
        public static (int index, int sub) Decode(int key)
        {
            int index = (int)((uint)key >> 21);
            int sub = key & 0x1FFFFF;
            return (index, sub);
        }

        public static int Encode(int index, int sub = 0)
        {
            return (index << 21) | sub;
        }

        public static class Keys
        {
            public const int JUMP_HIT = 585;
        }
    }
}