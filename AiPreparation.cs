namespace McuFlasher;

internal static class AiPreparation
{
    public static async Task<T> Run<T>(Func<CancellationToken, T> prepare, TimeSpan timeout, CancellationToken cancel)
        where T : IDisposable
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var work = Task.Run(() => prepare(token));
        try
        {
            var value = await work.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return value;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A file system call can outlive cancellation. Its result is never
            // passed to the programmer after this deadline.
            _ = Task.Run(async () =>
            {
                try { (await work.ConfigureAwait(false)).Dispose(); }
                catch (Exception) { /* Observe failure in abandoned preparation. */ }
            });
            if (cancel.IsCancellationRequested)
                throw new OperationCanceledException("已取消固件准备。本次未擦写芯片。", cancel);
            throw new TimeoutException($"固件准备超过 {timeout.TotalSeconds:0} 秒。本次未擦写芯片。");
        }
    }
}
