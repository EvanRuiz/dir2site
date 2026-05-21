// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

namespace dir2site.Services;

public class PreviewServerService : IDisposable
{
    private WebServer? _server;
    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounceTimer;
    private volatile int _version;

    public string ServerUrl { get; private set; } = string.Empty;
    public bool IsRunning { get; private set; }

    public Task StartAsync(string siteRoot)
    {
        if (IsRunning) return Task.CompletedTask;

        var port = FindAvailablePort(8080);
        ServerUrl = $"http://localhost:{port}/";

        _cts = new CancellationTokenSource();

        _server = new WebServer(o => o
                .WithUrlPrefix(ServerUrl)
                .WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new ActionModule("/", HttpVerbs.Any,
                ctx => HandleRequestAsync(ctx, siteRoot)));

        _debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => Interlocked.Increment(ref _version);

        _watcher = new FileSystemWatcher(siteRoot)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        _watcher.Changed += TriggerReload;
        _watcher.Created += TriggerReload;
        _watcher.Deleted += TriggerReload;
        _watcher.Renamed += TriggerReload;

        _ = _server.RunAsync(_cts.Token);
        IsRunning = true;

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _cts?.Cancel();
        _cts = null;
        _server = null;
        IsRunning = false;
        ServerUrl = string.Empty;
        return Task.CompletedTask;
    }

    private void TriggerReload(object? sender, FileSystemEventArgs e)
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private async Task HandleRequestAsync(IHttpContext ctx, string siteRoot)
    {
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";

        var urlPath = ctx.RequestedPath;

        if (urlPath == "/__reload-check")
        {
            var json = $"{{\"v\":{_version}}}";
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            return;
        }

        var relativePath = Uri.UnescapeDataString(urlPath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        var fullSiteRoot = Path.GetFullPath(siteRoot);
        var filePath = Path.GetFullPath(Path.Combine(fullSiteRoot, relativePath));

        if (!filePath.StartsWith(fullSiteRoot, StringComparison.OrdinalIgnoreCase))
            throw HttpException.Forbidden();

        if (Directory.Exists(filePath))
            filePath = Path.Combine(filePath, "index.html");

        if (!File.Exists(filePath))
            throw HttpException.NotFound();

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var mimeType = GetMimeType(ext);

        if (ext == ".html")
        {
            var html = await File.ReadAllTextAsync(filePath);
            html = InjectReloadScript(html);
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            ctx.Response.ContentType = mimeType;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
    }

    private static string InjectReloadScript(string html)
    {
        const string script =
            "<script>(function(){var v=null;setInterval(function(){" +
            "fetch('/__reload-check').then(function(r){return r.json();}).then(function(d){" +
            "if(v===null){v=d.v;}else if(v!==d.v){location.reload();}}).catch(function(){});},2000);})();</script>";

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? html.Insert(idx, script) : html + script;
    }

    private static string GetMimeType(string ext) => ext switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".pdf" => "application/pdf",
        ".xml" => "application/xml",
        _ => "application/octet-stream"
    };

    private static int FindAvailablePort(int startPort)
    {
        for (var port = startPort; port < startPort + 100; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch { }
        }
        return startPort;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
