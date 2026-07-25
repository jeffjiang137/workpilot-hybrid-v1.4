using Microsoft.UI.Dispatching;

namespace WorkPilot.Services;

public static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this DispatcherQueue queue, Func<Task> callback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try { await callback(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        })) completion.SetException(new InvalidOperationException("UI 调度器已关闭"));
        return completion.Task;
    }
}

