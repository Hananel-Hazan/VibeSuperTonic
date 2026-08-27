using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeSuperTonic.Daemon.Interop;

/// <summary>
/// The ext-data-control-v1 protocol, by hand.
///
/// <para>This file is the protocol only — connect, ask, read bytes. Every policy
/// decision (which selection, how much of it, what to tell the user when it is
/// not there) lives in <see cref="WaylandSelectionSource"/>, the same split
/// <see cref="X11Native"/> and <c>X11SelectionSource</c> already use.</para>
///
/// <para><b>Why this protocol.</b> <c>zwp_primary_selection_device_manager_v1</c>
/// is what an ordinary application uses, and it only delivers a selection to the
/// client holding KEYBOARD FOCUS — which a background daemon never has, so it
/// would always read nothing. ext-data-control-v1 exists for exactly the
/// unfocused, privileged case: clipboard managers, and us. It is the successor to
/// <c>wlr-data-control-unstable-v1</c>; KWin advertises it and dropped the older
/// name, which is why <c>wl-paste</c> from wl-clipboard 2.2.1 falls back to
/// spawning an invisible surface and stealing focus. Measured on Plasma 6.6.6,
/// 2026-08-22. Do not imitate that fallback: it would pull focus off the user's
/// application on every hotkey press.</para>
///
/// <para><b>Why the code looks like this.</b> libwayland has no ABI for a
/// protocol it does not define. <c>wayland-scanner</c> normally reads the XML and
/// generates the <c>wl_interface</c>/<c>wl_message</c> descriptor tables as C.
/// There is no scanner in this build, so the tables are constructed in unmanaged
/// memory below, transcribed from the published protocol XML rather than
/// guessed. Two things follow, and both have teeth:</para>
/// <list type="number">
///   <item>A wrong offset or signature is a <b>SIGSEGV inside libwayland</b>, not
///   a managed exception — no stack trace, no log line. This was built and proven
///   in <c>spike/wayland-selection</c> before it came near the daemon, for that
///   reason.</item>
///   <item>Descriptors must outlive every proxy referencing them, so they are
///   built <b>once per process</b> and never freed. Building them per capture
///   would leak steadily in a process designed to run for a week.</item>
/// </list>
///
/// <para>Opcodes are array positions: the wire carries an index, never a name, so
/// reordering any array below silently calls a different request.</para>
/// </summary>
internal static unsafe class WaylandNative
{
    private const string WL = "libwayland-client.so.0";

    [DllImport(WL)] internal static extern IntPtr wl_display_connect(string? name);
    [DllImport(WL)] internal static extern void   wl_display_disconnect(IntPtr display);
    [DllImport(WL)] internal static extern int    wl_display_roundtrip(IntPtr display);
    [DllImport(WL)] internal static extern int    wl_display_flush(IntPtr display);
    [DllImport(WL)] internal static extern uint   wl_proxy_get_version(IntPtr proxy);
    [DllImport(WL)] internal static extern int    wl_proxy_add_listener(IntPtr proxy, IntPtr impl, IntPtr data);
    [DllImport(WL)] internal static extern IntPtr wl_proxy_marshal_array_flags(
        IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr args);

    [DllImport("libc", SetLastError = true)] private static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte* buf, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int poll(PollFd* fds, nuint nfds, int timeoutMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Fd; public short Events; public short Revents; }

    private const short POLLIN = 0x001;
    private const uint WlDisplayGetRegistry = 1;
    private const uint WlRegistryBind = 0;

    // x86-64 layouts.
    //   wl_message   { char *name; char *signature; wl_interface **types; }      24
    //   wl_interface { char *name; int version; int method_count;
    //                  wl_message *methods; int event_count; wl_message *events } 40
    // Offsets: name 0, version 8, method_count 12, methods 16, event_count 24,
    // (4 bytes padding), events 32.
    private const int MessageSize = 24;
    private const int InterfaceSize = 40;

