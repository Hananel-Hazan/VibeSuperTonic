using Tmds.DBus.Protocol;

namespace VibeSuperTonic.Daemon.Tray;

/// <summary>
/// The icon itself, over <c>org.kde.StatusNotifierItem</c>.
///
/// <para><b>SNI is pure D-Bus, not X11</b>, which is the fact that makes rung 1
/// possible at all: a daemon with no <c>$DISPLAY</c> can legitimately own a tray
/// icon. What it cannot do without is <c>DBUS_SESSION_BUS_ADDRESS</c> — the new
/// <c>$DISPLAY</c>, and inherited the same way, which is why the daemon must
/// never refuse to start over it. See <see cref="TrayIcon"/>.</para>
///
/// <para>Everything the host reads is a property, and the properties are pulled
/// from a delegate rather than stored here, so there is exactly one copy of the
/// state and no way for the icon to disagree with the daemon about it.</para>
/// </summary>
internal sealed class StatusNotifierItem : IMethodHandler
{
    public const string ItemPath = "/StatusNotifierItem";
    private const string Interface = "org.kde.StatusNotifierItem";

    private readonly Func<TrayView> _view;
    private readonly Action _activate;
    private readonly Action _secondaryActivate;

    private readonly Action<string> _log;

    public StatusNotifierItem(Func<TrayView> view, Action activate, Action secondaryActivate, Action<string> log)
    {
        _view = view;
        _activate = activate;
        _secondaryActivate = secondaryActivate;
        _log = log;
    }

    public string Path => ItemPath;

    public bool RunMethodHandlerSynchronously(Message message) => true;

