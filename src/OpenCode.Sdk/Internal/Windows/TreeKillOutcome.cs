namespace OpenCode.Sdk.Internal.Windows;

/// <summary>How one run of the tree kill ended.</summary>
internal enum TreeKillOutcome
{
    /// <summary>The kill exited zero: it ended every process of the tree.</summary>
    Ended,

    /// <summary>The kill could not start, or exited non-zero: 128 when the root is already gone, so none of its descendants was reached either.</summary>
    Failed,

    /// <summary>The kill was still running when its bound expired; it was ended, so it does not outlive the release that started it.</summary>
    TimedOut,
}