    private static readonly object Gate = new();
    private static bool _built;
    private static IntPtr _ifaceManager, _ifaceDevice, _ifaceOffer, _wlRegistryIface, _wlSeatIface;
    private static IntPtr _registryListener, _deviceListener, _offerListener;

    // Callback state. Static because [UnmanagedCallersOnly] cannot capture; guarded
    // by Gate, which is also what makes one-capture-at-a-time true rather than hoped.
    private static uint _managerName, _managerVersion, _seatName, _seatVersion;
    private static bool _sawManager, _sawSeat, _finished;
    private static IntPtr _primaryOffer, _clipboardOffer;
    private static readonly Dictionary<IntPtr, List<string>> OfferMimes = new();

    /// <summary>What a capture attempt saw at the protocol level.</summary>
    internal readonly record struct WaylandCapture(
        bool Connected, bool ProtocolPresent, bool HadOffer, string? Text, string? Error);

    /// <summary>
    /// Read one selection. <paramref name="primary"/> false reads the clipboard.
    ///
    /// <para>Connects and disconnects per call rather than holding a session
    /// open. That is deliberate and it is the audio-device lesson applied before
    /// it could bite: a long-lived handle to a compositor that can restart is a
    /// stale handle waiting to happen, and this costs a few milliseconds against
    /// a 300 ms budget — measured at 30 ms including process start, so a fraction
    /// of that in-process.</para>
    /// </summary>
    internal static WaylandCapture Capture(bool primary, int timeoutMs)
    {
        lock (Gate)
        {
            IntPtr display = wl_display_connect(null);
            if (display == IntPtr.Zero)
                return new WaylandCapture(false, false, false, null, null);

            try
            {
                Build();
                ResetState();

                IntPtr regArgs = Zeroed(IntPtr.Size);
                IntPtr registry = wl_proxy_marshal_array_flags(
                    display, WlDisplayGetRegistry, _wlRegistryIface,
                    wl_proxy_get_version(display), 0, regArgs);
                Marshal.FreeHGlobal(regArgs);
                if (registry == IntPtr.Zero)
                    return new WaylandCapture(true, false, false, null, "no wl_registry");

                wl_proxy_add_listener(registry, _registryListener, IntPtr.Zero);
                wl_display_roundtrip(display);

                if (!_sawManager || !_sawSeat)
                    return new WaylandCapture(true, false, false, null, null);

                IntPtr manager = Bind(registry, _managerName, _ifaceManager,
                                      "ext_data_control_manager_v1", Math.Min(_managerVersion, 1u));
                IntPtr seat = Bind(registry, _seatName, _wlSeatIface, "wl_seat", Math.Min(_seatVersion, 1u));
                if (manager == IntPtr.Zero || seat == IntPtr.Zero)
                    return new WaylandCapture(true, true, false, null, "bind failed");

                // device = manager.get_data_device(seat)  — opcode 1, "no"
                IntPtr devArgs = Zeroed(IntPtr.Size * 2);
                Marshal.WriteIntPtr(devArgs, 0, IntPtr.Zero);       // new_id placeholder
                Marshal.WriteIntPtr(devArgs, IntPtr.Size, seat);
                IntPtr device = wl_proxy_marshal_array_flags(
                    manager, 1, _ifaceDevice, wl_proxy_get_version(manager), 0, devArgs);
                Marshal.FreeHGlobal(devArgs);
                if (device == IntPtr.Zero)
                    return new WaylandCapture(true, true, false, null, "get_data_device returned null");

                wl_proxy_add_listener(device, _deviceListener, IntPtr.Zero);

                // Two roundtrips: the first carries data_offer and the burst of
                // `offer` events naming its MIME types, the second guarantees the
                // selection/primary_selection event that says which offer is
                // current has arrived. One is usually enough, and "usually" is not
                // a property worth shipping in the path behind a hotkey.
                wl_display_roundtrip(display);
                wl_display_roundtrip(display);

                if (_finished)
                    return new WaylandCapture(true, true, false, null,
                        "another data-control client took over the seat");

                IntPtr chosen = primary ? _primaryOffer : _clipboardOffer;
                if (chosen == IntPtr.Zero)
                    return new WaylandCapture(true, true, false, null, null);

                var offered = OfferMimes.TryGetValue(chosen, out var m) ? m : new List<string>();
                string? mime = PickTextMime(offered);
                if (mime is null)
                    return new WaylandCapture(true, true, true, null,
                        offered.Count == 0
                            ? "the selection offered no types at all"
                            : $"the selection offers no text type (offered: {string.Join(", ", offered)})");

                string text = Receive(display, chosen, mime, timeoutMs);
                return new WaylandCapture(true, true, true, text, null);
            }
            finally
            {
                wl_display_disconnect(display);
            }
        }
    }

