using Tmds.DBus.Protocol;
using VibeSuperTonic.Core.Export;

namespace VibeSuperTonic.Ui;

/// <summary>
/// Makes a save-dialog result writable from a window the tray opened in the snap.
///
/// <para><b>Why.</b> The tray opens the window from the daemon, so it runs under
/// the <c>ctl</c> app's confinement. snapd reports that app's own desktop file
/// to the portal, and Kubuntu's portal grants the chosen file to
/// <c>snap.vibesupertonic_ctl</c>. But every app of the snap sees only the
/// portal folder of <c>snap.vibesupertonic</c>, so the granted file is
/// invisible and the export failed with "the folder /run/user/1000/doc/… does
/// not exist". Found on Kubuntu on 2026-10-16, in stable revision 16: the
/// 2026-10-15 "fix" had been confirmed only by overwriting a file that already
/// carried a grant the snap could see.</para>
///
/// <para><b>How.</b> The grant carries <c>grant-permissions</c>, so the window
/// passes read and write on to <c>snap.&lt;instance&gt;</c> through
/// <c>org.freedesktop.portal.Documents.GrantPermissions</c>, which the desktop
/// interface allows. Measured from inside <c>ctl</c>'s confinement before this
/// was written: the folder appeared, and a file written into it landed in
/// ~/Downloads. A window opened from the menu never needs it.</para>
/// </summary>
internal static class SnapDocumentGrant
{
    /// <summary>
    /// Null when the path's folder is usable (or this is not a snap, or not a
    /// portal path). Otherwise why it could not be made usable, for the status line.
    /// </summary>
    public static async Task<string?> EnsureWritableAsync(string path)
    {
        string? instance = Environment.GetEnvironmentVariable("SNAP_INSTANCE_NAME");
        if (string.IsNullOrEmpty(instance)) return null;
        if (ExportRunner.DocumentId(path) is not { } id) return null;

        string dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir)) return null;

        string? address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrEmpty(address)) return "the folder for that file is not visible, and there is no session bus to fix it";

        try
        {
            using var connection = new Connection(address);
            await connection.ConnectAsync();
            await connection.CallMethodAsync(Build(connection, id, $"snap.{instance}"));
        }
        catch (DBusException ex)
        {
            return $"the folder for that file is not visible, and granting it failed: {ex.ErrorName}";
        }

        return Directory.Exists(dir)
            ? null
            : "the folder for that file is still not visible after granting it. Open the window from the application menu and export from there";
    }

    // MessageWriter is a ref struct: built in its own method, awaited outside it.
    private static MessageBuffer Build(Connection connection, string id, string appId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.portal.Documents",
            path: "/org/freedesktop/portal/documents",
            @interface: "org.freedesktop.portal.Documents",
            member: "GrantPermissions",
            signature: "ssas");
        writer.WriteString(id);
        writer.WriteString(appId);
        writer.WriteArray(new[] { "read", "write" });
        return writer.CreateMessage();
    }
}
