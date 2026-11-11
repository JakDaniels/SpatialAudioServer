using System.Collections.Concurrent;

namespace SpatialAudioServer.Media;

/// <summary>
/// One dedicated thread per room that performs every change to the room's pipeline, in order. GStreamer callbacks
/// and API calls only enqueue work here, so graph mutation is never concurrent.
/// </summary>
public sealed class RoomWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private readonly ILogger _log;

    public RoomWorker(string name, ILogger log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool IsWorkerThread => Thread.CurrentThread == _thread;

    /// <summary>Queues work without waiting. Exceptions are logged.</summary>
    public void Post(Action action)
    {
        if (!_queue.TryAdd(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Room worker task failed");
                }
            }))
        {
            _log.LogDebug("Room worker stopped; dropped a task");
        }
    }

    /// <summary>Runs <paramref name="func"/> on the worker and returns its result (or exception).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsWorkerThread)
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            return tcs.Task;
        }
        if (!_queue.TryAdd(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new ObjectDisposedException(nameof(RoomWorker)));
        }
        return tcs.Task;
    }

    /// <summary>Lets queued work finish, then stops the thread.</summary>
    public void Dispose()
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }
        _queue.CompleteAdding();
        if (!IsWorkerThread)
        {
            _thread.Join();
        }
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            action();
        }
    }
}
