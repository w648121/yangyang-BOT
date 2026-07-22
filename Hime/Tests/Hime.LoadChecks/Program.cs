using System.Diagnostics;
using Hime.Data;
using Hime.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("LOAD CHECK FAILED: " + message);
}

var diagnostics = new RuntimeDiagnostics(Options.Create(new RuntimeDiagnosticsOptions
{
    SampleWindowSize = 4096,
    RecentEventLimit = 50
}));

// Reconnect/replay race: exactly one worker may accept the same protocol event.
var deduplication = new MessageDeduplicationService(Options.Create(new MessageDeduplicationOptions
{
    RetentionSeconds = 600,
    MaximumEntries = 5000
}));
var accepted = await Task.WhenAll(Enumerable.Range(0, 200)
    .Select(_ => Task.Run(() => deduplication.TryAccept("group:100:message:9001"))));
Assert(accepted.Count(value => value) == 1, "duplicate QQ events were accepted more than once");

// A generated voice that has not been delivered must survive a process restart,
// while duplicate enqueue attempts for the same reply remain idempotent.
var outboxDatabase = $"voice-outbox-check-{Guid.NewGuid():N}";
var voiceOptions = Options.Create(new VoiceSynthesisOptions
{
    Outbox = new VoiceOutboxOptions
    {
        Enabled = true,
        MaximumAttempts = 3,
        RecoveryMaximumAgeMinutes = 5
    }
});
string outboxId;
using (var database = new HimeDbContext(outboxDatabase))
{
    var store = new VoiceOutboxStore(database, voiceOptions, NullLogger<VoiceOutboxStore>.Instance);
    var created = store.TryCreate("测试补发", "yangyang", "group:100", "neutral", 0, "corr-1", "group", 100);
    Assert(created is not null, "voice outbox did not accept a new delivery");
    outboxId = created!.Id;
    Assert(store.TryCreate("测试补发", "yangyang", "group:100", "neutral", 0, "corr-1", "group", 100) is null,
        "voice outbox accepted a duplicate delivery");
    Assert(store.PendingCount == 1, "voice outbox pending count is inaccurate");
}
using (var database = new HimeDbContext(outboxDatabase))
{
    var recovered = new VoiceOutboxStore(database, voiceOptions, NullLogger<VoiceOutboxStore>.Instance);
    Assert(recovered.GetRecoverable(DateTimeOffset.UtcNow).Any(entry => entry.Id == outboxId),
        "voice outbox did not survive a database reopen");
    recovered.Complete(outboxId);
    Assert(recovered.PendingCount == 0, "completed voice outbox entry was retained");
}

// Bounded partitioned dispatcher: preserve strict order per conversation while
// unrelated conversations execute concurrently.
var dispatcher = new ConversationMessageDispatcher(
    Options.Create(new MessageDispatchOptions { PartitionCount = 8, CapacityPerPartition = 64 }),
    NullLogger<ConversationMessageDispatcher>.Instance,
    diagnostics);
await dispatcher.StartAsync(CancellationToken.None);

const int conversations = 32;
const int perConversation = 64;
const int total = conversations * perConversation;
var observed = Enumerable.Range(0, conversations).ToDictionary(key => key, _ => new List<int>(perConversation));
var completed = 0;
var active = 0;
var maximumActive = 0;
var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var stopwatch = Stopwatch.StartNew();

for (var sequence = 0; sequence < perConversation; sequence++)
{
    for (var conversation = 0; conversation < conversations; conversation++)
    {
        var capturedConversation = conversation;
        var capturedSequence = sequence;
        await dispatcher.EnqueueAsync(
            capturedConversation,
            $"load:{capturedConversation}:{capturedSequence}",
            async cancellationToken =>
            {
                var current = Interlocked.Increment(ref active);
                while (true)
                {
                    var previous = Volatile.Read(ref maximumActive);
                    if (current <= previous || Interlocked.CompareExchange(ref maximumActive, current, previous) == previous)
                        break;
                }

                await Task.Delay(1, cancellationToken);
                observed[capturedConversation].Add(capturedSequence);
                Interlocked.Decrement(ref active);
                if (Interlocked.Increment(ref completed) == total)
                    allCompleted.TrySetResult();
            });
    }
}

await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20));
stopwatch.Stop();
foreach (var conversation in observed)
{
    Assert(
        conversation.Value.SequenceEqual(Enumerable.Range(0, perConversation)),
        $"conversation {conversation.Key} was processed out of order");
}
Assert(maximumActive >= 2, "unrelated conversations did not execute concurrently");
Assert(dispatcher.PendingCount == 0, "dispatcher retained pending work after completion");
try
{
    await dispatcher.StopAsync(CancellationToken.None);
}
catch (OperationCanceledException)
{
}

// Failure accounting must not break later successful measurements.
for (var index = 0; index < 20; index++)
{
    try
    {
        var shouldFail = index % 4 == 0;
        await diagnostics.TrackAsync("fault.synthetic", async () =>
        {
            await Task.Yield();
            if (shouldFail)
                throw new TimeoutException("synthetic dependency timeout");
        });
    }
    catch (TimeoutException)
    {
    }
}

var snapshot = diagnostics.Snapshot();
var fault = snapshot.Metrics["fault.synthetic"];
var processing = snapshot.Metrics["message.process"];
Assert(fault.Completed == 20 && fault.Failed == 5, "fault metrics are inaccurate");
Assert(processing.Completed == total && processing.Failed == 0, "message processing metrics are inaccurate");

Console.WriteLine($"Messages={total}, Conversations={conversations}, ElapsedMs={stopwatch.ElapsedMilliseconds}, MaxParallel={maximumActive}");
Console.WriteLine($"Message P50={processing.P50Ms:F2}ms, P95={processing.P95Ms:F2}ms, Failures={processing.Failed}");
Console.WriteLine("PASS: bounded concurrency, per-conversation ordering, replay deduplication, and fault accounting");