    /// <summary>
    /// Is there a Wayland compositor to talk to at all? Cheap, and used to decide
    /// which selection source the daemon builds.
    ///
    /// <para><b>A MISSING libwayland-client IS AN ANSWER, NOT AN ERROR.</b> This
    /// is asked once, from the daemon's startup path, and the P/Invoke below
    /// throws <see cref="DllNotFoundException"/> when the machine has no
    /// <c>libwayland-client.so.0</c> — which killed the daemon outright, before
    /// it had bound its socket, with a stack trace about a shared library.</para>
    ///
    /// <para>Every desktop that could possibly run Wayland has that library, so
    /// this was invisible until something asked without one: a container, a
    /// minimal X11-only install, a server. Found 2026-08-27 by
    /// <c>build/smoke-test.sh</c> inside a bare <c>ubuntu:22.04</c>, which is the
    /// first machine to run this product that did not already have a desktop on
    /// it.</para>
    ///
    /// <para>The honest reading of "the Wayland client library is not installed"
    /// is <c>false</c> — there is no compositor here — and the daemon then builds
    /// the X11 source and says so in its log, which is what it does on any X11
    /// session anyway.</para>
    /// </summary>
    internal static bool CanConnect()
    {
        IntPtr d;
        try
        {
            d = wl_display_connect(null);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // A libwayland-client.so.0 that exists and is not one — stub
            // packages and ABI-renamed forks both turn up in the wild.
            return false;
        }

        if (d == IntPtr.Zero) return false;
        wl_display_disconnect(d);
        return true;
    }

    // ------------------------------------------------------------- internals

    private static void ResetState()
    {
        _sawManager = _sawSeat = _finished = false;
        _managerName = _managerVersion = _seatName = _seatVersion = 0;
        _primaryOffer = _clipboardOffer = IntPtr.Zero;
        OfferMimes.Clear();
    }

    private static IntPtr Bind(IntPtr registry, uint name, IntPtr iface, string ifaceName, uint version)
    {
        // wl_registry.bind is the one request whose new_id carries an explicit
        // interface name and version on the wire — signature "usun".
        IntPtr args = Zeroed(IntPtr.Size * 4);
        IntPtr nameStr = Utf8(ifaceName);
        Marshal.WriteInt32(args, 0, (int)name);
        Marshal.WriteIntPtr(args, IntPtr.Size, nameStr);
        Marshal.WriteInt32(args, IntPtr.Size * 2, (int)version);
        Marshal.WriteIntPtr(args, IntPtr.Size * 3, IntPtr.Zero);
        IntPtr p = wl_proxy_marshal_array_flags(registry, WlRegistryBind, iface, version, 0, args);
        Marshal.FreeHGlobal(args);
        Marshal.FreeHGlobal(nameStr);
        return p;
    }

    /// <summary>
    /// Most specific first. The X11-era names are still offered by toolkits
    /// bridging out of Xwayland, and accepting them costs nothing — KWin bridges
    /// XWayland selections into Wayland, so this source sees legacy clients too,
    /// which is what makes it a replacement for the X11 one rather than a peer.
    /// </summary>
    private static string? PickTextMime(List<string> offered)
    {
        string[] order =
        {
            "text/plain;charset=utf-8", "text/plain;charset=UTF-8",
            "UTF8_STRING", "text/plain", "STRING", "TEXT",
        };
        foreach (string want in order)
            foreach (string have in offered)
                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                    return have;
        return null;
    }

