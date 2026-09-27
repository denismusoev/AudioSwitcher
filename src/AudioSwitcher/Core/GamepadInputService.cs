namespace AudioSwitcher.Core;

public interface IGamepadSource
{
    int Read(Span<GamepadSnapshot> destination);
}

public sealed class GamepadInputService : IAsyncDisposable
{
    private readonly IGamepadSource source;
    private readonly GamepadInputEngine engine;
    private readonly Action<Action> scheduleDrain;
    private readonly Func<GamepadCommand, GamepadCommandResult> handler;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollInterval;
    private readonly int queueCapacity;
    private readonly object queueLock = new();
    private readonly object engineLock = new();
    private readonly List<GamepadCommand> queue = [];
    private CancellationTokenSource? cancellation;
    private Task? pollTask;
    private bool drainScheduled;
    private bool deliveryEnabled = true;
    private bool disposed;

    public GamepadInputService(
        IGamepadSource source,
        GamepadInputEngine engine,
        Action<Action> scheduleDrain,
        Func<GamepadCommand, GamepadCommandResult> handler,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        int queueCapacity = 32)
    {
        this.source = source;
        this.engine = engine;
        this.scheduleDrain = scheduleDrain;
        this.handler = handler;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(8);
        this.queueCapacity = Math.Max(1, queueCapacity);
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (disposed) throw new ObjectDisposedException(nameof(GamepadInputService));
        if (pollTask != null) return;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollTask = Task.Run(() => PollAsync(cancellation.Token), CancellationToken.None);
    }

    public void SetDeliveryEnabled(bool enabled)
    {
        deliveryEnabled = enabled;
        lock (engineLock) engine.SetDeliveryEnabled(enabled, Milliseconds());
    }

    private async Task PollAsync(CancellationToken token)
    {
        var snapshots = new GamepadSnapshot[4];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count;
                try { count = source.Read(snapshots); }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { break; }

                IReadOnlyList<GamepadCommand> commands;
                lock (engineLock)
                    commands = engine.Update(new ArraySegment<GamepadSnapshot>(snapshots, 0, count), Milliseconds(), deliveryEnabled);
                foreach (var command in commands) Enqueue(command);
                await Task.Delay(pollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private long Milliseconds()
    {
        long timestamp = timeProvider.GetTimestamp();
        return (long)timeProvider.GetElapsedTime(0, timestamp).TotalMilliseconds;
    }

    private void Enqueue(GamepadCommand command)
    {
        bool schedule = false;
        lock (queueLock)
        {
            if (queue.Count == queueCapacity)
            {
                int repeat = queue.FindIndex(item => item.IsRepeat);
                if (repeat >= 0) queue.RemoveAt(repeat);
                else if (command.IsRepeat) return;
                else queue.RemoveAt(0);
            }
            queue.Add(command);
            if (!drainScheduled)
            {
                drainScheduled = true;
                schedule = true;
            }
        }
        if (schedule) scheduleDrain(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            GamepadCommand command;
            lock (queueLock)
            {
                if (queue.Count == 0)
                {
                    drainScheduled = false;
                    return;
                }
                command = queue[0];
                queue.RemoveAt(0);
            }

            GamepadCommandResult result;
            try { result = handler(command); }
            catch { result = GamepadCommandResult.Unhandled; }
            lock (engineLock) engine.Report(command, result);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cancellation?.Cancel();
        if (pollTask != null)
        {
            try { await pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
    }
}
