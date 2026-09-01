using System.Net;
using System.Text;
using System.Text.Json;
using PromptQueue.Core.Models;
using PromptQueue.Core.Storage;

namespace PromptQueue.Server;

/// <summary>
/// A tiny, read-only web view of every project's task queue (ZP-38). Runs as a
/// console app with its own window; the pages cannot edit anything.
/// </summary>
internal static class Program
{
    private const int ReloadSeconds = 120;
    private static readonly object Sync = new();
    private static readonly List<SseClient> Clients = new();

    private static Workspace _workspace = null!;
    private static string _viewHtml = "";

    private static int Main(string[] args)
    {
        Console.Title = "zProject task server";
        Console.OutputEncoding = Encoding.UTF8;

        var port = 8787;
        if (args.Length > 0 && int.TryParse(args[0], out var p))
            port = p;

        _workspace = Workspace.Load();
        _viewHtml = LoadViewHtml();

        HttpListener listener = new();
        var prefix = $"http://localhost:{FindFreePort(port)}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"Could not start the listener on {prefix}");
            Console.WriteLine(ex.Message);
            Console.WriteLine();
            Console.WriteLine("If this is an access error, run once in an elevated prompt:");
            Console.WriteLine($"    netsh http add urlacl url={prefix} user=%USERNAME%");
            Console.WriteLine();
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
            return 1;
        }

        Banner(prefix);

        using var reloadTimer = new Timer(_ => AutoReload(), null,
            TimeSpan.FromSeconds(ReloadSeconds), TimeSpan.FromSeconds(ReloadSeconds));

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        _ = AcceptLoop(listener, cts.Token);

        Console.WriteLine("Serving. Press Ctrl+C to stop.");
        Console.WriteLine(new string('-', 60));
        cts.Token.WaitHandle.WaitOne();

