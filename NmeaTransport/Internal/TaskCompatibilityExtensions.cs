namespace NmeaTransport.Internal;

internal static class TaskCompatibilityExtensions
{
    public static async Task WaitAsyncCompat(this Task task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled || task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellationTask = CreateCancellationTask(ct);
        var completedTask = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);

        if (completedTask == cancellationTask)
        {
            throw new OperationCanceledException(ct);
        }

        await task.ConfigureAwait(false);
    }

    public static async Task<T> WaitAsyncCompat<T>(this Task<T> task, CancellationToken ct)
    {
        await ((Task)task).WaitAsyncCompat(ct).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    public static async Task<string?> ReadLineAsyncCompat(this StreamReader reader, CancellationToken ct)
    {
        return await reader.ReadLineAsync().WaitAsyncCompat(ct).ConfigureAwait(false);
    }

    private static Task CreateCancellationTask(CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
        {
            return Task.Delay(Timeout.Infinite, CancellationToken.None);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs);
        return tcs.Task;
    }
}
