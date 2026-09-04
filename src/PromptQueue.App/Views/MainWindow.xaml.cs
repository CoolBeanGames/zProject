using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PromptQueue.App.Controls;
using PromptQueue.App.ViewModels;
using PromptQueue.Core.Models;
using WinForms = System.Windows.Forms;

namespace PromptQueue.App.Views;

/// <summary>Interaction logic for MainWindow.xaml.</summary>
public partial class MainWindow : Window
{
    // Press bookkeeping (before a drag actually starts)
    private Point _pressPoint;
    private ListBoxItem? _pressContainer;
    private TaskItem? _pressTask;

    // Active drag state
    private bool _dragging;
    private ListBoxItem? _dragContainer;
    private TaskItem? _dragTask;
    private RowDragAdorner? _ghost;
    private AdornerLayer? _adornerLayer;
    private double _grabOffsetY;
    private double _gapHeight;
    private int _insertIndex = -1;
    // Intended (target) vertical offset per row, so the insert-index maths reads
    // rest positions and never the mid-flight animated transform value (ZP-64).
    private readonly Dictionary<ListBoxItem, double> _targetOffset = new();

    private bool _allowClose;
    private bool _wasMinimized;
    private MainViewModel? _hookedVm;
    private WinForms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SetupTray();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ---- Close = hide to the notification area (system tray), ZP-44 ----

