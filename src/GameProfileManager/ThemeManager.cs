using System.Windows;
using System.Windows.Media;

namespace GameProfileManager;

internal static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Colors =
        new Dictionary<string, (string, string)>
        {
            ["BackgroundBrush"] = ("#F3F5F9", "#101419"),
            ["SidebarBrush"] = ("#FFFFFF", "#161C22"),
            ["SurfaceBrush"] = ("#FFFFFF", "#1B232B"),
            ["RaisedBrush"] = ("#F7F9FC", "#242F38"),
            ["StrokeBrush"] = ("#DDE4EC", "#36434D"),
            ["TextBrush"] = ("#172438", "#F3F6F5"),
            ["MutedBrush"] = ("#65758A", "#AAB9C0"),
            ["AccentBrush"] = ("#365FCB", "#64DCC6"),
            ["AccentForegroundBrush"] = ("#FFFFFF", "#10312B"),
            ["AccentHoverBrush"] = ("#284FB9", "#87ECD9"),
            ["AccentPressedBrush"] = ("#20439F", "#45C8B0"),
            ["HoverBrush"] = ("#EBF1FC", "#2B3B41"),
            ["SelectedBrush"] = ("#E5EDFF", "#25473F"),
            ["DangerBrush"] = ("#AE4051", "#FF9B9F"),
            ["DangerSurfaceBrush"] = ("#FFF0F2", "#38282F"),
            ["DangerBorderBrush"] = ("#F3C9D0", "#695057"),
            ["WarningSurfaceBrush"] = ("#FFF5E6", "#392F24"),
            ["WarningBorderBrush"] = ("#EAD1A4", "#715D44"),
            ["WarningTextBrush"] = ("#694513", "#F3D4A2"),
            ["SuccessSurfaceBrush"] = ("#E8F6ED", "#20443B"),
            ["SuccessTextBrush"] = ("#247348", "#7CE4B0"),
            ["NeutralSurfaceBrush"] = ("#EAF0F6", "#2A343D"),
            ["ScrollThumbBrush"] = ("#BCC8D7", "#5A6F78"),
            ["HeroBrush"] = ("#EAF0FF", "#1D3635")
        };

    public static void Apply(string theme)
    {
        if (System.Windows.Application.Current is not { } app) return;
        bool light = theme == "Light";
        foreach (var (key, pair) in Colors)
        {
            // Brush не изменяется после публикации; Freeze сокращает расходы WPF на отслеживание изменений.
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? pair.Light : pair.Dark));
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }
}
