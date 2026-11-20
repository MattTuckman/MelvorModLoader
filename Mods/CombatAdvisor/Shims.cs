namespace Adventurers;

// The fight model comes from the Adventurers mod. These two small pieces of that mod are all it asks for besides
// its own files; the Combat Advisor never sends anyone to a lodge, so the recovery time is only a default.
static class Loot
{
    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

static class Lodge
{
    public static double RecoveryHours => 2.0;
}
