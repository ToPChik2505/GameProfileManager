using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

/// <summary>Closes a transient InfoBar after the requested interval.</summary>
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
        _bar.Unloaded += (_, _) => _timer.Stop();
        _bar.Closed += (_, _) => _timer.Stop();
    }

    public void Show(TimeSpan? delay = null)
    {
        _timer.Stop();
        if (delay is { } interval) _timer.Interval = interval;
        _bar.IsOpen = true;
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
}
