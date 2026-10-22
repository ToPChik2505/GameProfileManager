using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameProfileManager;

public sealed partial class RoutePage : Page
{
    private Func<Task<bool>>? _canLeave;
    public string Route { get; private set; } = "";

    public RoutePage() => InitializeComponent();

    public Task<bool> CanLeaveAsync() => _canLeave?.Invoke() ?? Task.FromResult(true);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not RouteRequest request) return;
        Route = request.Route;
        request.Owner.SetCurrentRoute(request.Route);
        var composition = PageFactory.Build(request);
        PageRoot.Children.Clear();
        PageRoot.Children.Add(composition.Content);
        _canLeave = composition.CanLeave;
    }
}
