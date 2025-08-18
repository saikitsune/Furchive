using Furchive.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Furchive.Core.Services;

/// <summary>
/// Background worker pool for CPU-bound tasks. Degree of parallelism is configurable.
/// </summary>
public sealed class CpuWorkQueue : BackgroundService, ICpuWorkQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _channel;
    private readonly ILogger<CpuWorkQueue> _logger;
    private readonly int _degree;

    public CpuWorkQueue(ILogger<CpuWorkQueue> logger, ISettingsService settings)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        _degree = Math.Clamp(settings.GetSetting<int>("CpuWorkerDegree", Environment.ProcessorCount / 2), 1, Environment.ProcessorCount);
    }

    public void Enqueue(Func<CancellationToken, Task> work)
    {
        if (work == null) return;
        _ = _channel.Writer.TryWrite(work);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var readers = new List<Task>();
        for (int i = 0; i < _degree; i++)
        {
            readers.Add(Task.Run(() => RunWorkerAsync(stoppingToken), stoppingToken));
        }
        await Task.WhenAll(readers);
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var work))
                {
                    try { await work(ct); }
                    catch (OperationCanceledException) { /* ignore */ }
                    catch (Exception ex) { _logger.LogDebug(ex, "CpuWork item failed"); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CpuWorkQueue worker crashed");
        }
    }
}