    /// <summary>
    /// <para><b>Nothing may escape this method.</b> The handler runs on the
    /// connection's own reader thread, so an exception here does not fail one
    /// call — it ends the read loop and the bus drops the connection, which
    /// presents as an icon that appeared for 15 ms and then was never there,
    /// with nothing logged anywhere. Found exactly that way.</para>
    /// </summary>
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try { return Dispatch(context); }
        catch (Exception ex)
        {
            _log($"tray: {context.Request.MemberAsString} failed: {ex}");
            if (!context.ReplySent) context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
            return ValueTask.CompletedTask;
        }
    }

    private ValueTask Dispatch(MethodContext context)
    {
        var request = context.Request;

        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([IntrospectXml]);
            return ValueTask.CompletedTask;
        }

        switch (request.InterfaceAsString, request.MemberAsString)
        {
            case ("org.freedesktop.DBus.Properties", "GetAll"):
            {
                var v = _view();
                // Not `using var`: a using variable cannot be passed by ref, and
                // by ref is the whole point — see WriteProperties.
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    WriteProperties(ref writer, v);
                    context.Reply(writer.CreateMessage());
                }
                finally { writer.Dispose(); }
                break;
            }

            case ("org.freedesktop.DBus.Properties", "Get"):
                ReplyOneProperty(context);
                break;

            // Left click. Opens the Reader, not Status: this is a reader that
            // has settings, not a control panel that shows text.
            case (Interface, "Activate"):
            {
                _activate();
                using var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                break;
            }

            // Middle click. The one gesture a panel gives you for free, so it
            // gets the verb the hotkey has.
            case (Interface, "SecondaryActivate"):
            {
                _secondaryActivate();
                using var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                break;
            }

            case (Interface, "ContextMenu") or (Interface, "Scroll"):
            {
                // The menu lives at the Menu property's path and the host opens
                // it itself; there is nothing for us to do but answer.
                using var writer = context.CreateReplyWriter("");
                context.Reply(writer.CreateMessage());
                break;
            }

            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod",
                    $"{request.InterfaceAsString}.{request.MemberAsString}");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void ReplyOneProperty(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        reader.ReadString();                                  // interface
        string name = reader.ReadString();
        var view = _view();

        using var writer = context.CreateReplyWriter("v");

        switch (name)
        {
            case "Category": writer.WriteVariant((Variant)"ApplicationStatus"); break;
            case "Id": writer.WriteVariant((Variant)"vibesupertonic"); break;
            case "Title": writer.WriteVariant((Variant)"VibeSuperTonic"); break;
            case "Status": writer.WriteVariant((Variant)"Active"); break;
            case "IconName": writer.WriteVariant((Variant)""); break;
            case "ItemIsMenu": writer.WriteVariant((Variant)false); break;
            case "Menu": writer.WriteVariant((Variant)new ObjectPath(TrayMenu.MenuPath)); break;

            case "IconPixmap":
                writer.WriteVariant(PixmapVariant(view));
                break;

            case "ToolTip":
                writer.WriteVariant(ToolTipVariant(view));
                break;

            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                return;
        }

        context.Reply(writer.CreateMessage());
    }

    /// <summary>
    /// <para><b><c>ref</c> is load-bearing.</b> <c>MessageWriter</c> is a mutable
    /// ref struct: passed by value, every byte written here lands in a copy, and
    /// the caller sends a message whose header declares <c>a{sv}</c> over a body
    /// of length zero. The bus does not answer that with an error — it
    /// disconnects the sender, which presents as a tray icon that registers
    /// successfully and vanishes 15 ms later with nothing logged. It took a
    /// strace of the outgoing bytes to see, because every layer above the socket
    /// reported success.</para>
    /// </summary>
    private static void WriteProperties(ref MessageWriter writer, TrayView view)
    {
        var all = new Dictionary<string, Variant>
        {
            ["Category"] = "ApplicationStatus",
            ["Id"] = "vibesupertonic",
            ["Title"] = "VibeSuperTonic",
            ["Status"] = "Active",

            // Empty on purpose: the pixmap is the icon, because a themed name
            // needs an installed theme and this product is a folder you copy.
            ["IconName"] = "",
            ["IconPixmap"] = PixmapVariant(view),
            ["ToolTip"] = ToolTipVariant(view),

            // False, so a left click reaches Activate instead of opening the
            // menu. The menu is still one right click away, and the commonest
            // gesture should do the commonest thing.
            ["ItemIsMenu"] = false,
            ["Menu"] = new ObjectPath(TrayMenu.MenuPath),
        };

        writer.WriteDictionary(all);
    }

    /// <summary><c>a(iiay)</c> — width, height, ARGB32 bytes, once per size.</summary>
    private static Variant PixmapVariant(TrayView view)
    {
        var images = new Array<Struct<int, int, Array<byte>>>();
        foreach (var (width, height, argb) in TrayPixmap.For(view.State, view.UiAttached))
            images.Add(new Struct<int, int, Array<byte>>(width, height, new Array<byte>(argb)));

        return images.AsVariant();
    }

    /// <summary><c>(sa(iiay)ss)</c> — icon name, icon pixmaps, title, description.</summary>
    private static Variant ToolTipVariant(TrayView view) =>
        Variant.FromStruct(new Struct<string, Array<Struct<int, int, Array<byte>>>, string, string>(
            "",
            new Array<Struct<int, int, Array<byte>>>(),
            "VibeSuperTonic",
            view.ToolTip));

    private static readonly ReadOnlyMemory<byte> IntrospectXml = System.Text.Encoding.UTF8.GetBytes(
        """
          <interface name="org.kde.StatusNotifierItem">
            <property name="Category" type="s" access="read"/>
            <property name="Id" type="s" access="read"/>
            <property name="Title" type="s" access="read"/>
            <property name="Status" type="s" access="read"/>
            <property name="IconName" type="s" access="read"/>
            <property name="IconPixmap" type="a(iiay)" access="read"/>
            <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
            <property name="ItemIsMenu" type="b" access="read"/>
            <property name="Menu" type="o" access="read"/>
            <method name="Activate">
              <arg type="i" direction="in"/>
              <arg type="i" direction="in"/>
            </method>
            <method name="SecondaryActivate">
              <arg type="i" direction="in"/>
              <arg type="i" direction="in"/>
            </method>
            <method name="ContextMenu">
              <arg type="i" direction="in"/>
              <arg type="i" direction="in"/>
            </method>
            <signal name="NewIcon"/>
            <signal name="NewToolTip"/>
            <signal name="NewStatus">
              <arg type="s"/>
            </signal>
          </interface>
        """);
}

/// <summary>
/// Everything the icon shows, as one value. Passed as a snapshot rather than
/// read field by field so the icon and its tooltip can never describe two
/// different moments.
/// </summary>
internal readonly record struct TrayView(TrayState State, bool UiAttached, string ToolTip);
