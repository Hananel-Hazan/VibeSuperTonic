using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "VibeSuperTonic Control Panel";
        // Sized for the Monitor tab's worst-case layout: sessions grid (180) +
        // detail-metric grid (~170) + Currently-synthesizing pane (140) + Last
        // error pane (220) + tabs/chrome ≈ 880 px tall. Wider than before so the
        // 8-column sessions grid has room for the Process column to breathe.
        Width = 1240;
        Height = 920;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 780);
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MakeTabPage("Status",         new StatusTab()));
        tabs.TabPages.Add(MakeTabPage("Tune",           new TuneTab()));
        tabs.TabPages.Add(MakeTabPage("Pronunciations", new PronunciationsTab()));
        tabs.TabPages.Add(MakeTabPage("Export",         new ExportTab()));
        tabs.TabPages.Add(MakeTabPage("Benchmark",      new BenchmarkTab()));
        tabs.TabPages.Add(MakeTabPage("Monitor",        new MonitorTab()));
        tabs.TabPages.Add(MakeTabPage("Advanced",       new AdvancedTab()));
        tabs.TabPages.Add(MakeTabPage("About",          new AboutTab()));
        Controls.Add(tabs);

        Shown += (_, _) => WarnIfThirtyTwoBitBroken();
    }

    /// <summary>
    /// Say it out loud, once, when 32-bit SAPI clients cannot see the voices.
    ///
    /// Without the x86 runtime nothing is registered for 32-bit, so Balabolka and
    /// Lingoes list no VibeSuperTonic entries at all — while this window's own
    /// Test button speaks perfectly, because the Control Panel is 64-bit and
    /// self-contained. That combination reads as "the app works, the readers are
    /// broken", and it is the single most expensive way this product can fail: it
    /// sends the user to debug the wrong program.
    ///
    /// The Status tab has always held the evidence, but it was one truncated
    /// amber row among eleven green ones and nothing prompted anyone to read it.
    /// </summary>
    private void WarnIfThirtyTwoBitBroken()
    {
        Registration.State s;
        try { s = Registration.Inspect(); }
        catch { return; }   // never let a diagnostic stop the app from opening

        // Fires ONLY for the missing x86 runtime — the one cause the user cannot
        // discover and cannot fix from inside the app's normal flow.
        //
        // It used to fire whenever 32-bit was not working, which included the
        // ordinary state of a freshly extracted folder that has simply not been
        // registered yet. Worse, the text hardcoded "the runtime is not
        // installed", so a user who had just installed it was told it was
        // missing while the Status tab two inches away reported it present. A
        // diagnostic that contradicts the evidence on screen is worse than none.
        //
        // Everything else 32-bit — tokens absent, CLSID absent — is what
        // "Repair all" exists for and is already stated plainly on the Status
        // tab, so it does not need a modal.
        if (!s.X86ComHostExists) return;

        bool needsDotNet = !s.X86RuntimeInstalled;
        // The Visual C++ runtime fails differently and worse: registration
        // succeeds, the voices appear in the reader, the user selects one — and
        // nothing is ever heard, because ONNX Runtime's native DLL cannot load.
        // Windows reports that as "onnxruntime.dll or one of its dependencies",
        // naming the file that is present rather than the one that is not.
        bool needsVcRuntime = !VcRuntime.IsInstalled(DotNetRuntime.Arch.X86);
        if (!needsDotNet && !needsVcRuntime) return;
        if (WarningSuppressed()) return;

        string symptom = needsDotNet
            ? "32-bit programs cannot see the VibeSuperTonic voices."
            : "32-bit programs can see the VibeSuperTonic voices, but will play no sound.";

        var missing = new List<string>();
        if (needsDotNet) missing.Add($"    {DotNetRuntime.WingetCommand(DotNetRuntime.Arch.X86)}");
        if (needsVcRuntime) missing.Add($"    {VcRuntime.WingetCommand(DotNetRuntime.Arch.X86)}");

        var answer = MessageBox.Show(
            this,
            symptom + "\n\n" +
            "Most screen readers and TTS tools are still 32-bit — Balabolka, Lingoes, " +
            "and many NVDA setups — and they need 32-bit copies of two things this " +
            "machine is missing:\n\n" +
            (needsDotNet ? $"  • .NET {DotNetRuntime.RequiredMajor} runtime (x86) — without it, no voices are listed at all\n" : "") +
            (needsVcRuntime ? "  • Visual C++ runtime (x86) — without it, the voices are listed but silent\n" : "") +
            "\n" +
            "This window's Test buttons will keep working regardless, because the " +
            "Control Panel is 64-bit — so the voices working here is not a sign that " +
            "your reader will work.\n\n" +
            "VibeSuperTonic can install these for you using winget. This needs " +
            "administrator rights and installs them machine-wide:\n\n" +
            string.Join("\n", missing) + "\n\n" +
            "Install now?",
            "VibeSuperTonic — 32-bit clients cannot use the voices",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes) return;
        _ = InstallX86PrerequisitesAsync(needsDotNet, needsVcRuntime);
    }

    /// <summary>
    /// Runs the install and reports the outcome, then tells the user the one
    /// remaining step. Registration has to happen after the runtime lands —
    /// nothing is written for a bitness we cannot run.
    /// </summary>
    private async Task InstallX86PrerequisitesAsync(bool dotNet, bool vcRuntime)
    {
        var transcript = new List<string>();
        var log = new Progress<string>(transcript.Add);

        Enabled = false;
        bool ok = true;
        try
        {
            // .NET first: without it nothing is registered for 32-bit, so its
            // absence hides the Visual C++ problem behind an emptier one.
            if (dotNet)
                ok &= await Integrity.RuntimeInstaller.InstallAsync(DotNetRuntime.Arch.X86, log, CancellationToken.None);
            if (vcRuntime)
                ok &= await Integrity.RuntimeInstaller.InstallVcRuntimeAsync(DotNetRuntime.Arch.X86, log, CancellationToken.None);
        }
        catch (Exception ex) { transcript.Add($"Failed: {ex.Message}"); ok = false; }
        finally { Enabled = true; }

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, transcript) + Environment.NewLine + Environment.NewLine +
            (ok ? "Now press \"Repair all\" on the Status tab, then restart your reader. "
                + "A reader that was already running keeps the old, failed engine loaded until it restarts."
                : "Some of it did not install. The Status tab lists what is still missing, "
                + "with the command for each."),
            ok ? "Prerequisites installed" : "Not fully installed",
            MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Honours a user who has decided they do not care about 32-bit hosts.
    /// Set <c>HKCU\SOFTWARE\VibeSuperTonic\SuppressX86Warning</c> to 1.
    /// Deliberately not a checkbox in the dialog: the failure is total and
    /// silent, so the default has to be "keep saying it", and anyone who
    /// genuinely wants it gone can turn it off deliberately.
    /// </summary>
    private static bool WarningSuppressed()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
            return k?.GetValue("SuppressX86Warning") is int v && v != 0;
        }
        catch { return false; }
    }

    private static TabPage MakeTabPage(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }
}
