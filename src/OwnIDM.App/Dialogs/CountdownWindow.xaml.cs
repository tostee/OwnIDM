using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Scheduling;

namespace OwnIDM.App;

/// <summary>Cancelable countdown before the "when the queue finishes" action.</summary>
public partial class CountdownWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadManager _manager;
    private readonly CompletionAction _action;
    private readonly Action _exitApp;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly int _total;
    private int _remaining;

    public CountdownWindow(DownloadManager manager, CompletionAction action, Action exitApp)
    {
        InitializeComponent();
        _manager = manager;
        _action = action;
        _exitApp = exitApp;
        _total = _remaining = action == CompletionAction.ExitApp ? 15 : 60;
        NowButton.Content = action switch
        {
            CompletionAction.Shutdown => "Apagar ahora",
            CompletionAction.Sleep => "Suspender ahora",
            _ => "Cerrar ahora",
        };
        Bar.Maximum = _total;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        UpdateText();
    }

    private void UpdateText()
    {
        var what = _action switch
        {
            CompletionAction.Shutdown => "el equipo se apagará",
            CompletionAction.Sleep => "el equipo se suspenderá",
            _ => "OwnIDM se cerrará",
        };
        MessageText.Text = $"La cola de descargas ha terminado. Según el programador, {what} en {_remaining} s.";
        Bar.Value = _total - _remaining;
    }

    private void Tick()
    {
        _remaining--;
        UpdateText();
        if (_remaining <= 0)
            Execute();
    }

    private void OnNow(object sender, RoutedEventArgs e) => Execute();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private void Execute()
    {
        _timer.Stop();
        Close();
        // Something started during the countdown: do not cut it off.
        if (_manager.QueueRunning || _manager.Items.Any(i => i.IsActive))
            return;
        _manager.SaveIfDirty(force: true);
        switch (_action)
        {
            case CompletionAction.Shutdown:
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                _exitApp();
                break;
            case CompletionAction.Sleep:
                SetSuspendState(false, false, false);
                break;
            case CompletionAction.ExitApp:
                _exitApp();
                break;
        }
    }
}