    private static string Receive(IntPtr display, IntPtr offer, string mime, int timeoutMs)
    {
        int* fds = stackalloc int[2];
        if (pipe(fds) != 0) return "";

        IntPtr args = Zeroed(IntPtr.Size * 2);
        IntPtr mimeStr = Utf8(mime);
        Marshal.WriteIntPtr(args, 0, mimeStr);
        Marshal.WriteInt32(args, IntPtr.Size, fds[1]);
        wl_proxy_marshal_array_flags(offer, 0, IntPtr.Zero, wl_proxy_get_version(offer), 0, args);
        Marshal.FreeHGlobal(args);
        Marshal.FreeHGlobal(mimeStr);

        // Flush BEFORE closing our write end, so the request is on the socket.
        // Then close it: while this process holds the write end open, the read
        // below never sees EOF and the capture hangs for as long as the daemon
        // lives. libwayland has sent its own copy of the descriptor by now.
        wl_display_flush(display);
        close(fds[1]);

        var sb = new StringBuilder();
        var buf = new byte[16 * 1024];
        long deadline = Environment.TickCount64 + timeoutMs;

        // Bounded, unlike the spike. A selection transfer is a round trip through
        // another application's event loop and a wedged owner can simply never
        // write — the X11 source caps the same wait at 300 ms for the same reason.
        fixed (byte* p = buf)
        {
            while (true)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) break;

                PollFd pfd = new() { Fd = fds[0], Events = POLLIN };
                int ready = poll(&pfd, 1, remaining);
                if (ready <= 0) break;                       // timeout or error

                nint n = read(fds[0], p, (nuint)buf.Length);
                if (n <= 0) break;                           // EOF, or a failure
                sb.Append(Encoding.UTF8.GetString(buf, 0, (int)n));
            }
        }

        close(fds[0]);
        return sb.ToString();
    }

    // ------------------------------------------------- descriptor construction

    private static IntPtr Utf8(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
        Marshal.Copy(b, 0, p, b.Length);
        Marshal.WriteByte(p, b.Length, 0);
        return p;
    }

    private static IntPtr Zeroed(int bytes)
    {
        IntPtr p = Marshal.AllocHGlobal(bytes);
        for (int i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }

    private static IntPtr TypeList(params IntPtr[] types)
    {
        IntPtr p = Zeroed(IntPtr.Size * Math.Max(types.Length, 1));
        for (int i = 0; i < types.Length; i++) Marshal.WriteIntPtr(p, i * IntPtr.Size, types[i]);
        return p;
    }

    private static IntPtr Messages(params (string Name, string Sig, IntPtr Types)[] msgs)
    {
        IntPtr block = Zeroed(MessageSize * Math.Max(msgs.Length, 1));
        for (int i = 0; i < msgs.Length; i++)
        {
            IntPtr m = block + i * MessageSize;
            Marshal.WriteIntPtr(m, 0, Utf8(msgs[i].Name));
            Marshal.WriteIntPtr(m, 8, Utf8(msgs[i].Sig));
            Marshal.WriteIntPtr(m, 16, msgs[i].Types);
        }
        return block;
    }

    private static void Build()
    {
        if (_built) return;

        IntPtr lib = NativeLibrary.Load(WL);
        // Exported DATA symbols. Referenced rather than rebuilt: a hand-built
        // wl_seat that disagreed with libwayland's own would corrupt every seat
        // message, and there is no reason to have two definitions of it.
        _wlRegistryIface = NativeLibrary.GetExport(lib, "wl_registry_interface");
        _wlSeatIface = NativeLibrary.GetExport(lib, "wl_seat_interface");

        // Allocated before the message tables that point at them — the device and
        // offer interfaces reference each other (get_data_device, data_offer), so
        // both addresses must exist before either table is filled in.
        _ifaceManager = Zeroed(InterfaceSize);
        _ifaceDevice = Zeroed(InterfaceSize);
        _ifaceOffer = Zeroed(InterfaceSize);

        Fill(_ifaceManager, "ext_data_control_manager_v1", 1,
            3, Messages(
                ("create_data_source", "n",  TypeList(IntPtr.Zero)),
                ("get_data_device",    "no", TypeList(_ifaceDevice, _wlSeatIface)),
                ("destroy",            "",   TypeList())),
            0, IntPtr.Zero);

        Fill(_ifaceDevice, "ext_data_control_device_v1", 1,
            3, Messages(
                ("set_selection",         "?o", TypeList(IntPtr.Zero)),
                ("destroy",               "",   TypeList()),
                ("set_primary_selection", "?o", TypeList(IntPtr.Zero))),
            4, Messages(
                ("data_offer",        "n",  TypeList(_ifaceOffer)),
                ("selection",         "?o", TypeList(_ifaceOffer)),
                ("finished",          "",   TypeList()),
                ("primary_selection", "?o", TypeList(_ifaceOffer))));

        Fill(_ifaceOffer, "ext_data_control_offer_v1", 1,
            2, Messages(
                ("receive", "sh", TypeList(IntPtr.Zero, IntPtr.Zero)),
                ("destroy", "",   TypeList())),
            1, Messages(
                ("offer", "s", TypeList(IntPtr.Zero))));

        _registryListener = Zeroed(IntPtr.Size * 2);
        Marshal.WriteIntPtr(_registryListener, 0,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, uint, void>)&OnGlobal);
        Marshal.WriteIntPtr(_registryListener, IntPtr.Size,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnGlobalRemove);

        _deviceListener = Zeroed(IntPtr.Size * 4);
        Marshal.WriteIntPtr(_deviceListener, 0,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDataOffer);
        Marshal.WriteIntPtr(_deviceListener, IntPtr.Size,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSelection);
        Marshal.WriteIntPtr(_deviceListener, IntPtr.Size * 2,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnFinished);
        Marshal.WriteIntPtr(_deviceListener, IntPtr.Size * 3,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnPrimarySelection);

        _offerListener = Zeroed(IntPtr.Size);
        Marshal.WriteIntPtr(_offerListener, 0,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnOfferMime);

        _built = true;

        static void Fill(IntPtr i, string name, int version, int mc, IntPtr m, int ec, IntPtr e)
        {
            Marshal.WriteIntPtr(i, 0, Utf8(name));
            Marshal.WriteInt32(i, 8, version);
            Marshal.WriteInt32(i, 12, mc);
            Marshal.WriteIntPtr(i, 16, m);
            Marshal.WriteInt32(i, 24, ec);
            Marshal.WriteIntPtr(i, 32, e);
        }
    }

    // ---------------------------------------------------------- event handlers

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGlobal(IntPtr data, IntPtr registry, uint name, IntPtr iface, uint version)
    {
        string? s = Marshal.PtrToStringUTF8(iface);
        if (s == "ext_data_control_manager_v1") { _managerName = name; _managerVersion = version; _sawManager = true; }
        else if (s == "wl_seat" && !_sawSeat)   { _seatName = name;    _seatVersion = version;    _sawSeat = true; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGlobalRemove(IntPtr data, IntPtr registry, uint name) { }

    // The MIME types of an offer arrive as a burst on the new offer proxy BEFORE
    // the event that says which offer is current, so the listener goes on the
    // moment the proxy exists and the types accumulate against its address.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDataOffer(IntPtr data, IntPtr device, IntPtr offer)
    {
        OfferMimes[offer] = new List<string>();
        wl_proxy_add_listener(offer, _offerListener, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnOfferMime(IntPtr data, IntPtr offer, IntPtr mime)
    {
        string? s = Marshal.PtrToStringUTF8(mime);
        if (s is not null && OfferMimes.TryGetValue(offer, out var list)) list.Add(s);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSelection(IntPtr data, IntPtr device, IntPtr offer) => _clipboardOffer = offer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPrimarySelection(IntPtr data, IntPtr device, IntPtr offer) => _primaryOffer = offer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFinished(IntPtr data, IntPtr device) => _finished = true;
}
