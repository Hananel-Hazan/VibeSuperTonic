namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// How the daemon tells the Speech Dispatcher module to step aside when the user
/// chooses to apply a snap update now. snapd applies it only once none of the
/// snap's apps runs, and the module is one of them for as long as
/// speech-dispatcher lives.
///
/// <para>A file rather than a message: the module is speech-dispatcher's child
/// and talks to the daemon only while it renders, so there is no connection to
/// send one on. It lives in <c>$SNAP_USER_COMMON</c>, which both share, and
/// holds the revision that should go.</para>
///
/// <para><b>Only a marker written after the module started counts.</b> If the
/// update has not happened yet when speech-dispatcher next starts the module
/// (the old revision is still current), an old marker must not make that
/// module exit the moment it starts, which would leave the screen reader on its
/// fallback voice with nobody having asked.</para>
/// </summary>
public static class SnapUpdateMarker
{
    public const string FileName = "update-now";

    /// <summary>
    /// Should a module of revision <paramref name="own"/>, started at
    /// <paramref name="startedUtc"/>, exit for this marker?
    /// </summary>
    public static bool ShouldExit(string? own, string? markerRevision, DateTime markerWrittenUtc, DateTime startedUtc) =>
        !string.IsNullOrEmpty(own)
        && markerRevision?.Trim() == own
        && markerWrittenUtc > startedUtc;

    /// <summary>The marker's path, or null outside a snap.</summary>
    public static string? PathIn(string? snapUserCommon) =>
        string.IsNullOrEmpty(snapUserCommon) ? null : System.IO.Path.Combine(snapUserCommon, FileName);
}
