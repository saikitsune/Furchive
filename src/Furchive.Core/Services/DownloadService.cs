using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http;

namespace Furchive.Core.Services;

/// <summary>
/// Download service implementation
/// </summary>
public class DownloadService : IDownloadService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _downloadJobs = new();
    private readonly IUnifiedApiService _apiService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DownloadService> _logger;
    private readonly SemaphoreSlim _downloadSemaphore;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public event EventHandler<DownloadJob>? DownloadProgressUpdated;
    public event EventHandler<DownloadJob>? DownloadStatusChanged;

    public DownloadService(
        IUnifiedApiService apiService,
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILogger<DownloadService> logger)
    {
        _apiService = apiService;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Initialize semaphore with concurrent download limit from settings
        var concurrentDownloads = _settingsService.GetSetting<int>("ConcurrentDownloads", 3);
        _downloadSemaphore = new SemaphoreSlim(concurrentDownloads, concurrentDownloads);

        // Start processing downloads
        _ = Task.Run(ProcessDownloadQueueAsync);
    }

    public Task<string> QueueDownloadAsync(MediaItem mediaItem, string destinationPath)
    {
        var job = new DownloadJob
        {
            MediaItem = mediaItem,
            DestinationPath = GenerateFilePath(mediaItem, destinationPath),
            Status = DownloadStatus.Queued
        };

        // Check for duplicates based on policy
        var duplicatePolicy = _settingsService.GetSetting<string>("DownloadDuplicatesPolicy", "skip");
        if (duplicatePolicy == "skip" && File.Exists(job.DestinationPath))
        {
            job.Status = DownloadStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Skipping duplicate download: {Path}", job.DestinationPath);
        }

        _downloadJobs[job.Id] = job;
        DownloadStatusChanged?.Invoke(this, job);

    _logger.LogInformation("Queued download: {Title} to {Path}", mediaItem.Title, job.DestinationPath);
    return Task.FromResult(job.Id);
    }

    public async Task<List<string>> QueueMultipleDownloadsAsync(List<MediaItem> mediaItems, string destinationPath)
    {
        var jobIds = new List<string>();
        
        foreach (var mediaItem in mediaItems)
        {
            var jobId = await QueueDownloadAsync(mediaItem, destinationPath);
            jobIds.Add(jobId);
        }

        return jobIds;
    }

    public Task<List<DownloadJob>> GetDownloadJobsAsync()
    {
        return Task.FromResult(_downloadJobs.Values.OrderByDescending(j => j.QueuedAt).ToList());
    }

    public Task<DownloadJob?> GetDownloadJobAsync(string jobId)
    {
        _downloadJobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    public Task<bool> PauseDownloadAsync(string jobId)
    {
        if (_downloadJobs.TryGetValue(jobId, out var job) && job.Status == DownloadStatus.Downloading)
        {
            job.Status = DownloadStatus.Paused;
            DownloadStatusChanged?.Invoke(this, job);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> ResumeDownloadAsync(string jobId)
    {
        if (_downloadJobs.TryGetValue(jobId, out var job) && job.Status == DownloadStatus.Paused)
        {
            job.Status = DownloadStatus.Queued;
            DownloadStatusChanged?.Invoke(this, job);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> CancelDownloadAsync(string jobId)
    {
        if (_downloadJobs.TryGetValue(jobId, out var job))
        {
            job.Status = DownloadStatus.Cancelled;
            DownloadStatusChanged?.Invoke(this, job);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> RetryDownloadAsync(string jobId)
    {
        if (_downloadJobs.TryGetValue(jobId, out var job) && job.Status == DownloadStatus.Failed)
        {
            job.Status = DownloadStatus.Queued;
            job.RetryCount++;
            job.ErrorMessage = null;
            job.BytesDownloaded = 0;
            DownloadStatusChanged?.Invoke(this, job);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private async Task ProcessDownloadQueueAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                var queuedJobs = _downloadJobs.Values
                    .Where(j => j.Status == DownloadStatus.Queued)
                    .OrderBy(j => j.QueuedAt)
                    .ToList();

                var downloadTasks = queuedJobs.Select(job => ProcessSingleDownloadAsync(job));
                await Task.WhenAll(downloadTasks);

                await Task.Delay(1000, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in download queue processing");
                await Task.Delay(5000, _cancellationTokenSource.Token);
            }
        }
    }

    private async Task ProcessSingleDownloadAsync(DownloadJob job)
    {
        await _downloadSemaphore.WaitAsync(_cancellationTokenSource.Token);

        try
        {
            if (job.Status != DownloadStatus.Queued)
                return;

            job.Status = DownloadStatus.Downloading;
            job.StartedAt = DateTime.UtcNow;
            DownloadStatusChanged?.Invoke(this, job);

            // Get download URL and latest details
            var details = await _apiService.GetMediaDetailsAsync(job.MediaItem.Source, job.MediaItem.Id);
            if (details?.FullImageUrl == null)
            {
                throw new InvalidOperationException("Could not get download URL");
            }
            // If original extension missing, update path with derived extension
            if (string.IsNullOrWhiteSpace(job.MediaItem.FileExtension))
            {
                var ext = TryGetExtensionFromUrl(details.FullImageUrl);
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    var newPath = Path.ChangeExtension(job.DestinationPath, ext);
                    job.DestinationPath = newPath;
                }
            }

            // Ensure directory exists
            var directory = Path.GetDirectoryName(job.DestinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            // Download file
            using var httpClient = _httpClientFactory.CreateClient();
            try
            {
                var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                var defaultUa = $"Furchive/{version} (by USERNAME)";
                var ua = _settingsService.GetSetting<string>("E621UserAgent", defaultUa) ?? defaultUa;
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            }
            catch { /* ignore UA parse issues */ }
            httpClient.Timeout = TimeSpan.FromSeconds(_settingsService.GetSetting<int>("NetworkTimeoutSeconds", 30));

            using var response = await httpClient.GetAsync(details.FullImageUrl, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource.Token);
            response.EnsureSuccessStatusCode();

            job.TotalBytes = response.Content.Headers.ContentLength ?? 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(_cancellationTokenSource.Token);
            using var fileStream = new FileStream(job.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[8192];
            int bytesRead;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token)) > 0)
            {
                if (job.Status == DownloadStatus.Paused || job.Status == DownloadStatus.Cancelled)
                    break;

                await fileStream.WriteAsync(buffer, 0, bytesRead, _cancellationTokenSource.Token);
                job.BytesDownloaded += bytesRead;
                
                DownloadProgressUpdated?.Invoke(this, job);
            }

            if (job.Status == DownloadStatus.Downloading)
            {
                job.Status = DownloadStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                
                // Save metadata if enabled
                var saveMetadata = _settingsService.GetSetting<bool>("SaveMetadataJson", false);
                if (saveMetadata)
                {
                    await SaveMetadataAsync(job);
                }
            }

            DownloadStatusChanged?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            job.Status = DownloadStatus.Failed;
            job.ErrorMessage = ex.Message;
            DownloadStatusChanged?.Invoke(this, job);
            
            _logger.LogError(ex, "Download failed for {Title}", job.MediaItem.Title);
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private string GenerateFilePath(MediaItem mediaItem, string basePath)
    {
        var hasPoolContext = mediaItem.TagCategories != null && (mediaItem.TagCategories.ContainsKey("page_number") || mediaItem.TagCategories.ContainsKey("pool_name"));
        var template = hasPoolContext
            ? (_settingsService.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
            : (_settingsService.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}_{safeTitle}.{ext}") ?? "{source}/{artist}/{id}_{safeTitle}.{ext}");
        var useOriginalFilename = _settingsService.GetSetting<bool>("UseOriginalFilename", false);

        if (useOriginalFilename && !string.IsNullOrEmpty(mediaItem.Title))
        {
            var safeTitle = SanitizeFilename(mediaItem.Title);
            var ext = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? TryGetExtensionFromUrl(mediaItem.FullImageUrl) : mediaItem.FileExtension;
            return Path.Combine(basePath, $"{safeTitle}.{ext}");
        }

        // Replace template variables
        var extFinal = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? TryGetExtensionFromUrl(mediaItem.FullImageUrl) : mediaItem.FileExtension;
        var filename = template
            .Replace("{source}", mediaItem.Source)
            .Replace("{artist}", SanitizeFilename(mediaItem.Artist))
            .Replace("{id}", mediaItem.Id)
            .Replace("{safeTitle}", SanitizeFilename(mediaItem.Title))
            .Replace("{ext}", extFinal ?? string.Empty)
            .Replace("{pool_name}", SanitizeFilename(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("pool_name", out var poolNames) && poolNames.Count > 0 ? poolNames[0] : string.Empty))
            .Replace("{page_number}", SanitizeFilename(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("page_number", out var pageNumbers) && pageNumbers.Count > 0 ? pageNumbers[0] : string.Empty));

        return Path.Combine(basePath, filename);
    }

    private string SanitizeFilename(string filename)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(filename.Where(c => !invalidChars.Contains(c)).ToArray())
            .Replace(" ", "_")
            .Trim();
    }

    private static string? TryGetExtensionFromUrl(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var uri = new Uri(url);
            var ext = Path.GetExtension(uri.AbsolutePath).Trim('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) return null;
            return ext;
        }
        catch { return null; }
    }

    private async Task SaveMetadataAsync(DownloadJob job)
    {
        try
        {
            var metadataPath = Path.ChangeExtension(job.DestinationPath, "json");
            var metadata = System.Text.Json.JsonSerializer.Serialize(job.MediaItem, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metadataPath, metadata);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save metadata for {Title}", job.MediaItem.Title);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _downloadSemaphore?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}
