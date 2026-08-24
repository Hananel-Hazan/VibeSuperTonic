using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// wlsel — read the Wayland PRIMARY selection via ext-data-control-v1.
//
//     dotnet run --project spike/wayland-selection/WlSel.csproj -c Release
//     dotnet run --project spike/wayland-selection/WlSel.csproj -c Release -- --clipboard
//     dotnet run --project spike/wayland-selection/WlSel.csproj -c Release -- --list
//
// Prints the selection to stdout and everything else to stderr, the same split
// `status` and `benchmark` already use, so `wlsel | cat` is the text alone.
//
// ---------------------------------------------------------------------------
// WHY THIS PROTOCOL, AND NOT THE OBVIOUS ONES
//
// zwp_primary_selection_device_manager_v1 is the protocol a normal application
// uses, and it only delivers a selection to the client with KEYBOARD FOCUS. A
// background daemon never has focus, so it would always read nothing. That is
// what wl-clipboard 2.2.1 works around by spawning an invisible surface and
// taking focus with xdg_activation — measured on this machine, and not
// something to imitate: it would steal focus from the user's application on
// every hotkey press.
//
// ext-data-control-v1 exists precisely for privileged, unfocused clients —
// clipboard managers and the like. KWin advertises it (checked: global 15 on
// Plasma 6.6.6). It is the successor to wlr-data-control-unstable-v1, which
// KWin dropped; wl-clipboard 2.2.1 predates the rename, which is the whole
// reason it falls back to the focus hack rather than this.
//
// ---------------------------------------------------------------------------
// WHY THE INTEROP LOOKS LIKE THIS
//
// libwayland has no stable C ABI for a protocol it does not itself define.
// Normally wayland-scanner reads the XML and GENERATES the wl_interface and
// wl_message descriptor tables as C source. There is no scanner here, so the
// tables are built by hand in unmanaged memory below, from the protocol
// definition (opcodes and signatures transcribed from the published XML, not
// guessed — see PROTOCOL below).
//
// Two consequences worth knowing before editing:
//
//   1. A wrong offset or a wrong signature string is a SEGFAULT inside
//      libwayland, not a managed exception. There is no stack trace.
//   2. Every descriptor must outlive every proxy that references it, so they
//      are allocated once and never freed. This process is short-lived; the
//      daemon port must allocate them once per process, not per capture.
// ---------------------------------------------------------------------------

internal static unsafe class Program
{
    // ----------------------------------------------------------- PROTOCOL
    //
    // ext-data-control-v1, transcribed from the protocol XML. Order IS the
    // opcode — the wire format carries an index, never a name, so reordering
    // any of these arrays silently calls a different request.
    //
    // Signature letters: n=new_id  o=object  s=string  u=uint  h=fd  ?=nullable
    //
    //   ext_data_control_manager_v1   requests: 0 create_data_source "n"
    //                                           1 get_data_device    "no"
    //                                           2 destroy            ""
    //   ext_data_control_device_v1    requests: 0 set_selection         "?o"
    //                                           1 destroy               ""
    //                                           2 set_primary_selection "?o"
    //                                 events:   0 data_offer        "n"
    //                                           1 selection         "?o"
    //                                           2 finished          ""
    //                                           3 primary_selection "?o"
    //   ext_data_control_offer_v1     requests: 0 receive "sh"
    //                                           1 destroy ""
    //                                 events:   0 offer   "s"

    private const string WL = "libwayland-client.so.0";

    [DllImport(WL)] private static extern IntPtr wl_display_connect(string? name);
    [DllImport(WL)] private static extern void   wl_display_disconnect(IntPtr display);
    [DllImport(WL)] private static extern int    wl_display_roundtrip(IntPtr display);
    [DllImport(WL)] private static extern int    wl_display_flush(IntPtr display);
    [DllImport(WL)] private static extern uint   wl_proxy_get_version(IntPtr proxy);
    [DllImport(WL)] private static extern void   wl_proxy_destroy(IntPtr proxy);
    [DllImport(WL)] private static extern int    wl_proxy_add_listener(IntPtr proxy, IntPtr impl, IntPtr data);
    [DllImport(WL)] private static extern IntPtr wl_proxy_marshal_array_flags(
        IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr args);

