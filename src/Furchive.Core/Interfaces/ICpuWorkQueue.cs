namespace Furchive.Core.Interfaces;

/// <summary>
/// Minimal background work queue for CPU-bound or generic background tasks.
/// </summary>
public interface ICpuWorkQueue
{
    /// <summary>
    /// Enqueue a piece of work to run on the worker pool.
    /// </summary>
    void Enqueue(Func<CancellationToken, Task> work);
}
