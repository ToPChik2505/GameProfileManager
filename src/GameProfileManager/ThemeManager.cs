using Microsoft.UI.Xaml;

namespace GameProfileManager;

internal static class ThemeManager
{
    public static void Apply(string theme)
    {
        if (App.MainWindowInstance?.Content is not FrameworkElement root) return;
        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}
