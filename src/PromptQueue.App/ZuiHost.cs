using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PromptQueue.App
{
    /// <summary>
    /// Hosts a zUI document inside a WebView2 control and exposes the zUI
    /// message bus to managed code. The same CSS/JS core is used by the C++
    /// binding, so both languages render an identical UI.
    /// </summary>
    public sealed class ZuiHost : IAsyncDisposable
    {
        private const string VirtualHost = "zui.app";
        private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment =
            new(() => CoreWebView2Environment.CreateAsync());

        private readonly WebView2 _view;
        private readonly Dictionary<string, List<Action<JsonElement>>> _handlers = new();
        private readonly Queue<string> _pending = new();
        private Task? _initialization;
        private CoreWebView2? _core;
        private bool _ready;
        private bool _domReady;
        private bool _disposed;
        private string _mappedRoot = "";

        public ZuiHost(WebView2 view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <summary>Directory holding the copied `zui/` core assets. Defaults to
        /// the folder next to this assembly.</summary>
        public string CoreRoot { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "zui");

        public event EventHandler? Ready;

        /// <summary>Warm or reuse the process-wide WebView2 environment. Hosts
        /// with more than one view can pass this environment to their other
        /// controls and avoid duplicate browser-process startup work.</summary>
        public static Task<CoreWebView2Environment> GetSharedEnvironmentAsync() =>
            SharedEnvironment.Value;

        public Task InitializeAsync()
        {
            ThrowIfDisposed();
            return _initialization ??= InitializeCoreAsync();
        }

        private async Task InitializeCoreAsync()
        {
            var env = await GetSharedEnvironmentAsync();
            await _view.EnsureCoreWebView2Async(env);

            var core = _core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Map a virtual host so zUI assets and host documents load over https.
            _mappedRoot = Path.GetDirectoryName(Path.GetFullPath(CoreRoot))!;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, _mappedRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;

            // Bridge installed before any page script runs.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__zuiHost={postMessage:function(m){window.chrome.webview.postMessage(m);}};");

            core.DOMContentLoaded += OnDomContentLoaded;
            // Some documents or WebView2 versions can complete navigation
            // without the DOM callback reaching the host. Completion is a safe
            // second readiness signal; FlushPending is idempotent when empty.
            core.NavigationCompleted += OnNavigationCompleted;
            core.NavigationStarting += OnNavigationStarting;

            _ready = true;
            Ready?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Load a zUI document by path relative to the virtual root
        /// (so "showcase/index.html" resolves next to "zui/").</summary>
        public Task LoadAsync(string relativePath)
        {
            EnsureReady();
            _domReady = false;
            _view.CoreWebView2.Navigate($"https://{VirtualHost}/{relativePath.Replace('\\', '/')}");
            return Task.CompletedTask;
        }

        /// <summary>Render a full compiled document string (from the zslc `csharp`
        /// backend). Written under the virtual root and navigated to, so its
        /// <c>zui/...</c> asset links resolve.</summary>
        public void LoadDocument(string html)
        {
            EnsureReady();
            var name = "__zui_compiled.html";
            File.WriteAllText(Path.Combine(_mappedRoot, name), html);
            _domReady = false;
            _view.CoreWebView2.Navigate($"https://{VirtualHost}/{name}");
        }

        /// <summary>Push a message to the UI (host -&gt; UI). Buffered until the
        /// page's DOM is ready so early sends are not lost.</summary>
        public void Send(string channel, object? payload = null)
        {
            EnsureReady();
            var json = JsonSerializer.Serialize(new { channel, payload });
            if (_domReady) _view.CoreWebView2.PostWebMessageAsString(json);
            else _pending.Enqueue(json);
        }

        /// <summary>Subscribe to a UI channel (UI -&gt; host).</summary>
        public IDisposable On(string channel, Action<JsonElement> handler)
        {
            ThrowIfDisposed();
            if (!_handlers.TryGetValue(channel, out var list))
                _handlers[channel] = list = new();
            list.Add(handler);
            return new Subscription(() => list.Remove(handler));
        }

        public void SetTheme(string name) => Send("theme", name);

        private void FlushPending()
        {
            while (_pending.Count > 0)
                _view.CoreWebView2.PostWebMessageAsString(_pending.Dequeue());
        }

        private void MarkDocumentReady()
        {
            _domReady = true;
            FlushPending();
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) =>
            Dispatch(e.TryGetWebMessageAsString());

        private void OnDomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e) =>
            MarkDocumentReady();

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) =>
            _domReady = false;

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess) MarkDocumentReady();
        }

        private void Dispatch(string? webMessageJson)
        {
            if (string.IsNullOrEmpty(webMessageJson)) return;
            try
            {
                using var doc = JsonDocument.Parse(webMessageJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("channel", out var ch)) return;
                var name = ch.GetString();
                if (name is null || !_handlers.TryGetValue(name, out var list)) return;
                var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
                foreach (var h in list.ToArray()) h(payload);
            }
            catch (JsonException) { /* ignore malformed */ }
        }

        private void EnsureReady()
        {
            ThrowIfDisposed();
            if (!_ready)
                throw new InvalidOperationException("Call InitializeAsync() before using ZuiHost.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            if (_core is not null)
            {
                _core.WebMessageReceived -= OnWebMessageReceived;
                _core.DOMContentLoaded -= OnDomContentLoaded;
                _core.NavigationStarting -= OnNavigationStarting;
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core = null;
            }
            _ready = false;
            _domReady = false;
            _handlers.Clear();
            _pending.Clear();
            return ValueTask.CompletedTask;
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _dispose;
            public Subscription(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }
}