    private void SetupTray()
    {
        if (_tray != null)
            return;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Close", null, (_, _) => { _allowClose = true; Close(); });

        _tray = new WinForms.NotifyIcon
        {
            Text = "zProject",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
            return new System.Drawing.Icon(stream);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Vm?.ReloadAllProjects();   // pick up any changes made while hidden (ZP-20)
    }

    /// <summary>
    /// A second zProject launch was attempted (ZP-55): surface this existing
    /// window instead of starting another process.
    /// </summary>
    public void RestoreFromExternalRequest() => RestoreFromTray();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hookedVm != null)
            _hookedVm.ExitRequested -= OnExitRequested;
        _hookedVm = Vm;
        if (_hookedVm != null)
            _hookedVm.ExitRequested += OnExitRequested;
    }

    private void OnExitRequested()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Hide to the tray instead of exiting; the taskbar button goes away.
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized)
        {
            if (_wasMinimized)
                Vm?.ReloadAllProjects();
            _wasMinimized = false;
        }
        else
        {
            _wasMinimized = true;
        }
    }

    // ---- Press / start ------------------------------------------------

    private void TaskItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem container)
            return;
        _pressPoint = e.GetPosition(TaskList);
        _pressContainer = container;
        _pressTask = container.DataContext as TaskItem;
    }

    private void TaskItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _pressTask == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var now = e.GetPosition(TaskList);
        if (Math.Abs(now.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(now.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        BeginDrag(now);
    }

    private void BeginDrag(Point start)
    {
        if (_pressContainer == null || _pressTask == null || Vm?.SelectedProject == null)
            return;

        _dragging = true;
        _dragContainer = _pressContainer;
        _dragTask = _pressTask;

        var topLeft = _dragContainer.TranslatePoint(new Point(0, 0), TaskList);
        _grabOffsetY = start.Y - topLeft.Y;
        _gapHeight = _dragContainer.ActualHeight + 2;

        _adornerLayer = AdornerLayer.GetAdornerLayer(TaskList);
        if (_adornerLayer != null)
        {
            var w = _dragContainer.ActualWidth;
            var h = _dragContainer.ActualHeight;
            var snapshot = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(w)), Math.Max(1, (int)Math.Ceiling(h)),
                96, 96, PixelFormats.Pbgra32);
            snapshot.Render(_dragContainer);
            _ghost = new RowDragAdorner(TaskList, snapshot, w, h, topLeft.X);
            _adornerLayer.Add(_ghost);
            _ghost.UpdatePosition(topLeft.Y);
        }

        // Smoothly collapse the dragged row (height + fade) so the remaining rows
        // close up gradually instead of snapping (ZP-64).
        CollapseRow(_dragContainer);

        Mouse.Capture(TaskList);
        TaskList.MouseMove += TaskList_DragMove;
        TaskList.PreviewMouseLeftButtonUp += TaskList_DragEnd;
        TaskList.LostMouseCapture += TaskList_DragCancelled;

        UpdateDrag(start.Y);
    }

    // ---- Move --------------------------------------------------------

    private void TaskList_DragMove(object sender, MouseEventArgs e)
        => UpdateDrag(e.GetPosition(TaskList).Y);

    private void UpdateDrag(double pointerY)
    {
        _ghost?.UpdatePosition(pointerY - _grabOffsetY);

        var others = VisibleContainers();
        int insert = others.Count;
        for (int i = 0; i < others.Count; i++)
        {
            var c = others[i];
            var mid = c.TranslatePoint(new Point(0, c.ActualHeight / 2), TaskList).Y;
            // Read the row's REST position: subtract whatever offset we have asked
            // it to hold, not the mid-flight animated transform value. This keeps
            // the insert index stable and kills the back-and-forth jitter (ZP-64).
            mid -= _targetOffset.TryGetValue(c, out var off) ? off : 0;
            if (pointerY < mid)
            {
                insert = i;
                break;
            }
        }

        if (insert == _insertIndex)
            return;
        _insertIndex = insert;

        for (int i = 0; i < others.Count; i++)
        {
            double to = i >= _insertIndex ? _gapHeight : 0;
            _targetOffset[others[i]] = to;
            AnimateTranslateY(others[i], to);
        }
    }

    // ---- End / cancel ----------------------------------------------

    private void TaskList_DragEnd(object sender, MouseButtonEventArgs e) => EndDrag(commit: true);

    private void TaskList_DragCancelled(object sender, MouseEventArgs e) => EndDrag(commit: false);

    private void EndDrag(bool commit)
    {
        if (!_dragging)
            return;
        _dragging = false;

        TaskList.MouseMove -= TaskList_DragMove;
        TaskList.PreviewMouseLeftButtonUp -= TaskList_DragEnd;
        TaskList.LostMouseCapture -= TaskList_DragCancelled;
        if (Mouse.Captured == TaskList)
            Mouse.Capture(null);

        if (_ghost != null && _adornerLayer != null)
            _adornerLayer.Remove(_ghost);
        _ghost = null;
        _adornerLayer = null;

        var others = VisibleContainers();

        foreach (var c in AllContainers())
        {
            if (c.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, null);
            c.RenderTransform = null;
            if (!ReferenceEquals(c, _dragContainer))
                c.Visibility = Visibility.Visible;
        }
        _targetOffset.Clear();
        if (_dragContainer != null)
            RestoreRow(_dragContainer);

        var task = _dragTask;
        var target = _insertIndex;
        _dragTask = null;
        _dragContainer = null;
        _pressTask = null;
        _pressContainer = null;
        _insertIndex = -1;

        if (commit && task != null && target >= 0)
        {
            var targetBefore = target < others.Count ? (others[target].DataContext as TaskItem) : null;
            var targetAfter = target > 0 ? (others[target - 1].DataContext as TaskItem) : null;
            Vm?.MoveTaskRelative(task, targetBefore, targetAfter);
        }
    }

    // ---- helpers --------------------------------------------------

    private List<ListBoxItem> AllContainers()
    {
        var list = new List<ListBoxItem>();
        CollectContainers(TaskList, list);
        return list;
    }

    private static void CollectContainers(DependencyObject parent, List<ListBoxItem> list)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListBoxItem lbi)
                list.Add(lbi);
            else
                CollectContainers(child, list);
        }
    }

    private List<ListBoxItem> VisibleContainers()
    {
        var dragBranch = (_dragTask?.Branch ?? "main").Trim();
        return AllContainers()
            .Where(c => c.Visibility == Visibility.Visible && !ReferenceEquals(c, _dragContainer)
                && c.DataContext is TaskItem t
                && string.Equals(string.IsNullOrWhiteSpace(t.Branch) ? "main" : t.Branch,
                                 string.IsNullOrWhiteSpace(dragBranch) ? "main" : dragBranch,
                                 StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ---- source-row collapse / restore (ZP-64) --------------------

    private static void CollapseRow(ListBoxItem row)
    {
        var h = row.ActualHeight;
        if (h <= 0)
        {
            row.Visibility = Visibility.Collapsed;
            return;
        }
        row.Tag = "collapsing";
        var dur = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var height = new DoubleAnimation(h, 0, dur) { EasingFunction = ease };
        height.Completed += (_, _) =>
        {
            if (Equals(row.Tag, "collapsing"))
                row.Visibility = Visibility.Collapsed;
        };
        row.BeginAnimation(FrameworkElement.HeightProperty, height);
        row.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90)));
    }

    private static void RestoreRow(ListBoxItem row)
    {
        row.Tag = null;
        row.BeginAnimation(FrameworkElement.HeightProperty, null);
        row.BeginAnimation(UIElement.OpacityProperty, null);
        row.Height = double.NaN;
        row.Opacity = 1;
        row.Visibility = Visibility.Visible;
    }

    private static void AnimateTranslateY(UIElement element, double to)
    {
        if (element.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            element.RenderTransform = tt;
        }
        // Compare against the animation target, not the mid-flight value, so a
        // rapid change of direction still re-targets the row (ZP-64).
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        tt.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    // ---- Double-click ANYWHERE on a card opens the full view (ZP-45) ----

    private void TaskItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
            return;
        if (sender is not ListBoxItem { DataContext: TaskItem task })
            return;

        // Let a double-click that lands on an actual control (Edit/Delete/lock
        // buttons, checkboxes, the tags box) do its own thing instead.
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;

        if (Vm?.EditTaskCommand.CanExecute(task) == true)
        {
            Vm.EditTaskCommand.Execute(task);
            e.Handled = true;
        }
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase
                  or System.Windows.Controls.Primitives.TextBoxBase
                  or ComboBox)
                return true;
            if (d is ListBoxItem)
                return false;
        }
        return false;
    }

    // ---- Done checkbox toggled directly on the row ------------------

    private void TaskDone_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TaskItem task })
            Vm?.PersistTaskFlagChange(task);
    }

    // ---- Flag checkbox (bug/commit/build/release) toggled on an expanded card (ZP-75) ----

    private void TaskFlag_Click(object sender, RoutedEventArgs e)
    {
        // Click fires only on real user interaction, never on binding init, so no
        // spurious operator writes while cards are materialised.
        if (sender is CheckBox { DataContext: TaskItem task, Tag: string field } cb)
            Vm?.PersistTaskFieldChange(task, field, cb.IsChecked == true);
    }

    // ---- Inline subtask checkbox toggled on a task row -----------

    private void Subtask_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d)
            return;
        var container = ItemsControl.ContainerFromElement(TaskList, d) as ListBoxItem;
        if (container?.DataContext is TaskItem task
            && (sender as FrameworkElement)?.DataContext is Subtask sub)
            Vm?.PersistSubtaskChange(task, sub);
    }

    // ---- Quick add: Space starts it, Enter adds, Esc closes (ZP-57) ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;

        // ZP-58: Escape while editing a task closes the editor, applying changes.
        if (e.Key == Key.Escape && Vm?.Overlay is TaskFormViewModel form)
        {
            if (form.SaveCommand.CanExecute(null))
                form.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Space)
            return;
        if (Vm == null || Vm.HasOverlay || Vm.QuickAddActive)
            return;
        if (!Vm.BeginQuickAddCommand.CanExecute(null))
            return;

        // Don't hijack the space bar while the user is typing somewhere.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        if (Keyboard.FocusedElement is ComboBox { IsEditable: true })
            return;

        Vm.BeginQuickAddCommand.Execute(null);
        e.Handled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            QuickAddBox.Focus();
            QuickAddBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void QuickAdd_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null)
            return;

        if (e.Key == Key.Enter)
        {
            Vm.CommitQuickAddCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.CancelQuickAddCommand.Execute(null);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ---- Enter in the "new subtask" box adds it -------------------

    private void NewSubtask_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if ((sender as FrameworkElement)?.DataContext is TaskFormViewModel form &&
            form.AddSubtaskCommand.CanExecute(null))
        {
            form.AddSubtaskCommand.Execute(null);
            e.Handled = true;
        }
    }
}