        listener.Stop();
        Console.WriteLine("Server stopped.");
        return 0;
    }

    private static void Banner(string prefix)
    {
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  zProject task server  —  read only");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  Address : {prefix}");
        Console.WriteLine($"  Open    : {prefix}");
        Console.WriteLine($"  Projects: {_workspace.Projects.Count}");
        Console.WriteLine($"  Auto-reload every {ReloadSeconds}s and on each client connect");
        Console.WriteLine(new string('=', 60));
    }

    // ---- request loop --------------------------------------------------

    private static async Task AcceptLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        var started = DateTime.Now;
        var ip = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        int status = 200;

        try
        {
            if (path is "/" or "/index.html")
            {
                WriteText(ctx, _viewHtml, "text/html; charset=utf-8");
            }
            else if (path == "/api/projects")
            {
                WriteJson(ctx, BuildProjectsPayload());
            }
            else if (path.StartsWith("/api/project/"))
            {
                var idText = path["/api/project/".Length..].Trim('/');
                if (int.TryParse(idText, out var idx) && idx >= 0 && idx < _workspace.Projects.Count)
                {
                    var project = _workspace.Projects[idx];
                    ProjectStore.ReloadInto(project);   // fresh read on every view / project switch
                    WriteJson(ctx, BuildTasksPayload(project));
                }
                else
                {
                    status = 404;
                    ctx.Response.StatusCode = 404;
                    WriteText(ctx, "unknown project", "text/plain");
                }
            }
            else if (path == "/events")
            {
                status = 200;
                ServeEvents(ctx, ip);
                Log(ip, "GET", path, status, started);
                return; // ServeEvents blocks until the client goes away
            }
            else
            {
                status = 404;
                ctx.Response.StatusCode = 404;
                WriteText(ctx, "not found", "text/plain");
            }
        }
        catch (Exception ex)
        {
            status = 500;
            try
            {
                ctx.Response.StatusCode = 500;
                WriteText(ctx, "server error", "text/plain");
            }
            catch { /* client gone */ }
            Console.WriteLine($"  ! {ex.Message}");
        }

        Log(ip, ctx.Request.HttpMethod, path, status, started);
    }

    private static void Log(string ip, string method, string path, int status, DateTime started)
    {
        var ms = (DateTime.Now - started).TotalMilliseconds;
        Console.WriteLine($"[{started:HH:mm:ss}] {ip,-15} {method,-4} {path,-28} -> {status} ({ms:F0} ms)");
    }

    // ---- server-sent events (connection tracking + reload push) -------

    private sealed class SseClient
    {
        public required HttpListenerResponse Response { get; init; }
        public required string Ip { get; init; }
    }

    private static void ServeEvents(HttpListenerContext ctx, string ip)
    {
        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        res.Headers.Add("Cache-Control", "no-cache");
        res.SendChunked = true;

        var client = new SseClient { Response = res, Ip = ip };
        int active;
        lock (Sync)
        {
            Clients.Add(client);
            active = Clients.Count;
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] client {ip} connected  ({active} active)");

        // A connect is also a good moment to refresh from disk.
        AutoReload(silent: true);

        try
        {
            SendRaw(res, "event: hello\ndata: connected\n\n");
            while (true)
            {
                Thread.Sleep(15000);
                SendRaw(res, ": ping\n\n");   // keep-alive; throws when the client is gone
            }
        }
        catch
        {
            // client disconnected
        }
        finally
        {
            lock (Sync)
            {
                Clients.Remove(client);
                active = Clients.Count;
            }
            try { res.OutputStream.Close(); } catch { }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] client {ip} disconnected ({active} active)");
        }
    }

    private static void SendRaw(HttpListenerResponse res, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Flush();
    }

    private static void PushReload()
    {
        SseClient[] snapshot;
        lock (Sync)
            snapshot = Clients.ToArray();

        foreach (var c in snapshot)
        {
            try { SendRaw(c.Response, "event: reload\ndata: {}\n\n"); }
            catch { /* dropped; cleaned up by its own loop */ }
        }
    }

    private static void AutoReload(bool silent = false)
    {
        try
        {
            foreach (var project in _workspace.Projects)
                ProjectStore.ReloadInto(project);
            if (!silent)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] auto-reload: re-read {_workspace.Projects.Count} project(s) from disk");
            PushReload();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! auto-reload failed: {ex.Message}");
        }
    }

    // ---- payloads ----------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static object BuildProjectsPayload()
    {
        lock (Sync)
        {
            return new
            {
                projects = _workspace.Projects.Select((p, i) => new
                {
                    index = i,
                    name = p.Name,
                    directory = p.Directory,
                    total = p.Tasks.Count,
                    done = p.Tasks.Count(t => t.Done),
                    open = p.Tasks.Count(t => !t.Done && !t.Archived),
                }).ToArray(),
                reloadSeconds = ReloadSeconds,
            };
        }
    }

    private static object BuildTasksPayload(Project project)
    {
        return new
        {
            name = project.Name,
            directory = project.Directory,
            tasks = project.Tasks.OrderBy(t => t.Order).Select(t => new
            {
                id = t.Id,
                name = t.Name,
                displayName = t.DisplayName,
                prompt = t.Prompt,
                requirements = t.Requirements,
                inProgress = t.InProgress,
                done = t.Done,
                bug = t.Bug,
                error = t.Error,
                errorMessage = t.ErrorMessage,
                locked = t.Locked,
                archived = t.Archived,
                blockedBy = t.BlockedBy,
                dateStarted = t.DateStartedText,
                dueDate = t.DueDateText,
                commit = t.Commit,
                build = t.Build,
                release = t.Release,
                tags = t.Tags,
                notes = t.Notes,
                filesChanged = t.FilesChanged,
                sectionRank = t.SectionRank,
                sectionKey = t.SectionKey,
                statusText = t.StatusText,
                subtasks = t.Subtasks.Select(s => new { text = s.Text, done = s.Done }).ToArray(),
            }).ToArray(),
        };
    }

    // ---- helpers ---------------------------------------------------

    private static void WriteJson(HttpListenerContext ctx, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        WriteText(ctx, json, "application/json; charset=utf-8");
    }

    private static void WriteText(HttpListenerContext ctx, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static string LoadViewHtml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "zProject_webView.html");
        return File.Exists(path)
            ? File.ReadAllText(path)
            : "<!doctype html><title>zProject</title><p>zProject_webView.html is missing next to the server exe.</p>";
    }

    private static int FindFreePort(int preferred)
    {
        for (int port = preferred; port < preferred + 50; port++)
        {
            try
            {
                var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                t.Start();
                t.Stop();
                return port;
            }
            catch
            {
                // in use; try the next
            }
        }
        return preferred;
    }
}
