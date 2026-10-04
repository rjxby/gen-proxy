namespace GenProxy.StackRunner;

internal static class ProcessSupervision
{
    public static async Task<int> RunAsync(
        IReadOnlyList<ManagedProcess> requiredProcesses,
        Func<CancellationToken, Task<int>> operation,
        CancellationToken cancellationToken)
    {
        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exitTasks = requiredProcesses
            .Select(process => process.WaitForProcessExitAsync(observationCancellation.Token))
            .ToArray();
        Task<int>? operationTask = null;

        try
        {
            operationTask = operation(observationCancellation.Token);
            var completed = await Task.WhenAny(exitTasks.Append(operationTask));
            await completed;
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var process in requiredProcesses)
            {
                process.ThrowIfExitedUnexpectedly();
            }

            return await operationTask;
        }
        finally
        {
            observationCancellation.Cancel();
            var pendingTasks = operationTask is null ? exitTasks : exitTasks.Append(operationTask);
            try
            {
                await Task.WhenAll(pendingTasks);
            }
            catch
            {
                // Observe canceled work and secondary failures before the owner disposes
                // processes. The completed operation or process check keeps its outcome.
            }
        }
    }
}
