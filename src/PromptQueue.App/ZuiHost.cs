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

        private readonly WebView2 _view;
        private readonly Dictionary<string, List<Action<JsonElement>>> _handlers = new();
        private readonly Queue<string> _pending = new();
        private bool _ready;
        private bool _domReady;
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

        public async Task InitializeAsync()
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await _view.EnsureCoreWebView2Async(env);

            var core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Map a virtual host so zUI assets and host documents load over https.
            _mappedRoot = Path.GetDirectoryName(Path.GetFullPath(CoreRoot))!;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, _mappedRoot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += (_, e) => Dispatch(e.TryGetWebMessageAsString());

            // Bridge installed before any page script runs.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__zuiHost={postMessage:function(m){window.chrome.webview.postMessage(m);}};");

            core.DOMContentLoaded += (_, _) => { _domReady = true; FlushPending(); };
            core.NavigationStarting += (_, _) => _domReady = false;

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
            if (!_ready)
                throw new InvalidOperationException("Call InitializeAsync() before using ZuiHost.");
        }

        public ValueTask DisposeAsync()
        {
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
