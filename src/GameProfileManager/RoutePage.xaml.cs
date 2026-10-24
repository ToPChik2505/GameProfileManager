using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace GameProfileManager;

public sealed partial class RoutePage : Page
{
    private Func<Task<bool>>? _canLeave;
    private RouteRequest? _request;
    private RoutedEventHandler? _loadedHandler;
    public string Route { get; private set; } = "";
    internal RouteRequest? Request => _request;

    public RoutePage() => InitializeComponent();

    public Task<bool> CanLeaveAsync() => _canLeave?.Invoke() ?? Task.FromResult(true);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not RouteRequest request) return;
        _request = request;
        Route = request.Route;
        request.Owner.SetCurrentRoute(request.Route);
        long startedAt = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        var composition = PageFactory.Build(request);
        var build = NavigationDiagnostics.MeasureBuild(startedAt, allocatedBefore,
            gen0Before, gen1Before, gen2Before);
        PageRoot.Children.Clear();
        PageRoot.Children.Add(composition.Content);
        _canLeave = composition.CanLeave;
        var probe = request.Owner.CurrentNavigationProbe;
        _loadedHandler = (_, _) =>
        {
            Loaded -= _loadedHandler;
            _loadedHandler = null;
            if (probe is not null) request.Owner.CompleteNavigation(probe, this, build);
            if (request.Owner.TryGetPageState(request, out var state))
                DispatcherQueue.TryEnqueue(() => RestoreViewState(request, state));
        };
        Loaded += _loadedHandler;
    }

    internal RouteViewState CaptureViewState()
    {
        var scroll = PageRoot.Children.OfType<ScrollViewer>().FirstOrDefault();
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        Control[] controls = focused is null ? [] : MainWindow.Descendants(PageRoot).OfType<Control>()
            .Where(control => control.GetType() == focused.GetType()).ToArray();
        int ordinal = focused is null ? -1 : Array.IndexOf(controls, focused);
        return new RouteViewState(scroll?.HorizontalOffset ?? 0, scroll?.VerticalOffset ?? 0,
            ordinal >= 0 ? focused?.Name : null,
            ordinal >= 0 ? focused?.GetType().FullName : null, ordinal);
    }

    private void RestoreViewState(RouteRequest request, RouteViewState state)
    {
        if (!IsLoaded || !ReferenceEquals(_request, request)) return;
        var scroll = PageRoot.Children.OfType<ScrollViewer>().FirstOrDefault();
        scroll?.ChangeView(state.HorizontalOffset, state.VerticalOffset, null, true);
        if (state.FocusOrdinal < 0 || state.FocusType is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !ReferenceEquals(_request, request)) return;
            var matches = MainWindow.Descendants(PageRoot).OfType<Control>()
                .Where(control => control.GetType().FullName == state.FocusType).ToArray();
            var target = matches.FirstOrDefault(control =>
                !string.IsNullOrEmpty(state.FocusName) && control.Name == state.FocusName)
                ?? matches.ElementAtOrDefault(state.FocusOrdinal);
            if (target is { IsEnabled: true, Visibility: Visibility.Visible })
                target.Focus(FocusState.Programmatic);
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_loadedHandler is not null) Loaded -= _loadedHandler;
        _loadedHandler = null;
        _canLeave = null;
        _request = null;
        PageRoot.Children.Clear();
        base.OnNavigatedFrom(e);
    }
}
