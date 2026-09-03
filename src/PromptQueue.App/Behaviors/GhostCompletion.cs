using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PromptQueue.App.Behaviors;

/// <summary>
/// Inline "ghost text" autocomplete for a comma-separated <see cref="TextBox"/>
/// (ZP-66). As the user types the last segment, the best-matching entry from
/// <see cref="SuggestionsProperty"/> is drawn after the caret in a faint grey;
/// pressing Tab (or Right arrow at the end) accepts it.
/// </summary>
public static class GhostCompletion
{
    public static readonly DependencyProperty SuggestionsProperty =
        DependencyProperty.RegisterAttached(
            "Suggestions", typeof(IEnumerable), typeof(GhostCompletion),
            new PropertyMetadata(null, OnSuggestionsChanged));

    public static void SetSuggestions(DependencyObject o, IEnumerable? v) => o.SetValue(SuggestionsProperty, v);
    public static IEnumerable? GetSuggestions(DependencyObject o) => (IEnumerable?)o.GetValue(SuggestionsProperty);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(GhostState), typeof(GhostCompletion));

    private static void OnSuggestionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb)
            return;

        var state = (GhostState?)tb.GetValue(StateProperty);
        if (state == null)
        {
            state = new GhostState(tb);
            tb.SetValue(StateProperty, state);
        }
        state.Suggestions = e.NewValue as IEnumerable;
    }

    private sealed class GhostState
    {
        private readonly TextBox _tb;
        private GhostAdorner? _adorner;

        public IEnumerable? Suggestions { get; set; }

        public GhostState(TextBox tb)
        {
            _tb = tb;
            if (tb.IsLoaded)
                Attach();
            else
                tb.Loaded += (_, _) => Attach();
            tb.Unloaded += (_, _) => Detach();
        }

        private void Attach()
        {
            var layer = AdornerLayer.GetAdornerLayer(_tb);
            if (layer == null || _adorner != null)
                return;
            _adorner = new GhostAdorner(_tb, this);
            layer.Add(_adorner);

            _tb.TextChanged += OnChanged;
            _tb.SelectionChanged += OnChanged;
            _tb.LostFocus += OnChanged;
            _tb.GotKeyboardFocus += OnChanged;
            _tb.PreviewKeyDown += OnPreviewKeyDown;
            _tb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnChanged));
        }

        private void Detach()
        {
            if (_adorner == null)
                return;
            AdornerLayer.GetAdornerLayer(_tb)?.Remove(_adorner);
            _adorner = null;
            _tb.TextChanged -= OnChanged;
            _tb.SelectionChanged -= OnChanged;
            _tb.LostFocus -= OnChanged;
            _tb.GotKeyboardFocus -= OnChanged;
            _tb.PreviewKeyDown -= OnPreviewKeyDown;
            _tb.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnChanged));
        }

        private void OnChanged(object? sender, RoutedEventArgs e) => _adorner?.InvalidateVisual();

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab && !(e.Key == Key.Right && _tb.CaretIndex == _tb.Text.Length))
                return;
            var ghost = CurrentGhost();
            if (string.IsNullOrEmpty(ghost))
                return;
            var caret = _tb.CaretIndex;
            _tb.Text = _tb.Text.Insert(caret, ghost);
            _tb.CaretIndex = caret + ghost.Length;
            e.Handled = true;
        }

        /// <summary>The grey suffix to show/accept, or "" when there is none.</summary>
        public string CurrentGhost()
        {
            if (!_tb.IsKeyboardFocusWithin || _tb.SelectionLength > 0)
                return "";
            var text = _tb.Text;
            if (_tb.CaretIndex != text.Length || text.Length == 0)
                return "";

            var start = text.LastIndexOf(',') + 1;
            var segment = text[start..];
            var lead = segment.Length - segment.TrimStart().Length;
            segment = segment.Trim();
            if (segment.Length == 0)
                return "";
            // Trailing space after the segment means the user is done with it.
            if (lead + segment.Length != text.Length - start)
                return "";

            var already = new HashSet<string>(
                text[..start].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            foreach (var raw in Suggestions?.OfType<string>() ?? Enumerable.Empty<string>())
            {
                var s = raw?.Trim() ?? "";
                if (s.Length <= segment.Length || already.Contains(s))
                    continue;
                if (s.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                    return s[segment.Length..];
            }
            return "";
        }
    }

    private sealed class GhostAdorner : Adorner
    {
        private readonly TextBox _tb;
        private readonly GhostState _state;

        public GhostAdorner(TextBox tb, GhostState state) : base(tb)
        {
            _tb = tb;
            _state = state;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var ghost = _state.CurrentGhost();
            if (string.IsNullOrEmpty(ghost))
                return;

            var ppd = VisualTreeHelper.GetDpi(_tb).PixelsPerDip;
            var typeface = new Typeface(_tb.FontFamily, _tb.FontStyle, _tb.FontWeight, _tb.FontStretch);

            var prefix = new FormattedText(
                _tb.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, _tb.FontSize, Brushes.Black, ppd);
            var suffix = new FormattedText(
                ghost, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, _tb.FontSize, GhostBrush, ppd);

            var x = _tb.Padding.Left + _tb.BorderThickness.Left + prefix.WidthIncludingTrailingWhitespace
                    - _tb.HorizontalOffset + 1;
            var y = (_tb.ActualHeight - suffix.Height) / 2;
            if (x < 0 || x > _tb.ActualWidth)
                return;

            dc.DrawText(suffix, new Point(x, y));
        }

        private static readonly Brush GhostBrush =
            new SolidColorBrush(Color.FromArgb(0x80, 0x60, 0x60, 0x60));
    }
}
