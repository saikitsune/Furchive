using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Furchive.Avalonia.Services;

public interface ILocalMediaProxy
{
    Uri? BaseAddress { get; }
    string GetPlayerUrl(string mediaUrl);
}

public sealed class LocalMediaProxyService : ILocalMediaProxy, IHostedService
{
    private readonly ILogger<LocalMediaProxyService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private IHost? _webHost;
    private Uri? _baseAddress;

    public LocalMediaProxyService(ILogger<LocalMediaProxyService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public Uri? BaseAddress => _baseAddress;

    public string GetPlayerUrl(string mediaUrl)
    {
        if (_baseAddress == null) throw new InvalidOperationException("Local media proxy not started");
        var q = Uri.EscapeDataString(mediaUrl);
        return new Uri(_baseAddress, $"/player?u={q}").ToString();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Try to bind on a loopback port in a small range
        var rnd = new Random();
        var ports = Enumerable.Range(57910, 20).OrderBy(_ => rnd.Next()).ToArray();
        Exception? lastEx = null;
        foreach (var port in ports)
        {
            try
            {
                var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();
                builder.WebHost.UseKestrel();
                var app = builder.Build();
                app.Urls.Add($"http://127.0.0.1:{port}");

                MapEndpoints(app);

                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _webHost = app;
                _baseAddress = new Uri($"http://127.0.0.1:{port}");
                _logger.LogInformation("Local media proxy listening at {Base}", _baseAddress);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning(ex, "Failed to bind local proxy on port {Port}", port);
            }
        }
        throw new InvalidOperationException("Failed to start local media proxy", lastEx);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (_webHost != null) await _webHost.StopAsync(cancellationToken).ConfigureAwait(false); } catch { }
        try { if (_webHost != null) await _webHost.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false); } catch { }
        try { _webHost?.Dispose(); } catch { }
        _webHost = null;
        _baseAddress = null;
    }

    private void MapEndpoints(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        app.MapGet("/player", async (HttpContext ctx) =>
        {
            if (!ctx.Request.Query.TryGetValue("u", out var val) || string.IsNullOrWhiteSpace(val))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("Missing u param");
                return;
            }
            var url = val.ToString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("Invalid URL");
                return;
            }
            var videoSrc = $"/video?u={Uri.EscapeDataString(url)}";
            var html = $$"""
                <!doctype html>
                <html>
                <head>
                  <meta charset="utf-8" />
                  <meta http-equiv="X-UA-Compatible" content="IE=edge" />
                  <meta name="viewport" content="width=device-width, initial-scale=1" />
                  <style>html,body{height:100%;background:#000;margin:0;padding:0}video{width:100%;height:100%;background:#000}</style>
                  <title>Player</title>
                </head>
                <body>
                  <video id="v" controls autoplay playsinline preload="auto">
                    <source src="{videoSrc}" />
                  </video>
                  <script>
                    const v=document.getElementById('v');
                    window.addEventListener('message', ev => {
                      const {cmd, val}=ev.data||{};
                      try {
                        if(cmd==='play') v.play();
                        else if(cmd==='pause') v.pause();
                        else if(cmd==='seek') v.currentTime = Number(val)||0;
                        else if(cmd==='volume') v.volume = Math.max(0, Math.min(1, Number(val)||0));
                      } catch {}
                    });
                  </script>
                </body>
                </html>
                """;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        });

        app.MapGet("/video", async (HttpContext ctx) =>
        {
            if (!ctx.Request.Query.TryGetValue("u", out var val) || string.IsNullOrWhiteSpace(val))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("Missing u param");
                return;
            }
            var url = val.ToString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("Invalid URL");
                return;
            }
            // Optional: restrict hosts to e621 static content
            // if (!uri.Host.EndsWith("e621.net", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 403; return; }

            var client = _httpClientFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // Forward range to support seeking
            if (ctx.Request.Headers.TryGetValue("Range", out var range))
                req.Headers.TryAddWithoutValidation("Range", range.ToString());
            // Set headers expected by e621
            req.Headers.UserAgent.Clear();
            req.Headers.TryAddWithoutValidation("User-Agent", "Furchive/1.1 (+https://github.com/saikitsune/Furchive)");
            req.Headers.Referrer = new Uri("https://e621.net/");
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("video/webm"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("video/mp4"));

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers)
                ctx.Response.Headers[h.Key] = string.Join(",", h.Value);
            foreach (var h in resp.Content.Headers)
                ctx.Response.Headers[h.Key] = string.Join(",", h.Value);
            // Ensure Kestrel doesn't try to apply chunked encoding when Content-Length exists
            ctx.Response.Headers.Remove("transfer-encoding");

            await using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
            await stream.CopyToAsync(ctx.Response.Body, 64 * 1024, ctx.RequestAborted);
        });
    }
}
