namespace Hime.Data.Services;

/// <summary>Controls coalesced background persistence for frequently updated LiteDB records.</summary>
public sealed class LiteDbWriteBehindOptions
{
    public int FlushIntervalMilliseconds { get; set; } = 250;

    public int MaxBatchSize { get; set; } = 128;

    public int PendingKeyWarningThreshold { get; set; } = 1024;
}
