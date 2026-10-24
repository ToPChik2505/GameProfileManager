using GameProfileManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

internal static partial class PageFactory
{
    private static PageComposition Overview(RouteRequest request)
    {
        var vm = Vm(request);
        bool rtssReady = vm.RtssStatus.StartsWith("RTSS запущен", StringComparison.OrdinalIgnoreCase);
        bool nvidiaReady = vm.NvidiaStatus.Contains("доступен", StringComparison.OrdinalIgnoreCase)
            && !vm.NvidiaStatus.Contains("недоступен", StringComparison.OrdinalIgnoreCase);
        int configured = vm.Games.Count(game => game.ProfileId is not null || game.AutoSwitchProfile);
        var metrics = ResponsiveCards(
            MetricCard("RTSS", rtssReady ? "Работает" : "Не запущен", vm.RtssStatus,
                rtssReady ? "SuccessMetricValueTextStyle" : "CautionMetricValueTextStyle"),
            MetricCard("NVIDIA DRS", nvidiaReady ? "Доступно" : "Недоступно", vm.NvidiaStatus,
                nvidiaReady ? "SuccessMetricValueTextStyle" : "CautionMetricValueTextStyle"),
            MetricCard("Основной дисплей", vm.OverviewDisplayTitle, vm.OverviewDisplayRate,
                "AccentMetricValueTextStyle"),
            MetricCard("Настроено игр", configured.ToString(), $"Всего в библиотеке: {vm.GameCount}"),
            MetricCard("Профили", vm.ProfileCount.ToString(), "Стандартные и пользовательские"),
            MetricCard("Требуют внимания", vm.ErrorCount.ToString(),
                vm.ErrorCount == 0 ? "Проверенных проблем нет" : "Откройте диагностику",
                vm.ErrorCount > 0 ? "CautionMetricValueTextStyle" : "MetricValueTextStyle"));
        var recent = Column(Strong("Последние игры"));
        var recentCards = vm.GameCards.OrderByDescending(item => item.Model.LastApplied).Take(4).ToArray();
        if (recentCards.Length == 0)
            recent.Children.Add(Secondary("Добавьте игровой EXE или импортируйте игры из Steam и Epic."));
        foreach (var game in recentCards)
            recent.Children.Add(RecentGameRow(game, () => request.Owner.Navigate("game", game.Id)));
        var actions = Column(Strong("Быстрые действия"));
        foreach (var button in new[]
        {
            ActionButton("Открыть мои игры", () => request.Owner.Navigate("games"), true),
            ActionButton("Создать профиль", () => request.Owner.Navigate("profile-new")),
            ActionButton("Диагностика", () => request.Owner.Navigate("diagnostics")),
            ActionButton("Проверить настройки", () => vm.VerifyCommand.Execute(null))
        })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            actions.Children.Add(button);
        }
        return Shell("Обзор", "Состояние системы и недавние действия", metrics,
            ResponsivePair(Card(recent), Card(actions), 1.8, 1));
    }

    private static Border MetricCard(string title, string value, string detail,
        string valueStyle = "MetricValueTextStyle")
    {
        var heading = new TextBlock
        {
            Text = value, Style = Style(valueStyle),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        };
        var note = Secondary(detail);
        note.MaxLines = 2;
        return Card(Column(Strong(title), heading, note));
    }

    private static Button RecentGameRow(GameProfileManager.Presentation.GameEntryViewModel game, Action open)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(GameIcon(game, 38));
        var name = Column(Strong(game.Name), Secondary(game.ProfileDisplay), Secondary(game.StatusLabel));
        Grid.SetColumn(name, 1);
        content.Children.Add(name);
        var button = new Button { Content = content, Tag = open, MinHeight = 58,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => open();
        return button;
    }

    private static PageComposition Games(RouteRequest request)
    {
        var vm = Vm(request);
        var search = new TextBox { PlaceholderText = "Поиск игр или EXE", Text = vm.Search };
        search.TextChanged += (_, _) => vm.Search = search.Text;
        var list = new ListView
        {
            Name = "GamesList",
            ItemsSource = vm.GamesView,
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Application.Current.Resources["GameRowTemplate"],
            SelectionMode = ListViewSelectionMode.Single,
            ItemContainerStyle = (Style)Microsoft.UI.Xaml.Application.Current.Resources["GameListItemStyle"],
            MinHeight = 180
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not GameProfileManager.Presentation.GameEntryViewModel card) return;
            vm.SelectedGameCard = card;
            request.Owner.Navigate("game", card.Id);
        };
        var headers = new Grid { Name = "GameListHeaders", ColumnSpacing = 16,
            Padding = new Thickness(12, 0, 12, 0) };
        headers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        headers.ColumnDefinitions.Add(new ColumnDefinition());
        headers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        headers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        headers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        foreach (var (title, column) in new[] { ("Игра / EXE", 1), ("Профиль / режим", 2),
                     ("Лимит FPS", 3), ("Состояние", 4) })
        {
            var label = Secondary(title);
            Grid.SetColumn(label, column);
            headers.Children.Add(label);
        }
        var regularTemplate = list.ItemTemplate;
        var compactTemplate = (DataTemplate)Microsoft.UI.Xaml.Application.Current.Resources["CompactGameRowTemplate"];
        void ArrangeList(double width)
        {
            bool compact = width < 760;
            headers.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            var template = compact ? compactTemplate : regularTemplate;
            if (!ReferenceEquals(list.ItemTemplate, template)) list.ItemTemplate = template;
        }
        list.Loaded += (_, _) => ArrangeList(list.ActualWidth);
        list.SizeChanged += (_, args) => ArrangeList(args.NewSize.Width);
        return Shell("Мои игры", "Настройки игр и их профилей, без запуска игр",
            ResponsivePair(Field("Поиск", search), Field("Статус", Choice(
                vm.StatusFilters.Select(item => new ModeOption<string>(item, item)), vm.StatusFilter,
                value => vm.StatusFilter = value)), 2.5, 1),
            Buttons(ActionButton("Добавить игру", () => vm.AddGameCommand.Execute(null), true),
                ActionButton("Импорт Steam / Epic", () => request.Owner.Navigate("import"))),
            Strong($"Игр в библиотеке: {vm.GameCount}"),
            headers,
            list);
    }

    private static PageComposition GameGeneral(RouteRequest request)
    {
        var vm = Vm(request);
        var game = Game(request);
        if (game is null) return Shell("Настройки игры", "Сначала выберите игру в библиотеке.");
        vm.SelectedGame = game;
        var options = vm.ProfileOptions.Select(option => new ModeOption<Guid>(option.Id, option.Name));
        var profile = Choice(options, game.ProfileSelectionId, id => game.ProfileSelectionId = id);
        var displayOptions = vm.GameDisplayOptions.Select(option => new ModeOption<string>(option.Value, option.Label));
        var display = Choice(displayOptions, game.DisplayId, id => game.DisplayId = id);
        var displayAssignments = Column(
            Field("Правило выбора экрана", Choice(vm.DisplayProfilePolicies, game.DisplayProfilePolicy,
                value => game.DisplayProfilePolicy = value)));
        displayAssignments.Name = "DisplayAssignments";
        foreach (var row in vm.Displays)
        {
            Guid selected = game.DisplayProfileAssignments.FirstOrDefault(item => item.DisplayId == row.Id)?.ProfileId ?? Guid.Empty;
            displayAssignments.Children.Add(Field("Профиль: " + row.Name,
                Choice(vm.ProfileOptions.Select(option => new ModeOption<Guid>(option.Id, option.AssignmentName)),
                    selected, id => vm.SetDisplayProfileAssignment(game, row.Id, id == Guid.Empty ? null : id))));
        }
        if (vm.Displays.Count == 0) displayAssignments.Children.Add(Secondary("Мониторы пока не обнаружены."));
        displayAssignments.Visibility = game.AutoSwitchProfile ? Visibility.Visible : Visibility.Collapsed;
        var monitorToggle = Toggle("Профиль по монитору", game.AutoSwitchProfile, value =>
        {
            game.AutoSwitchProfile = value;
            displayAssignments.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        });
        var assignment = Column(
            Field("Основной профиль", profile, "Изменение выбора сохраняет конфигурацию; внешнее применение выполняется отдельно."),
            monitorToggle,
            displayAssignments,
            Field("Экран для расчёта FPS", display));
        System.ComponentModel.PropertyChangedEventHandler selectionChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(GameEntry.ProfileId))
                profile.SelectedItem = profile.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    item.Tag is Guid id && id == game.ProfileSelectionId);
            if (args.PropertyName == nameof(GameEntry.AutoSwitchProfile))
            {
                monitorToggle.IsOn = game.AutoSwitchProfile;
                displayAssignments.Visibility = game.AutoSwitchProfile ? Visibility.Visible : Visibility.Collapsed;
            }
        };
        game.PropertyChanged += selectionChanged;
        assignment.Unloaded += (_, _) => game.PropertyChanged -= selectionChanged;
        var statusText = Strong(game, nameof(GameEntry.StatusLabel));
        statusText.Style = Style("AccentMetricValueTextStyle");
        var actions = Buttons(
            ActionButton("Применить профиль к игре", () => vm.ApplyGameCommand.Execute(game), true),
            ActionButton("Выбрать EXE", () => vm.ChooseExeCommand.Execute(null)),
            ActionButton("Восстановить прежние параметры", async () =>
            {
                if (await Confirm(request.Owner, "Восстановление оригиналов",
                    "Снять профиль игры и восстановить исходные значения RTSS/NVIDIA, если они сохранены?", "Восстановить"))
                    vm.RestoreCommand.Execute(null);
            }));
        return Shell("Настройки игры", "Профиль и правила дисплеев",
            GameHeader(request, game), ResponsivePair(Card(assignment), Card(Column(
                Strong("Текущее состояние"),
                statusText,
                Secondary(game, nameof(GameEntry.Detail)),
                new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") },
                DetailRow("Профиль", Strong(game, nameof(GameEntry.ProfileDisplay))),
                DetailRow("Лимит FPS", Strong(game, nameof(GameEntry.FpsDisplay))),
                DetailRow("Режим NVIDIA", Strong(game, nameof(GameEntry.VrrDisplay))),
                DetailRow("V-Sync", Strong(game, nameof(GameEntry.VSyncDisplay))),
                DetailRow("Лимитер RTSS", Strong(game, nameof(GameEntry.RtssLimiterDisplay))),
                DetailRow("Применено", Strong(game, nameof(GameEntry.LastAppliedDisplay)))))),
            actions);
    }

    private static PageComposition GameFps(RouteRequest request)
    {
        var vm = Vm(request);
        var game = Game(request);
        if (game is null) return Shell("FPS / Лимиты", "Сначала выберите игру.");
        var profile = vm.Profiles.FirstOrDefault(item => item.Id == game.ProfileId);
        string screen = vm.GameDisplayOptions.FirstOrDefault(item => item.Value == game.DisplayId)?.Label
            ?? "Экран по умолчанию";
        return Shell("FPS / Лимиты", "Расчёт для выбранного игрового профиля",
            GameHeader(request, game),
            ResponsivePair(
                Card(Column(Strong("Ограничение FPS"),
                    new TextBlock { Text = game.FpsDisplay, Style = Style("MetricValueTextStyle") },
                    Secondary(profile is null ? "Назначьте профиль игры." :
                        profile.FpsMode == FpsLimitMode.Manual ? "Лимит задан вручную." :
                        $"Доля частоты экрана: 1/{profile.FpsDivisor}."),
                    DetailRow("Экран", Strong(screen)))),
                Card(Column(Strong("Режим лимитера RTSS"),
                    new TextBlock { Text = game.RtssLimiterDisplay, Style = Style("SubtitleTextBlockStyle") },
                    Secondary(vm.RtssStatus),
                    Secondary("Изменение общего профиля затронет все назначенные ему игры.")))),
            Buttons(ActionButton("Редактировать профиль", () => request.Owner.Navigate("profile-edit", profile?.Id),
                    enabled: profile is { IsBuiltIn: false }),
                ActionButton("Применить профиль к игре", () => vm.ApplyGameCommand.Execute(game), true)));
    }

    private static PageComposition GameNvidia(RouteRequest request)
    {
        var vm = Vm(request);
        var game = Game(request);
        if (game is null) return Shell("NVIDIA", "Сначала выберите игру.");
        return Shell("NVIDIA", "Параметры NVIDIA DRS для выбранного EXE",
            GameHeader(request, game),
            ResponsivePair(Card(Column(Strong("Режим монитора"),
                    Strong(game, nameof(GameEntry.VrrDisplay)),
                    Secondary("Выбранный режим сохраняется в профиле NVIDIA DRS этого EXE."))),
                Card(Column(Strong("Вертикальная синхронизация"),
                    Strong(game, nameof(GameEntry.VSyncDisplay)),
                    Secondary("Настройка применяется вместе с остальными параметрами профиля игры.")))),
            Card(Column(Strong("Состояние игры"), DetailRow("Статус", Strong(game, nameof(GameEntry.StatusLabel))),
                DetailRow("Профиль", Strong(game, nameof(GameEntry.ProfileDisplay))), Secondary(game, nameof(GameEntry.Detail)))),
            Message("Проверка в игре", "Повторное чтение DRS подтверждает сохранённое значение, но не действие внутри уже запущенной игры.", InfoBarSeverity.Warning),
            Buttons(ActionButton("Восстановить прежние параметры", async () =>
            {
                if (await Confirm(request.Owner, "Восстановить NVIDIA и RTSS",
                    "Снять профиль игры и восстановить исходные значения RTSS/NVIDIA, если они сохранены?", "Восстановить"))
                    vm.RestoreCommand.Execute(null);
            }),
                ActionButton("Применить профиль к игре", () => vm.ApplyGameCommand.Execute(game), true)));
    }

    private static PageComposition GameRtss(RouteRequest request)
    {
        var vm = Vm(request);
        var game = Game(request);
        if (game is null) return Shell("RTSS", "Сначала выберите игру.");
        return Shell("RTSS", "Лимит и режим RTSS для выбранного EXE",
            GameHeader(request, game),
            ResponsivePair(Card(Column(Strong("Лимит FPS"),
                    new TextBlock { Text = game.FpsDisplay, Style = Style("MetricValueTextStyle") },
                    Secondary("Статус игры: " + game.StatusLabel))),
                Card(Column(Strong("Режим ограничения"),
                    new TextBlock { Text = game.RtssLimiterDisplay, Style = Style("SubtitleTextBlockStyle") },
                    Secondary(vm.RtssStatus)))),
            Card(Column(Strong("Профиль игры"), DetailRow("Назначение", Strong(game.ProfileDisplay)),
                DetailRow("Последнее применение", Strong(game.LastApplied?.ToLocalTime().ToString("g") ?? "Ещё не применялось")))),
            Message("Применение", "Запись в RTSS и NVIDIA выполняется вместе через безопасную очередь применения."),
            Buttons(ActionButton("Проверить сейчас", () => vm.VerifyCommand.Execute(null)),
                ActionButton("Применить профиль к игре", () => vm.ApplyGameCommand.Execute(game), true)));
    }

    private static PageComposition Graphics(RouteRequest request)
    {
        var game = Game(request);
        return Shell("Графические конфигурации", "Планируемая подсистема снимков игровых файлов",
            game is null ? Secondary("Сначала выберите игру.") : GameHeader(request, game),
            Message("Функция недоступна", "Поиск, сохранение и применение снимков графических файлов пока не реализованы.",
                InfoBarSeverity.Warning),
            Buttons(ActionButton("Новый снимок", () => request.Owner.Navigate("graphics-new", game?.Id), enabled: false),
                ActionButton("Выбрать файлы", () => request.Owner.Navigate("graphics-files", game?.Id), enabled: false)));
    }

    private static PageComposition GraphicsUnavailable(RouteRequest request) => Shell(
        request.Route == "graphics-new" ? "Новый снимок графики" : "Выбор файлов конфигурации",
        "Этот сценарий будет доступен после реализации безопасной работы с игровыми файлами.",
        Message("Функция недоступна", "GameProfileManager не читает и не меняет графические файлы игры."));

    private static PageComposition Profiles(RouteRequest request)
    {
        var vm = Vm(request);
        var list = new ListView
        {
            ItemsSource = vm.Profiles,
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Application.Current.Resources["ProfileRowTemplate"],
            SelectionMode = ListViewSelectionMode.Single, MinHeight = 180,
            SelectedItem = vm.SelectedProfile
        };
        var regularTemplate = list.ItemTemplate;
        var compactTemplate = (DataTemplate)Microsoft.UI.Xaml.Application.Current.Resources["CompactProfileRowTemplate"];
        void ArrangeList(double width)
        {
            var template = width < 400 ? compactTemplate : regularTemplate;
            if (!ReferenceEquals(list.ItemTemplate, template)) list.ItemTemplate = template;
        }
        list.Loaded += (_, _) => ArrangeList(list.ActualWidth);
        list.SizeChanged += (_, args) => ArrangeList(args.NewSize.Width);
        var selectedName = new TextBlock { Style = Style("SubtitleTextBlockStyle") };
        var selectedDescription = Secondary("");
        var selectedFps = Strong("");
        var selectedVrr = Strong("");
        var selectedVsync = Strong("");
        var selectedRtss = Strong("");
        var selectedUse = Strong("");
        var edit = ActionButton("Редактировать", () => request.Owner.Navigate("profile-edit", vm.SelectedProfile?.Id));
        var copy = ActionButton("Создать копию", () => request.Owner.Navigate("profile-new", vm.SelectedProfile?.Id));
        var delete = ActionButton("Удалить профиль", async () =>
        {
            if (await Confirm(request.Owner, "Удалить профиль",
                "Профиль будет удалён, если он не назначен ни одной игре.", "Удалить"))
                vm.RemoveProfileCommand.Execute(null);
        }, destructive: true);
        void UpdateSelection()
        {
            var profile = vm.SelectedProfile;
            selectedName.Text = profile?.Name ?? "Выберите профиль";
            selectedDescription.Text = profile is null ? "Выберите профиль слева, чтобы посмотреть его параметры."
                : string.IsNullOrWhiteSpace(profile.Description) ? "Описание не добавлено" : profile.Description;
            selectedFps.Text = profile?.FpsPreview ?? "";
            selectedVrr.Text = profile?.VrrDisplay ?? "—";
            selectedVsync.Text = profile is null ? "—" : ProfileLabels.VSync(profile.VSync);
            selectedRtss.Text = profile is null ? "—" : profile.RtssLimiterMode is { } mode
                ? ProfileLabels.Rtss(mode) : "Наследует: " + ProfileLabels.Rtss(vm.RtssLimiterMode);
            selectedUse.Text = profile is null ? "—" : vm.Games.Count(game =>
                game.ProfileId == profile.Id || game.DisplayProfileAssignments.Any(item => item.ProfileId == profile.Id))
                .ToString();
            edit.IsEnabled = delete.IsEnabled = profile is { IsBuiltIn: false };
            copy.IsEnabled = profile is not null;
        }
        list.SelectionChanged += (_, _) => { vm.SelectedProfile = list.SelectedItem as PerformanceProfile; UpdateSelection(); };
        UpdateSelection();
        return Shell("Профили", "Готовые и пользовательские профили производительности",
            ResponsivePair(Card(Column(Strong("Профили"), list)), Card(Column(
                Strong("Выбранный профиль"), selectedName, selectedDescription,
                new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") },
                DetailRow("Лимит FPS", selectedFps),
                DetailRow("Режим NVIDIA", selectedVrr), DetailRow("V-Sync", selectedVsync),
                DetailRow("Лимитер RTSS", selectedRtss), DetailRow("Назначен играм", selectedUse))), 1, 1.5),
            Buttons(ActionButton("Создать профиль", () => request.Owner.Navigate("profile-new"), true),
                edit, copy, delete));
    }

    private static PageComposition Import(RouteRequest request)
    {
        var vm = Vm(request);
        return Shell("Импорт игр", "Поиск установленных игр без их запуска",
            Message("Существующий импорт", "Импорт выполняется для найденных игр целиком. Выбор отдельных записей из макета пока недоступен."),
            ResponsivePair(Card(Column(Strong("Steam"), Secondary("Найти установленные игры Steam и добавить их в библиотеку."),
                    ActionButton("Импортировать Steam", () => vm.ImportSteamCommand.Execute(null), true))),
                Card(Column(Strong("Epic Games Store"), Secondary("Найти установленные игры Epic и добавить их в библиотеку."),
                    ActionButton("Импортировать Epic", () => vm.ImportEpicCommand.Execute(null))))));
    }
}
