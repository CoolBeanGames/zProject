using System.Net;
using System.Text;
using System.Text.Json;
using PromptQueue.Core.Models;
using PromptQueue.Core.Operator;
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
    private static string? _publicUrl;

    private static int Main(string[] args)
    {
        Console.Title = "zProject task server";
        Console.OutputEncoding = Encoding.UTF8;

        var port = 8787;
        if (args.Length > 0 && int.TryParse(args[0], out var p))
            port = p;

        _workspace = Workspace.Load();
        foreach (var broken in _workspace.Projects.Where(proj => proj.HasLoadError))
            Console.WriteLine($"WARNING: {broken.Name}: {broken.LoadError}");
        _viewHtml = LoadViewHtml();

        port = FindFreePort(port);

        // ZP-68: reach the phone through Tailscale Funnel instead of the old
        // LAN / URL-ACL route. --no-funnel keeps it localhost-only.
        var useFunnel = !args.Contains("--no-funnel", StringComparer.OrdinalIgnoreCase);

        var listener = StartListener(port, useFunnel);
        if (listener == null)
        {
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
            return 1;
        }

        _publicUrl = useFunnel ? Tailscale.StartFunnel(port) : null;
        if (_publicUrl != null)
            SelfCheck(port);

        Banner(port);

        using var reloadTimer = new Timer(_ => AutoReload(), null,
            TimeSpan.FromSeconds(ReloadSeconds), TimeSpan.FromSeconds(ReloadSeconds));

        var cts = new CancellationTokenSource();
        var stopping = 0;
        void Shutdown()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0)
                return;
            if (_publicUrl != null)
                Tailscale.StopFunnel(port);
            try { listener.Stop(); } catch { }
            cts.Cancel();
        }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        _ = AcceptLoop(listener, cts.Token);

        Console.WriteLine("Serving. Press Ctrl+C to stop.");
        Console.WriteLine(new string('-', 60));
        cts.Token.WaitHandle.WaitOne();

        Shutdown();
        Console.WriteLine("Server stopped.");
        return 0;
    }

    /// <summary>
    /// Tailscale Funnel forwards requests with the PUBLIC host header
    /// (<c>&lt;node&gt;.&lt;tailnet&gt;.ts.net</c>), and HttpListener answers 503 to any
    /// request whose host/port matches none of its prefixes (ZP-78). So in funnel
    /// mode we must bind the wildcard <c>http://+:{port}/</c>, which needs a
    /// one-time URL reservation on Windows — we add it elevated (one UAC) if it
    /// is missing. Without funnel, localhost is enough.
    /// </summary>
    private static HttpListener? StartListener(int port, bool useFunnel)
    {
        if (useFunnel && TryStart(Wildcard(port), out var wild))
            return wild;

        if (useFunnel)
        {
            Console.WriteLine($"Windows has no URL reservation for http://+:{port}/ (needed so the");
            Console.WriteLine("Tailscale Funnel hostname is accepted). Adding it now — approve the UAC prompt.");
            if (TryAddUrlAcl(port) && TryStart(Wildcard(port), out var retry))
            {
                Console.WriteLine("Reservation added.");
                return retry;
            }
            Console.WriteLine();
            Console.WriteLine("Could not add the reservation. Falling back to localhost — the Funnel URL");
            Console.WriteLine("will return 503 until you run this once in an elevated prompt:");
            Console.WriteLine($"    netsh http add urlacl url=http://+:{port}/ user=\"{Environment.UserDomainName}\\{Environment.UserName}\"");
            Console.WriteLine();
        }

        if (TryStart(new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" }, out var loc))
            return loc;

        Console.WriteLine($"Could not start the HTTP listener on port {port}.");
        return null;
    }

    private static string[] Wildcard(int port) => new[] { $"http://+:{port}/" };

    private static bool TryStart(string[] prefixes, out HttpListener? listener)
    {
        var l = new HttpListener();
        foreach (var p in prefixes)
            l.Prefixes.Add(p);
        try
        {
            l.Start();
            listener = l;
            return true;
        }
        catch (HttpListenerException)
        {
            l.Close();
            listener = null;
            return false;
        }
    }

    private static bool TryAddUrlAcl(int port)
    {
        try
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"http add urlacl url=http://+:{port}/ user=\"{user}\"",
                Verb = "runas",              // one UAC prompt
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(20000);
            return proc is { ExitCode: 0 };
        }
        catch (Exception ex)
        {
            Console.WriteLine("  netsh failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Confirms the local server answers before we trust the Funnel URL (ZP-78).</summary>
    private static void SelfCheck(int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var res = http.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
            Console.WriteLine($"  local self-check: http://127.0.0.1:{port}/ -> {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  local self-check FAILED: " + ex.Message);
        }
    }

    private static void Banner(int port)
    {
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  zProject task server");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  Port    : {port}");
        Console.WriteLine("  On this machine :");
        Console.WriteLine($"    http://localhost:{port}/");
        Console.WriteLine("  On your phone (anywhere, via Tailscale Funnel) :");
        if (_publicUrl != null)
        {
            Console.WriteLine($"    {_publicUrl}");
            Console.WriteLine("    (the very first request can take ~10-30s / show 503 while");
            Console.WriteLine("     Tailscale provisions the HTTPS certificate — just retry)");
        }
        else
        {
            Console.WriteLine("    (Tailscale Funnel not active — see the note above)");
        }
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
            else if (path == "/api/op" && ctx.Request.HttpMethod == "POST")
            {
                status = HandleOperator(ctx);
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

    /// <summary>
    /// ZP-68: the phone adjusts tasks and projects through the operator. Body is
    /// <c>{"command":"sync","args":["ZP-1","done","true"]}</c>; the operator runs
    /// in-process (same queue + mutex as the app and the operator exe). After a
    /// successful change we re-read from disk and push a reload to every viewer.
    /// </summary>
    private static int HandleOperator(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        OpRequest? req;
        try { req = JsonSerializer.Deserialize<OpRequest>(body, JsonReadOpts); }
        catch { req = null; }

        if (req == null || string.IsNullOrWhiteSpace(req.Command))
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { ok = false, message = "expected {command, args[]}" });
            return 400;
        }

        var a = req.Args ?? Array.Empty<string>();
        OperatorResult result = req.Command.ToLowerInvariant() switch
        {
            "read" when a.Length >= 1 => OperatorEngine.Read(a[0]),
            "get_archive" when a.Length >= 1 => OperatorEngine.GetArchive(a[0]),
            "get_tag" when a.Length >= 2 => OperatorEngine.GetTag(a[0], a[1]),
            "list" => OperatorEngine.List(),
            "sync" when a.Length >= 3 => OperatorEngine.Sync(a[0], a[1], string.Join(' ', a.Skip(2))),
            "new_task" when a.Length >= 2 => OperatorEngine.NewTask(a[0], a[1], a.Length > 2 ? string.Join(' ', a.Skip(2)) : ""),
            "new_project" when a.Length >= 1 => OperatorEngine.NewProject(a[0], a.Length > 1 ? a[1] : null),
            "new_local_project" when a.Length >= 1 => OperatorEngine.NewLocalProject(a[0]),
            "new_subtask" when a.Length >= 2 => OperatorEngine.NewSubtask(a[0], string.Join(' ', a.Skip(1))),
            "subtask_done" when a.Length >= 3 && int.TryParse(a[1], out var sdi) =>
                OperatorEngine.SetSubtaskDone(a[0], sdi, a[2] is "true" or "1"),
            "sync_many" when a.Length >= 3 => OperatorEngine.SyncMany(a[0], Pairs(a.Skip(1).ToArray())),
            "delete" when a.Length >= 1 => OperatorEngine.Delete(a[0]),
            "archive" when a.Length >= 1 => OperatorEngine.Archive(a[0]),
            "agent_lock" when a.Length >= 2 => OperatorEngine.AgentLock(a[0], a[1]),
            "agent_unlock" when a.Length >= 2 => OperatorEngine.AgentUnlock(a[0], a[1]),
            "move" when a.Length >= 2 && int.TryParse(a[1], out var mi) => OperatorEngine.Move(a[0], mi),
            _ => OperatorResult.Fail($"bad or incomplete command '{req.Command}'"),
        };

        if (result.Ok)
        {
            if (req.Command.StartsWith("new_", StringComparison.OrdinalIgnoreCase)
                && req.Command.Contains("project", StringComparison.OrdinalIgnoreCase))
                ReloadWorkspace();
            else
                AutoReload(silent: true);
        }

        ctx.Response.StatusCode = result.Ok ? 200 : 400;
        WriteJson(ctx, new { ok = result.Ok, message = result.Message, output = result.Output });
        return ctx.Response.StatusCode;
    }

    private sealed record OpRequest(string Command, string[]? Args);

    private static IEnumerable<KeyValuePair<string, string>> Pairs(string[] items)
    {
        for (int i = 0; i + 1 < items.Length; i += 2)
            yield return new KeyValuePair<string, string>(items[i], items[i + 1]);
    }

    /// <summary>Rebuilds the workspace project list from disk (after a new project).</summary>
    private static void ReloadWorkspace()
    {
        try
        {
            var fresh = Workspace.Load();
            lock (Sync)
                _workspace = fresh;
            PushReload();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! workspace reload failed: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
            tasks = project.Tasks.OrderBy(t => t.SectionRank).ThenBy(t => t.Order).Select(t => new
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
                merge = t.Merge,
                branch = t.Branch,
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
