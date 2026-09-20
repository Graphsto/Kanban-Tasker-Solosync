namespace KanbanTasker.Tests;

// Unix permits replacing a read-only file when its directory is writable and
// does not enforce Windows FILE_SHARE_DELETE semantics. Report these tests as
// skipped there instead of either asserting Windows behavior or silently passing.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows file attribute/sharing semantics.";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows file attribute/sharing semantics.";
    }
}
