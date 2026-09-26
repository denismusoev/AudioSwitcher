namespace AudioSwitcher;

internal static class TaskObserver
{
    internal static void Observe(Task task, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(onFault);

        _ = task.ContinueWith(completed =>
        {
            var error = completed.Exception?.GetBaseException();
            if (error == null) return;
            try { onFault(error); }
            catch (Exception callbackError) { DiagnosticLog.Error("task.failure_callback", callbackError); }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
