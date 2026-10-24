using GameProfileManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace GameProfileManager;

internal sealed record PageComposition(UIElement Content, Func<Task<bool>>? CanLeave = null);

internal static partial class PageFactory
{
    public static PageComposition Build(RouteRequest request) => request.Route switch
    {
        "overview" => Overview(request),
        "games" => Games(request),
        "game" => GameGeneral(request),
        "game-fps" => GameFps(request),
        "game-nvidia" => GameNvidia(request),
        "game-rtss" => GameRtss(request),
        "game-graphics" => Graphics(request),
        "graphics-new" or "graphics-files" => GraphicsUnavailable(request),
        "profiles" => Profiles(request),
        "profile-new" or "profile-edit" => ProfileEditor(request),
        "monitors" => Monitors(request),
        "diagnostics" => Diagnostics(request),
        "diagnostics-details" => DiagnosticDetails(request),
        "settings" => Settings(request),
        "settings-controller" => ControllerSettings(request),
        "settings-backup" => BackupSettings(request),
        "import" => Import(request),
        _ => Shell("Раздел недоступен", "Неизвестный маршрут.")
    };

    private static MainViewModel Vm(RouteRequest request) => request.Owner.ViewModel;

    private static PageComposition Shell(string title, string subtitle, params UIElement[] items)
    {
        var column = new StackPanel { Spacing = 20, Padding = new Thickness(24, 20, 24, 32),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        column.SizeChanged += (_, args) =>
        {
            double width = args.NewSize.Width;
            double side = width >= 1200 ? 32 : width >= 930 ? 24 : 20;
            column.Padding = new Thickness(side, 20, side, 32);
        };
        column.Children.Add(new TextBlock { Text = title, Style = Style("TitleTextBlockStyle") });
        if (!string.IsNullOrWhiteSpace(subtitle)) column.Children.Add(Secondary(subtitle));
        foreach (var item in items) column.Children.Add(item);
        return new PageComposition(new ScrollViewer { Content = column });
    }

    private static Style Style(string name) => (Style)Microsoft.UI.Xaml.Application.Current.Resources[name];

    private static Brush? Brush(string name) => Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(name, out object value)
        ? value as Brush : null;

    private static StackPanel Column(params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    private static TextBlock Secondary(string value) => new()
    {
        Text = value, Style = Style("BodyTextBlockStyle"),
        Opacity = 0.7, TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Strong(string value) => new()
    {
        Text = value, Style = Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Strong(GameProfileManager.Core.GameEntry game, string property)
    {
        var text = Strong("");
        text.SetBinding(TextBlock.TextProperty, new Binding { Source = game, Path = new PropertyPath(property) });
        return text;
    }

    private static TextBlock Secondary(GameProfileManager.Core.GameEntry game, string property)
    {
        var text = Secondary("");
        text.SetBinding(TextBlock.TextProperty, new Binding { Source = game, Path = new PropertyPath(property) });
        return text;
    }

    private static Border Card(UIElement content) => new()
    {
        Background = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(20), Child = content
    };

    private static Button ActionButton(string label, Action action, bool accent = false, bool enabled = true,
        bool destructive = false)
    {
        var button = new Button { Content = label, Tag = action, IsEnabled = enabled, MinHeight = 40 };
        if (accent) button.Style = Style("AccentButtonStyle");
        if (destructive)
        {
            button.Foreground = Brush("SystemFillColorCriticalBrush");
            button.BorderBrush = Brush("SystemFillColorCriticalBrush");
            button.BorderThickness = new Thickness(1);
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static Grid Buttons(params Button[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        foreach (var button in buttons)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(button);
        }
        void Arrange(double width)
        {
            bool horizontal = width >= 900;
            for (int i = 0; i < buttons.Length; i++)
            {
                grid.ColumnDefinitions[i].Width = horizontal ? GridLength.Auto
                    : i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                Grid.SetColumn(buttons[i], horizontal ? i : 0);
                Grid.SetRow(buttons[i], horizontal ? 0 : i);
                buttons[i].HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            }
        }
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    private static UIElement ResponsivePair(FrameworkElement first, FrameworkElement second,
        double firstWeight = 1, double secondWeight = 1)
    {
        var grid = new Grid { Name = "ResponsivePair", ColumnSpacing = 16, RowSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(firstWeight, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(secondWeight, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        grid.Children.Add(first);
        grid.Children.Add(second);
        Grid.SetColumn(second, 1);
        void Arrange(double width)
        {
            bool wide = width >= 930;
            grid.ColumnDefinitions[1].Width = wide ? new GridLength(secondWeight, GridUnitType.Star) : new GridLength(0);
            grid.RowDefinitions[1].Height = wide ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(second, wide ? 1 : 0);
            Grid.SetRow(second, wide ? 0 : 1);
        }
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    private static UIElement ResponsiveCards(params FrameworkElement[] cards)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cards.Length; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var card in cards) grid.Children.Add(card);
        void Arrange(double width)
        {
            int columns = width >= 1050 ? 3 : width >= 650 ? 2 : 1;
            grid.ColumnDefinitions[1].Width = columns >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            grid.ColumnDefinitions[2].Width = columns == 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            for (int i = 0; i < cards.Length; i++)
            {
                Grid.SetColumn(cards[i], i % columns);
                Grid.SetRow(cards[i], i / columns);
            }
        }
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    private static UIElement ResponsiveTiles(params FrameworkElement[] cards)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cards.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(cards[i]);
        }
        void Arrange(double width)
        {
            bool wide = width >= 930;
            grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            for (int i = 0; i < cards.Length; i++)
            {
                Grid.SetColumn(cards[i], wide ? i % 2 : 0);
                Grid.SetRow(cards[i], wide ? i / 2 : i);
            }
        }
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    private static ComboBox Choice<T>(IEnumerable<ModeOption<T>> options, T selected, Action<T> changed)
    {
        var combo = new ComboBox { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in options)
            combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            EqualityComparer<T>.Default.Equals((T)item.Tag, selected));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is T value) changed(value);
        };
        return combo;
    }

    private static FrameworkElement Field(string label, UIElement editor, string? help = null)
    {
        var column = Column(Strong(label), editor);
        if (help is not null) column.Children.Add(Secondary(help));
        return column;
    }

    private static UIElement DetailRow(string label, TextBlock value)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4, MinHeight = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        grid.Children.Add(Secondary(label));
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        void Arrange(double width)
        {
            bool narrow = width < 380;
            grid.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(150);
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(value, narrow ? 0 : 1);
            Grid.SetRow(value, narrow ? 1 : 0);
        }
        grid.Loaded += (_, _) => Arrange(grid.ActualWidth);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    private static ToggleSwitch Toggle(string label, bool value, Action<bool> changed, Func<bool>? readBack = null)
    {
        var toggle = new ToggleSwitch { Header = label, IsOn = value };
        bool reflecting = false;
        toggle.Toggled += (_, _) =>
        {
            if (reflecting) return;
            changed(toggle.IsOn);
            if (readBack is null) return;
            bool actual = readBack();
            if (toggle.IsOn == actual) return;
            reflecting = true;
            try { toggle.IsOn = actual; }
            finally { reflecting = false; }
        };
        return toggle;
    }

    private static UIElement Message(string title, string detail, InfoBarSeverity severity = InfoBarSeverity.Informational)
        => new InfoBar { Title = title, Message = detail, Severity = severity, IsOpen = true, IsClosable = false };

    private static GameEntry? Game(RouteRequest request)
    {
        var vm = Vm(request);
        var game = vm.Games.FirstOrDefault(item => item.Id == request.ItemId) ?? vm.SelectedGame;
        if (game is not null) vm.SelectedGame = game;
        return game;
    }

    private static UIElement GameTabs(RouteRequest request, GameEntry game)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, route) in new[] { ("Общие", "game"), ("FPS / Лимит", "game-fps"),
                     ("NVIDIA", "game-nvidia"), ("RTSS", "game-rtss"), ("Графические файлы", "game-graphics") })
            row.Children.Add(ActionButton(label, () => request.Owner.Navigate(route, game.Id), route == request.Route));
        return new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static FrameworkElement GameIcon(GameProfileManager.Presentation.GameEntryViewModel? card, double size)
    {
        var content = new Grid();
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE7F1", FontSize = size * 0.43,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        if (card is not null)
        {
            var image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill };
            image.SetBinding(Image.SourceProperty, new Binding { Source = card, Path = new PropertyPath("Icon") });
            content.Children.Add(image);
        }
        return new Border { Width = size, Height = size, Child = content,
            Background = Brush("ControlFillColorDefaultBrush"), CornerRadius = new CornerRadius(9) };
    }

    private static UIElement GameHeader(RouteRequest request, GameEntry game)
    {
        var card = Vm(request).GameCards.FirstOrDefault(item => item.Id == game.Id);
        var row = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        row.Children.Add(GameIcon(card, 56));
        var identity = Column(Strong(game, nameof(GameEntry.Name)), Secondary(game, nameof(GameEntry.ExeDisplay)));
        Grid.SetColumn(identity, 1);
        row.Children.Add(identity);
        var state = Column(Strong(game, nameof(GameEntry.StatusLabel)), Secondary(game, nameof(GameEntry.ProfileDisplay)));
        state.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(state, 2);
        row.Children.Add(state);
        void Arrange(double width)
        {
            bool narrow = width < 600;
            row.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : GridLength.Auto;
            row.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(0);
            state.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Grid.SetColumn(state, narrow ? 1 : 2);
            Grid.SetRow(state, narrow ? 1 : 0);
        }
        row.Loaded += (_, _) => Arrange(row.ActualWidth);
        row.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return Column(Card(row), GameTabs(request, game));
    }

    private static async Task<bool> Confirm(MainWindow owner, string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.Content.XamlRoot, Title = title, Content = message,
            PrimaryButtonText = action, CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close
        };
        return await owner.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    internal static Task<bool> ConfirmUpdate(MainWindow owner, string version) => Confirm(owner,
        "Обновление Game Profile Manager", $"Доступна версия {version}. Открыть страницу обновления?", "Открыть");
}
