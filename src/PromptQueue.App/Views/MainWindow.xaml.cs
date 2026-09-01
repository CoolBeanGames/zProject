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

        // Collapse the dragged row so the remaining rows form a continuous list.
        _dragContainer.Visibility = Visibility.Collapsed;

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
            // Discount any gap shift already applied so the midpoint reflects the rest position.
            if (GetTranslateY(c) > 0)
                mid -= _gapHeight;
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
            AnimateTranslateY(others[i], i >= _insertIndex ? _gapHeight : 0);
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

        foreach (var c in AllContainers())
        {
            c.RenderTransform = null;
            c.Visibility = Visibility.Visible;
        }

        var task = _dragTask;
        var target = _insertIndex;
        _dragTask = null;
        _dragContainer = null;
        _pressTask = null;
        _pressContainer = null;
        _insertIndex = -1;

        if (commit && task != null && target >= 0)
            Vm?.MoveTaskToIndex(task, target);
    }

    // ---- helpers --------------------------------------------------

    private List<ListBoxItem> AllContainers()
    {
        // Iterate items (not indices): with grouping the top-level generator
        // yields GroupItems, but ContainerFromItem still resolves the row.
        var list = new List<ListBoxItem>();
        foreach (var item in TaskList.Items)
        {
            if (TaskList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c)
                list.Add(c);
        }
        return list;
    }

    private List<ListBoxItem> VisibleContainers()
        => AllContainers().Where(c => c.Visibility == Visibility.Visible).ToList();

    private static double GetTranslateY(UIElement e)
        => (e.RenderTransform as TranslateTransform)?.Y ?? 0;

    private static void AnimateTranslateY(UIElement element, double to)
    {
        if (element.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            element.RenderTransform = tt;
        }
        if (Math.Abs(tt.Y - to) < 0.5)
            return;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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

    // ---- Inline subtask checkbox toggled on a task row -----------

    private void Subtask_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject d)
            return;
        var container = ItemsControl.ContainerFromElement(TaskList, d) as ListBoxItem;
        if (container?.DataContext is TaskItem task)
            Vm?.PersistTaskFlagChange(task);
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
