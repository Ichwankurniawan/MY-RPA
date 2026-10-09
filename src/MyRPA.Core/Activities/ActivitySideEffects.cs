namespace MyRPA.Core.Activities;

/// <summary>
/// What an activity touches outside the workflow (catalog 1.2, ADR-0042): declared by the activity, shown by tools, and
/// the basis of permissions later. <see cref="None"/> means it only reads and writes workflow values.
/// </summary>
[Flags]
public enum ActivitySideEffects
{
    /// <summary>Only workflow values.</summary>
    None = 0,

    /// <summary>Reads or writes files or folders.</summary>
    FileSystem = 1,

    /// <summary>Sends network requests.</summary>
    Network = 2,

    /// <summary>Drives a web browser.</summary>
    Browser = 4,
}
