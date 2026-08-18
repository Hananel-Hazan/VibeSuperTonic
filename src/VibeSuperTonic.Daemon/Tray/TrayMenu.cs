using Tmds.DBus.Protocol;

namespace VibeSuperTonic.Daemon.Tray;

/// <summary>One row of the tray menu.</summary>
/// <param name="Id">Stable across revisions — the host sends it back in <c>Event</c>.</param>
/// <param name="Label">Evaluated on every layout request, because two of the four change with state.</param>
/// <param name="Click">What the row does. Every one of these is a verb — see <see cref="TrayIcon"/>.</param>
internal sealed record MenuRow(int Id, Func<string> Label, Action Click, bool IsSeparator = false);

/// <summary>
/// The tray menu, over <c>com.canonical.dbusmenu</c>.
///
/// <para><b>This is the price of rung 1.</b> StatusNotifierItem does not carry a
/// menu; it carries an object path where one is expected to be, and what has to
/// be at that path is DBusMenu — a protocol with a recursive layout structure,
/// its own revision counter, and a property model. Rung 2
/// (<c>libayatana-appindicator3</c>) would have supplied all of it, at the cost
/// of GTK inside a daemon that has to run without a display, which is nearly the
/// objection rung 1 exists to avoid.</para>
///
/// <para>Implemented to the depth this menu needs and no further: a flat list
/// under a root, no submenus, no icons, no radio or check states. Anything
/// deeper is speculative work for a menu with four rows in it.</para>
/// </summary>
internal sealed class TrayMenu : IMethodHandler
{
    public const string MenuPath = "/MenuBar";
    private const string Interface = "com.canonical.dbusmenu";

    private readonly IReadOnlyList<MenuRow> _rows;
    private uint _revision = 1;

    private readonly Action<string> _log;

    public TrayMenu(IReadOnlyList<MenuRow> rows, Action<string> log)
    {
        _rows = rows;
        _log = log;
    }

    public string Path => MenuPath;

    /// <summary>
    /// Bumped whenever a label changes, and sent with <c>LayoutUpdated</c> so the
    /// host re-reads. A menu that is only correct when it happens to be rebuilt
    /// would show "Stop" over an idle daemon.
    /// </summary>
    public uint Revision => _revision;

    public uint NextRevision() => ++_revision;

    // Menu work is a dictionary lookup and a string; a thread hop would cost
    // more than the work.
    public bool RunMethodHandlerSynchronously(Message message) => true;

    /// <inheritdoc cref="StatusNotifierItem.HandleMethodAsync"/>
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try { return Dispatch(context); }
        catch (Exception ex)
        {
            _log($"tray menu: {context.Request.MemberAsString} failed: {ex}");
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
                ReplyProperties(context);
                break;

            case ("org.freedesktop.DBus.Properties", "Get"):
                ReplyProperty(context);
                break;

            case (Interface, "GetLayout"):
                ReplyLayout(context);
                break;

            case (Interface, "GetGroupProperties"):
                ReplyGroupProperties(context);
                break;

            case (Interface, "AboutToShow"):
            {
                // "false" means the layout the host already has is still good.
                // Ours changes on state, not on opening, and the LayoutUpdated
                // signal is what says so.
                using var writer = context.CreateReplyWriter("b");
                writer.WriteBool(false);
                context.Reply(writer.CreateMessage());
                break;
            }

            case (Interface, "Event"):
            {
                var reader = request.GetBodyReader();
                int id = reader.ReadInt32();
                string eventId = reader.ReadString();

                if (eventId == "clicked")
                    _rows.FirstOrDefault(r => r.Id == id)?.Click();

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

    private void ReplyProperties(MethodContext context)
    {
        using var writer = context.CreateReplyWriter("a{sv}");
        writer.WriteDictionary(Properties());
        context.Reply(writer.CreateMessage());
    }

    private void ReplyProperty(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        reader.ReadString();                              // interface
        string name = reader.ReadString();

        if (!Properties().TryGetValue(name, out var value))
        {
            context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
            return;
        }

        using var writer = context.CreateReplyWriter("v");
        writer.WriteVariant(value);
        context.Reply(writer.CreateMessage());
    }

    private static Dictionary<string, Variant> Properties() => new()
    {
        ["Version"] = (uint)3,
        ["TextDirection"] = "ltr",
        ["Status"] = "normal",
    };

    /// <summary>
    /// <c>GetLayout</c> → <c>u(ia{sv}av)</c>: a revision, then a node of
    /// (id, properties, children-as-variants). The recursion in the signature is
    /// why this is written by hand rather than described by a type.
    /// </summary>
    private void ReplyLayout(MethodContext context)
    {
        using var writer = context.CreateReplyWriter("u(ia{sv}av)");
        writer.WriteUInt32(_revision);

        writer.WriteStructureStart();
        writer.WriteInt32(0);
        writer.WriteDictionary(new Dictionary<string, Variant> { ["children-display"] = "submenu" });

        var children = writer.WriteArrayStart(DBusType.Variant);
        foreach (var row in _rows)
            writer.WriteVariant(Node(row));
        writer.WriteArrayEnd(children);

        context.Reply(writer.CreateMessage());
    }

    private void ReplyGroupProperties(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        int[] ids = reader.ReadArrayOfInt32();

        using var writer = context.CreateReplyWriter("a(ia{sv})");
        var array = writer.WriteArrayStart(DBusType.Struct);

        // An empty id list means "all of them", which is what the specification
        // says and what most hosts send.
        foreach (var row in _rows.Where(r => ids.Length == 0 || ids.Contains(r.Id)))
        {
            writer.WriteStructureStart();
            writer.WriteInt32(row.Id);
            writer.WriteDictionary(RowProperties(row));
        }

        writer.WriteArrayEnd(array);
        context.Reply(writer.CreateMessage());
    }

    private static Variant Node(MenuRow row) =>
        Variant.FromStruct(new Struct<int, Dict<string, Variant>, Array<Variant>>(
            row.Id,
            new Dict<string, Variant>(RowProperties(row)),
            new Array<Variant>()));

    private static Dictionary<string, Variant> RowProperties(MenuRow row) => row.IsSeparator
        ? new Dictionary<string, Variant> { ["type"] = "separator" }
        : new Dictionary<string, Variant>
        {
            ["label"] = row.Label(),
            ["enabled"] = true,
            ["visible"] = true,
        };

    private static readonly ReadOnlyMemory<byte> IntrospectXml = System.Text.Encoding.UTF8.GetBytes(
        """
          <interface name="com.canonical.dbusmenu">
            <property name="Version" type="u" access="read"/>
            <property name="TextDirection" type="s" access="read"/>
            <property name="Status" type="s" access="read"/>
            <method name="GetLayout">
              <arg type="i" direction="in"/>
              <arg type="i" direction="in"/>
              <arg type="as" direction="in"/>
              <arg type="u" direction="out"/>
              <arg type="(ia{sv}av)" direction="out"/>
            </method>
            <method name="GetGroupProperties">
              <arg type="ai" direction="in"/>
              <arg type="as" direction="in"/>
              <arg type="a(ia{sv})" direction="out"/>
            </method>
            <method name="Event">
              <arg type="i" direction="in"/>
              <arg type="s" direction="in"/>
              <arg type="v" direction="in"/>
              <arg type="u" direction="in"/>
            </method>
            <method name="AboutToShow">
              <arg type="i" direction="in"/>
              <arg type="b" direction="out"/>
            </method>
            <signal name="LayoutUpdated">
              <arg type="u"/>
              <arg type="i"/>
            </signal>
          </interface>
        """);
}
