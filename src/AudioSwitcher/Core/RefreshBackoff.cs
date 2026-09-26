namespace AudioSwitcher.Core;

internal sealed class RefreshBackoff
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(24),
        TimeSpan.FromSeconds(30)
    ];
    private int delayIndex;

    internal TimeSpan CurrentDelay => Delays[delayIndex];

    internal void RecordFailure() => delayIndex = Math.Min(delayIndex + 1, Delays.Length - 1);

    internal void RecordSuccess() => delayIndex = 0;
}
