using Tmds.DBus.Protocol;

namespace VibeSuperTonic.Daemon;

/// <summary>What one attempt to press Ctrl+C for the user came to.</summary>
/// <param name="Sent">The key was sent. It says nothing about whether anything was copied.</param>
/// <param name="Why">When it was not sent: the sentence the user is shown.</param>
/// <param name="Pending">Not sent yet because a person has a dialog to answer. The
/// press says so and stops; reading the stale selection instead is the bug this
/// feature exists for.</param>
internal readonly record struct CopyKeyResult(bool Sent, string? Why = null, bool Pending = false);

/// <summary>
/// Presses Ctrl+C in whichever application has the keyboard focus.
///
/// <para>A seam, like <see cref="ISelectionSource"/>, because the real thing needs
/// a desktop session, a portal and a person to click a dialog.</para>
/// </summary>
internal interface ICopyKey
{
    CopyKeyResult Send();
}

/// <summary>
/// Ctrl+C through the XDG RemoteDesktop portal, which is the one way a background
/// process may press a key on a Wayland desktop.
///
/// <para><b>Why this exists.</b> Reported 2026-10-30: in PdfCraft and similar
/// egui applications highlighting text never publishes a selection, so the hotkey
/// reads whatever was selected last. The only thing that worked was Ctrl+C by
/// hand. Measured the same day on Plasma 6 / Wayland: a session created through
/// this portal with the keyboard device pressed Ctrl+C in PdfCraft and the text
/// arrived on the clipboard; a session started with a remembered
/// <c>restore_token</c> opened with no dialog (0.0 s against 2.9 s).</para>
///
/// <para><b>The first press asks the desktop's permission, and the dialog is a
/// person's to answer.</b> Setup therefore runs in the background and the press
/// that started it is told to try again, rather than holding the hotkey for the
/// length of a dialog. The grant is remembered in <c>remotedesktop.token</c>
/// beside <c>settings.json</c>, so a later daemon start does not ask again.</para>
///
/// <para>The session stays open for the daemon's life: closing it revokes the
/// grant. A call that fails drops it, and the next press sets up again.</para>
/// </summary>
internal sealed class PortalCopyKey : ICopyKey
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    private const string Request = "org.freedesktop.portal.Request";

    private const uint KeyboardDevice = 1;
    /// <summary>"Persist until revoked" in the portal's vocabulary.</summary>
    private const uint PersistUntilRevoked = 2;
    private const int LeftCtrl = 29, KeyC = 46;                // evdev codes

    /// <summary>
    /// The hotkey is Ctrl+` and its modifier is usually still down when the press
    /// reaches the daemon. Sending the key at once would arrive as Ctrl pressed
    /// twice. Not measured against a real hotkey yet; see the setting's notes.
    /// </summary>
    internal const int ReleaseGraceMs = 250;

    private static readonly TimeSpan DialogPatience = TimeSpan.FromMinutes(2);

    private readonly string _tokenFile;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private Task? _setup;
    private Connection? _connection;
    private string? _session;
    private string? _setupFailure;
    private int _requestCounter;

    public PortalCopyKey(string dataDir, Action<string> log)
    {
        _tokenFile = Path.Combine(dataDir, "remotedesktop.token");
        _log = log;
    }

    public CopyKeyResult Send()
    {
        Connection? connection;
        string? session;
        lock (_lock)
        {
            connection = _connection;
            session = _session;
            if (session is null)
            {
                if (_setup is null || _setup.IsCompleted)
                {
                    string? last = _setupFailure;
                    _setupFailure = null;
                    _setup = Task.Run(SetUpAsync);
                    if (last is not null)
                        return new CopyKeyResult(false, last);
                }
                return new CopyKeyResult(false,
                    "waiting for your permission to press Ctrl+C for you. Approve the " +
                    "dialog the desktop is showing, then press the hotkey again.", Pending: true);
            }
        }

        try
        {
            Thread.Sleep(ReleaseGraceMs);
            foreach (var (code, down) in new[] { (LeftCtrl, true), (KeyC, true), (KeyC, false), (LeftCtrl, false) })
                connection!.CallMethodAsync(BuildKey(connection!, session, code, down)).GetAwaiter().GetResult();
            return new CopyKeyResult(true);
        }
        catch (Exception ex)
        {
            // The session is gone (the desktop revoked it, or restarted). Drop it so
            // the next press asks again instead of failing the same way forever.
            _log($"auto-copy: sending Ctrl+C failed: {ex.GetType().Name}: {ex.Message}");
            lock (_lock) { _session = null; _connection = null; }
            return new CopyKeyResult(false, "the desktop refused the Ctrl+C key press; press the hotkey again to set it up again.");
        }
    }

    private async Task SetUpAsync()
    {
        try
        {
            string? address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (string.IsNullOrEmpty(address))
                throw new InvalidOperationException("no session bus, so the daemon was started outside the desktop session");

            var connection = new Connection(address);
            await connection.ConnectAsync();
            string sender = connection.UniqueName![1..].Replace('.', '_');

            var created = await RequestAsync(connection, sender, "CreateSession",
                token => BuildCreateSession(connection, token), TimeSpan.FromSeconds(10));
            string session = created["session_handle"].GetString();

            string? saved = ReadToken();
            await RequestAsync(connection, sender, "SelectDevices",
                token => BuildSelectDevices(connection, session, token, saved), TimeSpan.FromSeconds(10));

            var started = await RequestAsync(connection, sender, "Start",
                token => BuildStart(connection, session, token), DialogPatience);

            if (started.TryGetValue("restore_token", out var fresh))
                WriteToken(fresh.GetString());

            lock (_lock) { _connection = connection; _session = session; }
            _log("auto-copy: the desktop granted keyboard access");
        }
        catch (Exception ex)
        {
            string why = ex is PortalDeniedException
                ? "the desktop did not allow the key press, so the hotkey will keep reading the selection only. Press again to be asked once more."
                : $"could not set up the key press ({ex.Message})";
            _log($"auto-copy: {why} [{ex.GetType().Name}]");
            lock (_lock) _setupFailure = why;
        }
    }

    private sealed class PortalDeniedException() : Exception("denied");

    /// <summary>
    /// One portal call that answers later, on a <c>Request</c> object. The reply
    /// signal is subscribed to <b>before</b> the call: the portal may answer
    /// before the method call returns, and a subscription made afterwards would
    /// wait for ever for a signal that had already gone.
    /// </summary>
    private async Task<Dictionary<string, VariantValue>> RequestAsync(
        Connection connection, string sender, string member,
        Func<string, MessageBuffer> build, TimeSpan patience)
    {
        string token = $"vstreq{Environment.ProcessId}_{Interlocked.Increment(ref _requestCounter)}";
        string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        var answer = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = Request, Member = "Response", Path = requestPath },
            (Message m, object? _) =>
            {
                var r = m.GetBodyReader();
                uint code = r.ReadUInt32();
                var results = r.ReadDictionaryOfStringToVariantValue();
                return (code, results);
            },
            (Exception? ex, (uint Code, Dictionary<string, VariantValue> Results) e, object? _, object? _) =>
            {
                if (ex is not null) answer.TrySetException(ex);
                else answer.TrySetResult(e);
            },
            emitOnCapturedContext: false);

        await connection.CallMethodAsync(build(token));

        var finished = await Task.WhenAny(answer.Task, Task.Delay(patience));
        if (finished != answer.Task)
            throw new TimeoutException($"{member}: no answer within {patience.TotalSeconds:0} s");

        var (code, results) = await answer.Task;
        // 0 success, 1 the user cancelled, 2 anything else.
        if (code == 1) throw new PortalDeniedException();
        if (code != 0) throw new InvalidOperationException($"{member}: the portal answered {code}");
        return results;
    }

    // MessageWriter is built and used in one method and never handed to a callback:
    // passing it on copies it, and the copy's writes are lost, which the bus answers
    // by closing the connection ("Connection closed by peer", found 2026-10-30).
    private static MessageBuffer BuildCreateSession(Connection connection, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service, path: ObjectPath, @interface: RemoteDesktop,
            member: "CreateSession", signature: "a{sv}");
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["session_handle_token"] = "vst" + Environment.ProcessId,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSelectDevices(Connection connection, string session, string token, string? restore)
    {
        var options = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["types"] = KeyboardDevice,
            ["persist_mode"] = PersistUntilRevoked,
        };
        if (restore is not null) options["restore_token"] = restore;

        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service, path: ObjectPath, @interface: RemoteDesktop,
            member: "SelectDevices", signature: "oa{sv}");
        writer.WriteObjectPath(session);
        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildStart(Connection connection, string session, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service, path: ObjectPath, @interface: RemoteDesktop,
            member: "Start", signature: "osa{sv}");
        writer.WriteObjectPath(session);
        writer.WriteString("");                                   // parent window
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildKey(Connection connection, string session, int code, bool down)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service, path: ObjectPath, @interface: RemoteDesktop,
            member: "NotifyKeyboardKeycode", signature: "oa{sv}iu");
        writer.WriteObjectPath(session);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        writer.WriteInt32(code);
        writer.WriteUInt32(down ? 1u : 0u);
        return writer.CreateMessage();
    }

    private string? ReadToken()
    {
        try { return File.Exists(_tokenFile) ? File.ReadAllText(_tokenFile).Trim() is { Length: > 0 } t ? t : null : null; }
        catch (IOException) { return null; }
    }

    private void WriteToken(string token)
    {
        try { File.WriteAllText(_tokenFile, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"auto-copy: could not remember the permission ({ex.Message}); the desktop will ask again next start");
        }
    }
}
