using VibeSuperTonic.Core.Settings;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The rule four reported bugs broke: a reload must not discard an edit.
///
/// <para>A speaker that snapped back to 0, a rate that reverted when Save landed
/// on the same repaint as a refresh, a volume trim read from the wrong scope,
/// and a rule list a tab switch threw away. Each was found by a person using the
/// product, which is the expensive way.</para>
/// </summary>
public sealed class PendingEditsTests
{
    [Fact]
    public void An_untouched_document_loads()
    {
        var edits = new PendingEdits();
        bool filled = false;

        Assert.True(edits.Load(() => filled = true));
        Assert.True(filled);
        Assert.False(edits.Touched);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. These tabs re-read on every visit, and a toolkit
    /// re-attaches a tab's content each time it is selected — so switching away
    /// and back is a reload nobody asked for, arriving on top of unsaved work.
    /// </summary>
    [Fact]
    public void A_reload_does_not_run_while_something_is_unsaved()
    {
        var edits = new PendingEdits();
        edits.Edited();

        bool filled = false;
        Assert.False(edits.Load(() => filled = true));
        Assert.False(filled);
    }

    /// <summary>
    /// And the caller is TOLD, rather than left to guess: the return value is
    /// what a tab uses to say "these are your unsaved edits, not the file". A
    /// tab that silently ignores the file is as confusing as one that silently
    /// discards the edit.
    /// </summary>
    [Fact]
    public void The_answer_says_which_of_the_two_is_on_screen()
    {
        var edits = new PendingEdits();

        Assert.True(edits.Load(() => { }));
        edits.Edited();
        Assert.False(edits.Load(() => { }));
        edits.Saved();
        Assert.True(edits.Load(() => { }));
    }

    [Fact]
    public void A_save_hands_authority_back_to_the_file()
    {
        var edits = new PendingEdits();
        edits.Edited();
        edits.Saved();

        Assert.False(edits.Touched);
        Assert.True(edits.Load(() => { }));
    }

    /// <summary>Revert is the one path allowed to lose work, and it fills.</summary>
    [Fact]
    public void Discarding_throws_the_edits_away_and_fills()
    {
        var edits = new PendingEdits();
        edits.Edited();

        bool filled = false;
        edits.Discard(() => filled = true);

        Assert.True(filled);
        Assert.False(edits.Touched);
    }

    [Fact]
    public void Editing_again_after_a_save_protects_the_new_edit_too()
    {
        var edits = new PendingEdits();
        edits.Edited();
        edits.Saved();
        edits.Edited();

        Assert.False(edits.Load(() => Assert.Fail("this reload should not have run")));
    }
}
