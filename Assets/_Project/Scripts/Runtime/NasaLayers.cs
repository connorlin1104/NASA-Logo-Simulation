namespace NasaSim
{
    /// <summary>
    /// Project layer indices. Layers 8/9 are written into TagManager by
    /// Tools &gt; NASA Sim &gt; Setup &gt; Configure Layers &amp; Physics; 4 (Water) is a Unity built-in.
    /// Kept as constants so runtime code never depends on LayerMask.NameToLayer succeeding before the
    /// setup menu has been run.
    /// </summary>
    public static class NasaLayers
    {
        public const int Water = 4;
        public const int Player = 8;
        public const int Interactable = 9;

        public const int PlayerMask = 1 << Player;
        public const int InteractableMask = 1 << Interactable;
    }
}