    [DllImport("libc", SetLastError = true)] private static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    // wl_display opcode 1 is get_registry. wl_registry opcode 0 is bind.
    private const uint WlDisplayGetRegistry = 1;
    private const uint WlRegistryBind = 0;

    // --------------------------------------------------- descriptor building
    //
    // struct wl_message { const char *name; const char *signature;
    //                     const struct wl_interface **types; }   -> 24 bytes
    // struct wl_interface { const char *name; int version; int method_count;
    //                       const struct wl_message *methods;
    //                       int event_count; const struct wl_message *events; }
    // Field offsets, x86-64: name 0, version 8, method_count 12, methods 16,
    // event_count 24, (pad), events 32.                          -> 40 bytes
    private const int MessageSize = 24;
    private const int InterfaceSize = 40;

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

    /// <summary>An array of wl_interface* for one message's arguments.</summary>
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

    private static IntPtr Interface(string name, int version,
                                    int methodCount, IntPtr methods,
                                    int eventCount, IntPtr events)
    {
        IntPtr i = Zeroed(InterfaceSize);
        Marshal.WriteIntPtr(i, 0, Utf8(name));
        Marshal.WriteInt32(i, 8, version);
        Marshal.WriteInt32(i, 12, methodCount);
        Marshal.WriteIntPtr(i, 16, methods);
        Marshal.WriteInt32(i, 24, eventCount);
        Marshal.WriteIntPtr(i, 32, events);
        return i;
    }

    // Built once in Main. Never freed — see note 2 at the top.
    private static IntPtr _ifaceManager, _ifaceDevice, _ifaceOffer;
    private static IntPtr _wlRegistryIface, _wlSeatIface;

