using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

/// <summary>Hides a transient InfoBar after it has been visible for the requested interval.</summary>
internal sealed class InfoBarDismissTimer
{
    private readonly InfoBar _bar;
    private readonly DispatcherTimer _timer;

    public InfoBarDismissTimer(InfoBar bar, TimeSpan delay)
    {
        _bar = bar;
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) => Hide();
        _bar.Loaded += OnLoaded;
        _bar.Unloaded += OnUnloaded;
    }

    public void Show(TimeSpan? delay = null)
    {
        _bar.IsOpen = true;
        _timer.Stop();
        if (delay is { } interval) _timer.Interval = interval;
        if (_bar.IsLoaded) _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        _bar.IsOpen = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_bar.IsOpen) _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => _timer.Stop();
}
