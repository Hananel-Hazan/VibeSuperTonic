namespace VibeSuperTonic.Core.Selection;

/// <summary>
/// Which selection a capture should read, given what the X server says about
/// how old each one is.
///
/// <para>The problem this exists for, found on 2026-08-16 against Gmail's
/// in-frame attachment viewer in Brave: some windows render selectable text and
/// never take ownership of PRIMARY. X11 has no empty state for a selection, so
/// the previous owner's text simply stays there. The daemon then reads it,
/// correctly by the protocol and wrongly by any user's expectation — and
/// because the stale text is usually the one already playing, re-reading it
/// sounds exactly like the hotkey doing nothing. The field report that failure
/// produces is "the hotkey stops working in Gmail", which points at the hotkey,
/// the daemon, and the socket, none of which are involved.</para>
///
/// <para><b>ICCCM's TIMESTAMP target is what makes this detectable.</b> Every
/// selection owner must answer it with the server time at which it acquired the
/// selection, so "has this selection been re-established since I last looked" is
/// a question with an actual answer. An earlier reading of this bug assumed no
/// such timestamp existed and tried to compare owner windows instead, which
/// cannot work: Chromium's selection owner is an unmapped window carrying
/// neither <c>WM_CLASS</c> nor <c>_NET_WM_PID</c>, so there is nothing on it to
/// compare against the focused application.</para>
///
/// <para>Kept here rather than beside the Xlib calls because it is arithmetic
/// and policy with no X in it, and because the wraparound rule below is the kind
/// of thing that wants tests rather than a machine that has been up long
/// enough.</para>
/// </summary>
public static class SelectionFreshness
{
    /// <summary>
    /// Is server time <paramref name="a"/> after <paramref name="b"/>?
    ///
    /// <para>X server time is milliseconds in 32 bits, so it wraps roughly every
    /// 49.7 days and a plain <c>&gt;</c> is wrong for the ~50 ms either side of
    /// the wrap on a long-lived session. ICCCM's rule is to compare the signed
    /// difference, which stays correct across it: any two timestamps less than
    /// 24.8 days apart order correctly regardless of where the wrap falls.</para>
    /// </summary>
    public static bool IsAfter(uint a, uint b) => unchecked((int)(a - b)) > 0;

    /// <summary>
    /// Decide which selection to read.
    /// </summary>
    /// <param name="primary">PRIMARY's owner window and ownership time, now.</param>
    /// <param name="lastRead">
    /// What PRIMARY looked like at the end of the previous successful capture,
    /// or null if this is the first capture since the daemon started.
    /// </param>
    /// <param name="clipboard">
    /// CLIPBOARD's owner and ownership time, or null when nothing owns it or the
    /// caller did not look.
    /// </param>
    /// <param name="clipboardFallback">
    /// Whether the user has opted into reading CLIPBOARD when PRIMARY is stale.
    /// Off by default, and deliberately: X11 offers no way to tell "the
    /// application did not publish my selection" apart from "the user pressed
    /// the key twice on the same selection" — both leave PRIMARY untouched. With
    /// the fallback on, the second case reads the clipboard instead of repeating
    /// the sentence, which is the wrong answer for a user who simply wanted to
    /// hear it again. The notice is emitted either way, so the behaviour stays
    /// explainable whichever way this is set.
    /// </param>
    public static SelectionUse Decide(
        SelectionStamp primary,
        SelectionStamp? lastRead,
        SelectionStamp? clipboard,
        bool clipboardFallback)
    {
        bool unchanged = lastRead is { } last
                      && last.Owner == primary.Owner
                      && last.Time == primary.Time;

        if (!unchanged)
            return SelectionUse.Primary;

        // Only a copy made AFTER the current selection was established can be
        // the thing the user meant. An older clipboard is whatever they were
        // doing before and reading it would be the "surprising fallback to the
        // last thing" that X11SelectionSource already refuses elsewhere.
        if (clipboardFallback && clipboard is { } clip && IsAfter(clip.Time, primary.Time))
            return SelectionUse.Clipboard;

        return SelectionUse.PrimaryUnchanged;
    }
}

/// <summary>A selection's owner window and the server time it took ownership.</summary>
public readonly record struct SelectionStamp(ulong Owner, uint Time);

/// <summary>What <see cref="SelectionFreshness.Decide"/> concluded.</summary>
public enum SelectionUse
{
    /// <summary>PRIMARY changed since the last read: an ordinary, fresh capture.</summary>
    Primary,

    /// <summary>
    /// PRIMARY is byte-for-byte the same claim as last time. Still read — a
    /// deliberate repeat is legitimate — but say so, because the other cause is
    /// an application that never published the selection at all.
    /// </summary>
    PrimaryUnchanged,

    /// <summary>PRIMARY is stale and the user copied something more recently.</summary>
    Clipboard,
}
