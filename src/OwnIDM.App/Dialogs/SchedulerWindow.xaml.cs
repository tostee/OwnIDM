using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Scheduling;

namespace OwnIDM.App;

public partial class SchedulerWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly (DayOfWeek Day, string Label)[] WeekDays =
    [
        (DayOfWeek.Monday, "L"), (DayOfWeek.Tuesday, "M"), (DayOfWeek.Wednesday, "X"), (DayOfWeek.Thursday, "J"),
        (DayOfWeek.Friday, "V"), (DayOfWeek.Saturday, "S"), (DayOfWeek.Sunday, "D"),
    ];

    private static readonly (CompletionAction Action, string Label)[] Completions =
    [
        (CompletionAction.None, "No hacer nada"),
        (CompletionAction.ExitApp, "Cerrar OwnIDM"),
        (CompletionAction.Sleep, "Suspender el equipo"),
        (CompletionAction.Shutdown, "Apagar el equipo"),
    ];

    private readonly DownloadManager _manager;
    private readonly Dictionary<DayOfWeek, CheckBox> _dayBoxes = new();
    private readonly ListCollectionView _queue;
    private bool _loading = true;

    public SchedulerWindow(DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;

        foreach (var (day, label) in WeekDays)
        {
            var box = new CheckBox { Content = label, Margin = new Thickness(0, 0, 10, 0), ToolTip = App.Spanish.DateTimeFormat.GetDayName(day) };
            box.Checked += OnChanged;
            box.Unchecked += OnChanged;
            _dayBoxes[day] = box;
            DaysPanel.Children.Add(box);
        }
        CompletionBox.ItemsSource = Completions.Select(c => c.Label).ToList();

        var s = manager.Settings.Schedule;
        StartEnabledBox.IsChecked = s.StartEnabled;
        StartTimeBox.Text = s.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        DailyRadio.IsChecked = s.Daily;
        OnceRadio.IsChecked = !s.Daily;
        foreach (var (day, box) in _dayBoxes)
            box.IsChecked = s.Days.Contains(day);
        OnceDatePicker.SelectedDate = s.OnceDate < DateOnly.FromDateTime(DateTime.Today)
            ? DateTime.Today : s.OnceDate.ToDateTime(TimeOnly.MinValue);
        StopEnabledBox.IsChecked = s.StopEnabled;
        StopTimeBox.Text = s.StopTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        ConcurrentBox.Text = manager.Settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        CompletionBox.SelectedIndex = Array.FindIndex(Completions, c => c.Action == s.OnComplete);

        // A separate view: the main window's default view is sorted by date.
        _queue = new ListCollectionView(manager.Items) { Filter = o => o is DownloadItem { InQueue: true } };
        _queue.IsLiveFiltering = true;
        _queue.LiveFilteringProperties.Add(nameof(DownloadItem.InQueue));
        QueueList.ItemsSource = _queue;

        manager.QueueStateChanged += UpdateButtons;
        Closed += (_, _) =>
        {
            manager.QueueStateChanged -= UpdateButtons;
            _queue.DetachFromSourceCollection();
        };
        _loading = false;
        OnChanged(this, new RoutedEventArgs());
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        StartNowButton.IsEnabled = !_manager.QueueRunning;
        StopButton.IsEnabled = _manager.QueueRunning;
    }

    private static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>Builds a schedule from the controls; null (with a message) when something is invalid.</summary>
    private QueueSchedule? ReadSchedule(out string? error)
    {
        error = null;
        var current = _manager.Settings.Schedule;
        if (!TryTime(StartTimeBox.Text, out var start))
            error = "La hora de inicio debe tener el formato HH:mm (por ejemplo 02:30).";
        else if (!TryTime(StopTimeBox.Text, out var stop))
            error = "La hora de parada debe tener el formato HH:mm.";
        else
        {
            var schedule = new QueueSchedule
            {
                StartEnabled = StartEnabledBox.IsChecked == true,
                StartTime = start,
                Daily = DailyRadio.IsChecked == true,
                Days = _dayBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList(),
                OnceDate = DateOnly.FromDateTime(OnceDatePicker.SelectedDate ?? DateTime.Today),
                StopEnabled = StopEnabledBox.IsChecked == true,
                StopTime = stop,
                OnComplete = CompletionBox.SelectedIndex >= 0 ? Completions[CompletionBox.SelectedIndex].Action : CompletionAction.None,
                LastStartFired = current.LastStartFired,
                LastStopFired = current.LastStopFired,
            };
            if (schedule.StartEnabled && schedule.Daily && schedule.Days.Count == 0)
                error = "Marca al menos un día de la semana.";
            else
                return schedule;
        }
        return null;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        StartOptions.IsEnabled = StartEnabledBox.IsChecked == true;
        StartTimeBox.IsEnabled = StartEnabledBox.IsChecked == true;
        StopTimeBox.IsEnabled = StopEnabledBox.IsChecked == true;
        DaysPanel.IsEnabled = DailyRadio.IsChecked == true;
        OnceDatePicker.IsEnabled = OnceRadio.IsChecked == true;

        var schedule = ReadSchedule(out var error);
        ValidationText.Text = error;
        ValidationText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        var next = schedule is null ? null : QueueScheduler.NextStart(schedule, DateTime.Now);
        NextStartText.Text = next is { } n
            ? "Próximo inicio: " + n.ToString("dddd d 'de' MMMM, HH:mm", App.Spanish)
            : "Sin inicio programado";
    }

    private bool Apply()
    {
        var schedule = ReadSchedule(out var error);
        if (schedule is null || !int.TryParse(ConcurrentBox.Text, out int concurrent) || concurrent is < 1 or > 20)
        {
            MessageBox.Show(this, error ?? "Las descargas simultáneas deben estar entre 1 y 20.", "Programador",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (schedule.OnComplete == CompletionAction.Shutdown && _manager.Settings.Schedule.OnComplete != CompletionAction.Shutdown)
        {
            var ok = MessageBox.Show(this, "Cuando la cola termine, el equipo se apagará tras una cuenta atrás de 60 segundos " +
                                           "que podrás cancelar. ¿Continuar?", "Programador", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK)
                return false;
        }
        _manager.Settings.Schedule = schedule;
        _manager.Settings.MaxConcurrentDownloads = concurrent;
        _manager.SaveSettings();
        return true;
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Apply())
            Close();
    }

    private void OnStartNow(object sender, RoutedEventArgs e)
    {
        if (Apply())
            _manager.StartQueue();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _manager.StopQueue();

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int direction)
    {
        if (QueueList.SelectedItem is not DownloadItem item)
            return;
        _manager.MoveInQueue(item, direction);
        _queue.Refresh();
        QueueList.SelectedItem = item;
    }

    private void OnRemoveFromQueue(object sender, RoutedEventArgs e)
    {
        foreach (var item in QueueList.SelectedItems.OfType<DownloadItem>().ToList())
            _manager.SetInQueue(item, false);
    }
}