    private static void BuildInterfaces()
    {
        IntPtr lib = NativeLibrary.Load(WL);
        // libwayland exports these as DATA symbols, so they can be referenced
        // rather than rebuilt — which matters, because a hand-built wl_seat that
        // disagreed with libwayland's would corrupt every seat message.
        _wlRegistryIface = NativeLibrary.GetExport(lib, "wl_registry_interface");
        _wlSeatIface     = NativeLibrary.GetExport(lib, "wl_seat_interface");

        // Allocated before the message tables that point at them: the device and
        // offer interfaces reference each other through get_data_device and
        // data_offer, so the pointers must exist before either table is filled.
        _ifaceManager = Zeroed(InterfaceSize);
        _ifaceDevice  = Zeroed(InterfaceSize);
        _ifaceOffer   = Zeroed(InterfaceSize);

        IntPtr mgrMethods = Messages(
            ("create_data_source", "n",  TypeList(IntPtr.Zero)),
            ("get_data_device",    "no", TypeList(_ifaceDevice, _wlSeatIface)),
            ("destroy",            "",   TypeList()));
        Fill(_ifaceManager, "ext_data_control_manager_v1", 1, 3, mgrMethods, 0, IntPtr.Zero);

        IntPtr devMethods = Messages(
            ("set_selection",         "?o", TypeList(IntPtr.Zero)),
            ("destroy",               "",   TypeList()),
            ("set_primary_selection", "?o", TypeList(IntPtr.Zero)));
        IntPtr devEvents = Messages(
            ("data_offer",        "n",  TypeList(_ifaceOffer)),
            ("selection",         "?o", TypeList(_ifaceOffer)),
            ("finished",          "",   TypeList()),
            ("primary_selection", "?o", TypeList(_ifaceOffer)));
        Fill(_ifaceDevice, "ext_data_control_device_v1", 1, 3, devMethods, 4, devEvents);

        IntPtr offMethods = Messages(
            ("receive", "sh", TypeList(IntPtr.Zero, IntPtr.Zero)),
            ("destroy", "",   TypeList()));
        IntPtr offEvents = Messages(
            ("offer", "s", TypeList(IntPtr.Zero)));
        Fill(_ifaceOffer, "ext_data_control_offer_v1", 1, 2, offMethods, 1, offEvents);

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

    // ------------------------------------------------------------ listeners
    //
    // Static because [UnmanagedCallersOnly] cannot capture, so the callbacks and
    // the code that reads their results communicate through these fields. Fine
    // for a spike and for the daemon too, as long as one capture runs at a time
    // — which the daemon guarantees, since capture is synchronous by design.

    private static uint _managerName, _managerVersion, _seatName, _seatVersion;
    private static bool _sawManager, _sawSeat;
    private static IntPtr _primaryOffer, _clipboardOffer;
    private static readonly Dictionary<IntPtr, List<string>> _offerMimes = new();
    private static bool _finished;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGlobal(IntPtr data, IntPtr registry, uint name, IntPtr iface, uint version)
    {
        string? s = Marshal.PtrToStringUTF8(iface);
        if (s == "ext_data_control_manager_v1") { _managerName = name; _managerVersion = version; _sawManager = true; }
        else if (s == "wl_seat" && !_sawSeat)   { _seatName = name;    _seatVersion = version;    _sawSeat = true; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnGlobalRemove(IntPtr data, IntPtr registry, uint name) { }

    // A data_offer event hands us a brand-new offer proxy. Its MIME types arrive
    // as a burst of `offer` events on that proxy BEFORE the selection event that
    // says which offer is current — so a listener goes on immediately and the
    // types are accumulated against the proxy pointer.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDataOffer(IntPtr data, IntPtr device, IntPtr offer)
    {
        _offerMimes[offer] = new List<string>();
        wl_proxy_add_listener(offer, _offerListener, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnOfferMime(IntPtr data, IntPtr offer, IntPtr mime)
    {
        string? s = Marshal.PtrToStringUTF8(mime);
        if (s is not null && _offerMimes.TryGetValue(offer, out var list)) list.Add(s);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSelection(IntPtr data, IntPtr device, IntPtr offer) => _clipboardOffer = offer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPrimarySelection(IntPtr data, IntPtr device, IntPtr offer) => _primaryOffer = offer;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFinished(IntPtr data, IntPtr device) => _finished = true;

    private static IntPtr _registryListener, _deviceListener, _offerListener;

    private static void BuildListeners()
    {
        // A listener is a plain array of function pointers in event order.
        _registryListener = Zeroed(IntPtr.Size * 2);
        Marshal.WriteIntPtr(_registryListener, 0, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, uint, void>)&OnGlobal);
        Marshal.WriteIntPtr(_registryListener, IntPtr.Size, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OnGlobalRemove);

        _deviceListener = Zeroed(IntPtr.Size * 4);
        Marshal.WriteIntPtr(_deviceListener, 0,               (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnDataOffer);
        Marshal.WriteIntPtr(_deviceListener, IntPtr.Size,     (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnSelection);
        Marshal.WriteIntPtr(_deviceListener, IntPtr.Size * 2, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnFinished);
        Marshal.WriteIntPtr(_deviceListener, IntPtr.Size * 3, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnPrimarySelection);

        _offerListener = Zeroed(IntPtr.Size);
        Marshal.WriteIntPtr(_offerListener, 0, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnOfferMime);
    }

    // --------------------------------------------------------------- main
    private static int Main(string[] args)
    {
        bool clipboard = args.Contains("--clipboard");
        bool list = args.Contains("--list");

        IntPtr display = wl_display_connect(null);
        if (display == IntPtr.Zero)
        {
            Console.Error.WriteLine("cannot connect to a Wayland display ($WAYLAND_DISPLAY).");
            return 1;
        }

        BuildInterfaces();
        BuildListeners();

        // registry = wl_display.get_registry()
        IntPtr regArgs = Zeroed(8);
        IntPtr registry = wl_proxy_marshal_array_flags(
            display, WlDisplayGetRegistry, _wlRegistryIface, wl_proxy_get_version(display), 0, regArgs);
        wl_proxy_add_listener(registry, _registryListener, IntPtr.Zero);
        wl_display_roundtrip(display);

        if (!_sawManager)
        {
            Console.Error.WriteLine(
                "this compositor does not advertise ext_data_control_manager_v1.\n" +
                "Selections of native Wayland clients cannot be read without it.");
            return 2;
        }
        if (!_sawSeat) { Console.Error.WriteLine("no wl_seat advertised."); return 2; }

        Console.Error.WriteLine($"ext_data_control_manager_v1 v{_managerVersion}, wl_seat v{_seatVersion}");

        IntPtr manager = Bind(registry, _managerName, _ifaceManager, "ext_data_control_manager_v1", Math.Min(_managerVersion, 1));
        IntPtr seat    = Bind(registry, _seatName,    _wlSeatIface,  "wl_seat",                     Math.Min(_seatVersion, 1));

        // device = manager.get_data_device(seat)   -- opcode 1, "no"
        IntPtr devArgs = Zeroed(16);
        Marshal.WriteIntPtr(devArgs, 0, IntPtr.Zero);   // new_id placeholder
        Marshal.WriteIntPtr(devArgs, 8, seat);
        IntPtr device = wl_proxy_marshal_array_flags(manager, 1, _ifaceDevice, wl_proxy_get_version(manager), 0, devArgs);
        if (device == IntPtr.Zero) { Console.Error.WriteLine("get_data_device returned null"); return 3; }
        wl_proxy_add_listener(device, _deviceListener, IntPtr.Zero);

        // Two roundtrips: the first delivers data_offer plus its MIME burst, the
        // second guarantees the selection/primary_selection events that name the
        // current offer have arrived. One is usually enough and "usually" is not
        // a property worth shipping.
        wl_display_roundtrip(display);
        wl_display_roundtrip(display);

        if (_finished) { Console.Error.WriteLine("compositor sent `finished` — another data-control client took over."); return 4; }

        IntPtr chosen = clipboard ? _clipboardOffer : _primaryOffer;
        string which = clipboard ? "clipboard" : "primary";

        if (list)
        {
            Console.Error.WriteLine($"offers seen: {_offerMimes.Count}");
            foreach (var (ptr, mimes) in _offerMimes)
            {
                string tag = ptr == _primaryOffer ? " [PRIMARY]" : ptr == _clipboardOffer ? " [CLIPBOARD]" : "";
                Console.Error.WriteLine($"  offer 0x{ptr:x}{tag}: {string.Join(", ", mimes)}");
            }
        }

        if (chosen == IntPtr.Zero)
        {
            Console.Error.WriteLine($"nothing owns the {which} selection.");
            return 5;
        }

        var available = _offerMimes.TryGetValue(chosen, out var got) ? got : new List<string>();
        string? mime = PickTextMime(available);
        if (mime is null)
        {
            Console.Error.WriteLine($"the {which} selection offers no text type. Offered: {string.Join(", ", available)}");
            return 6;
        }
        Console.Error.WriteLine($"reading {which} as {mime}");

        string text = Receive(display, chosen, mime);
        Console.Out.Write(text);
        Console.Error.WriteLine($"\n({text.Length} characters)");

        wl_display_disconnect(display);
        return 0;
    }

    private static IntPtr Bind(IntPtr registry, uint name, IntPtr iface, string ifaceName, uint version)
    {
        // wl_registry.bind is the one request whose new_id carries an explicit
        // interface name and version on the wire: signature "usun".
        IntPtr args = Zeroed(32);
        Marshal.WriteInt32(args, 0, (int)name);
        Marshal.WriteIntPtr(args, 8, Utf8(ifaceName));
        Marshal.WriteInt32(args, 16, (int)version);
        Marshal.WriteIntPtr(args, 24, IntPtr.Zero);
        return wl_proxy_marshal_array_flags(registry, WlRegistryBind, iface, version, 0, args);
    }

    /// <summary>
    /// Preference order, most specific first. text/plain;charset=utf-8 is the
    /// only one that states its encoding; the X11-era names (UTF8_STRING,
    /// STRING, TEXT) are still offered by toolkits bridging from Xwayland and
    /// are worth accepting rather than failing in front of.
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

    private static string Receive(IntPtr display, IntPtr offer, string mime)
    {
        int* fds = stackalloc int[2];
        if (pipe(fds) != 0) throw new IOException("pipe() failed");

        // offer.receive(mime, fd) -- opcode 0, "sh". The compositor writes the
        // selection into our pipe; libwayland sends the descriptor over the
        // socket with SCM_RIGHTS and closes its copy.
        IntPtr args = Zeroed(16);
        Marshal.WriteIntPtr(args, 0, Utf8(mime));
        Marshal.WriteInt32(args, 8, fds[1]);
        wl_proxy_marshal_array_flags(offer, 0, IntPtr.Zero, wl_proxy_get_version(offer), 0, args);

        // Flush BEFORE closing our write end, so the request is actually on the
        // socket. Then close it: while this process holds the write end open the
        // read below never sees EOF, and the capture hangs forever.
        wl_display_flush(display);
        close(fds[1]);

        using var stream = new FileStream(new SafeFileHandle((IntPtr)fds[0], ownsHandle: true), FileAccess.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
