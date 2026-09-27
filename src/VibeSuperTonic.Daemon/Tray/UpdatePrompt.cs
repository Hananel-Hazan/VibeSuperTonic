using Tmds.DBus.Protocol;

namespace VibeSuperTonic.Daemon.Tray;

/// <summary>
/// The desktop notification that asks whether to apply a snap update now or
/// later. See <see cref="SnapUpdateWatch"/> for why it exists.
///
/// <para><b>"Later" is the default, and it is also what ignoring it does.</b>
/// Nothing happens until the user picks "Update now", here or in the tray menu,
/// which keeps the item for as long as the update waits. snapd applies it by
/// itself after 14 days, and the text says so, because that restart is the
/// one surprise we cannot prevent.</para>
///
/// <para>org.freedesktop.Notifications, which the snap's <c>desktop</c> plug
/// allows. A server without actions (some minimal ones) still shows the text,
/// and the tray menu is the way to act.</para>
/// </summary>
internal sealed class UpdatePrompt : IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    internal const string ActionUpdate = "update-now";
    internal const string ActionLater = "later";

    private readonly Connection _connection;
    private readonly Action _updateNow;
    private readonly Action<string> _log;
    private IDisposable? _subscription;
    private uint _id;

    public UpdatePrompt(Connection connection, Action updateNow, Action<string> log)
    {
        _connection = connection;
        _updateNow = updateNow;
        _log = log;
    }

    /// <summary>The words, pure, so a test can read them.</summary>
    internal static (string Summary, string Body) Text(int revision) =>
        ("VibeSuperTonic has an update",
         $"Revision {revision} is ready. Updating closes VibeSuperTonic for a moment; " +
         "the next hotkey press takes a few seconds longer while the voice loads, and a " +
         "screen reader uses its default voice until you next log in. " +
         "Choose Later to keep working: the tray menu keeps the option, and the system " +
         "applies the update by itself within 14 days.");

    public async Task ShowAsync(int revision)
    {
        try
        {
            _subscription ??= await _connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = Service,
                    Member = "ActionInvoked",
                    Path = ObjectPath,
                },
                (Message m, object? _) =>
                {
                    var r = m.GetBodyReader();
                    return (r.ReadUInt32(), r.ReadString());
                },
                (Exception? ex, (uint Id, string Action) e, object? _, object? _) =>
                {
                    if (ex is not null || e.Id != _id || _id == 0) return;
                    if (e.Action == ActionUpdate) _updateNow();
                    else _log("update: the user chose Later");
                },
                emitOnCapturedContext: false);

            _id = await _connection.CallMethodAsync(Build(revision), (Message m, object? _) => m.GetBodyReader().ReadUInt32());
        }
        catch (Exception ex)
        {
            // The tray menu still offers it. A missing notification server is
            // not a reason for the daemon to complain louder than the log.
            _log($"update: could not show the notification: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // MessageWriter is a ref struct; see TrayIcon for why this is a separate method.
    private MessageBuffer Build(int revision)
    {
        var (summary, body) = Text(revision);
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service,
            path: ObjectPath,
            @interface: Service,
            member: "Notify",
            signature: "susssasa{sv}i");
        writer.WriteString("VibeSuperTonic");
        writer.WriteUInt32(_id);                         // replaces the last one, if any
        writer.WriteString("audio-speakers");
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(new[] { ActionUpdate, "Update now", ActionLater, "Later" });
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            // Stays until answered, on servers that honour it.
            ["resident"] = true,
            ["urgency"] = (byte)1,
        });
        writer.WriteInt32(0);                            // no timeout
        return writer.CreateMessage();
    }

    public void Dispose() => _subscription?.Dispose();
}
