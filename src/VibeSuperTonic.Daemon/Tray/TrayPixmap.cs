namespace VibeSuperTonic.Daemon.Tray;

/// <summary>What the icon is saying.</summary>
internal enum TrayState
{
    /// <summary>Nothing is being read. The press that starts one is ~28 ms away.</summary>
    Idle,

    /// <summary>Acknowledged, no audio yet. This is the ~750 ms the blip exists for.</summary>
    Preparing,

    Speaking,
}

/// <summary>
/// The icon, drawn in code rather than shipped as a file.
///
/// <para><b>Why procedural.</b> StatusNotifierItem offers two ways to have an
/// icon: <c>IconName</c>, which resolves through the desktop's icon theme, and
/// <c>IconPixmap</c>, which is raw ARGB32 on the wire. A theme name needs the
/// icon installed into a theme directory — and this product ships as a portable
/// folder that the user can move anywhere, with no install step that could
/// register one. An icon that resolves on the developer's machine and not on a
/// copied folder is the same class of defect as a hardcoded path.</para>
///
/// <para>So: no files, no theme, no install, and state changes cost an array
/// allocation. The shapes are deliberately crude, because at 22 pixels
/// everything is.</para>
/// </summary>
internal static class TrayPixmap
{
    /// <summary>
    /// Two sizes, because the host picks and panels differ. 22 is the common
    /// Cinnamon/GNOME tray size and 44 covers the 2x scale this machine runs.
    /// </summary>
    public static IReadOnlyList<(int Width, int Height, byte[] Argb)> For(TrayState state, bool uiAttached) =>
        [Render(22, state, uiAttached), Render(44, state, uiAttached)];

    // Idle is deliberately muted rather than absent: an icon that vanishes when
    // nothing is happening is an icon the user cannot find when they want the
    // menu.
    private static (byte R, byte G, byte B) Colour(TrayState state) => state switch
    {
        TrayState.Speaking => (0x35, 0xB4, 0x5A),
        TrayState.Preparing => (0xE0, 0xA0, 0x20),
        _ => (0x9A, 0xA0, 0xA6),
    };

    private static (int, int, byte[]) Render(int size, TrayState state, bool uiAttached)
    {
        var (r, g, b) = Colour(state);
        var argb = new byte[size * size * 4];

        // Supersampled 3x3. Antialiasing a 22-pixel circle by hand is cheaper
        // than any dependency that would do it properly.
        const int Sub = 3;
        double centre = (size - 1) / 2.0;
        double unit = size / 2.0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < Sub; sy++)
                {
                    for (int sx = 0; sx < Sub; sx++)
                    {
                        double px = x + (sx + 0.5) / Sub - 0.5;
                        double py = y + (sy + 0.5) / Sub - 0.5;
                        double d = Math.Sqrt((px - centre) * (px - centre) + (py - centre) * (py - centre)) / unit;

                        if (Covers(d, state, uiAttached)) hits++;
                    }
                }

                int i = (y * size + x) * 4;
                byte alpha = (byte)(255 * hits / (Sub * Sub));

                // ARGB32 in network byte order, which the spec asks for in as
                // many words — so A, R, G, B, big-endian, not the little-endian
                // BGRA a memory dump of an int would give.
                argb[i + 0] = alpha;
                argb[i + 1] = r;
                argb[i + 2] = g;
                argb[i + 3] = b;
            }
        }

        return (size, size, argb);
    }

    /// <summary>
    /// The glyph, as a predicate over distance from the centre in half-widths.
    /// Idle is a ring, preparing adds the centre, speaking fills it — a
    /// progression rather than three unrelated pictures, so the state is
    /// readable at a glance without learning three symbols.
    /// </summary>
    private static bool Covers(double d, TrayState state, bool uiAttached)
    {
        // A window is attached: a thin outer ring. Same information the plan
        // asks the icon to carry, in the one place on a 22-pixel canvas that is
        // still free once the state has had its say.
        if (uiAttached && d >= 0.86 && d <= 0.98) return true;

        return state switch
        {
            TrayState.Speaking => d <= 0.72,
            TrayState.Preparing => (d >= 0.50 && d <= 0.72) || d <= 0.26,
            _ => d >= 0.50 && d <= 0.72,
        };
    }
}
