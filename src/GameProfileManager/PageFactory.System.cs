using GameProfileManager.Core;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

internal static partial class PageFactory
{
    private static PageComposition Monitors(RouteRequest request)
    {
        var vm = Vm(request);
        var displayCards = new List<FrameworkElement>();
        foreach (var display in vm.Displays)
        {
            var identity = Column(Strong(display.Name), Secondary(display.IsPrimary
                ? "Основной экран Windows" : "Дополнительный экран"));
            var heading = new Grid { ColumnSpacing = 16 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.Children.Add(new FontIcon { Glyph = "\uE7F4", FontSize = 32,
                VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(identity, 1);
            heading.Children.Add(identity);
            var detail = Column(heading,
                DetailRow("Разрешение", Strong(display.ResolutionLabel)),
                DetailRow("Частота", Strong(display.RefreshLabel)),
                DetailRow("Подключение", Strong(display.Connection)));
            if (!string.IsNullOrWhiteSpace(display.RefreshPrecisionNote))
                detail.Children.Add(Secondary(display.RefreshPrecisionNote));
            displayCards.Add(Card(detail));
        }
        var options = vm.DefaultDisplayOptions.Select(item => new ModeOption<string>(item.Value, item.Label));
        string fpsDisplay = vm.Displays.FirstOrDefault(item => item.Id == vm.DefaultDisplayId)?.Name
            ?? vm.Displays.FirstOrDefault(item => item.IsPrimary)?.Name ?? "Не определён";
        return Shell("Мониторы", "Обнаруженные дисплеи и экран для расчёта FPS",
            ResponsiveCards(
                MetricCard("Подключено экранов", vm.Displays.Count.ToString(), vm.DisplayStatus),
                MetricCard("Основной экран", vm.Displays.FirstOrDefault(item => item.IsPrimary)?.Name ?? "Не определён",
                    "Назначение Windows"),
                MetricCard("Для расчёта FPS", fpsDisplay, "Выбор по умолчанию")),
            Strong("Подключённые дисплеи"),
            displayCards.Count == 0 ? Secondary(vm.DisplayStatus) : ResponsiveTiles(displayCards.ToArray()),
            Card(Field("Экран для расчёта FPS по умолчанию",
                Choice(options, vm.DefaultDisplayId, value => vm.DefaultDisplayId = value))),
            Message("Параметры Windows", "Приложение не меняет разрешение, частоту обновления или HDR."),
            ActionButton("Обновить список", async () => await vm.RunUserActionAsync("Обновляем список экранов…", async () =>
            {
                await vm.RefreshMonitorListAsync();
                request.Owner.Navigate("monitors");
            })));
    }

    private static PageComposition Diagnostics(RouteRequest request)
    {
        var vm = Vm(request);
        bool rtssReady = vm.RtssStatus.StartsWith("RTSS запущен", StringComparison.OrdinalIgnoreCase);
        bool nvidiaReady = vm.NvidiaStatus.Contains("доступен", StringComparison.OrdinalIgnoreCase)
            && !vm.NvidiaStatus.Contains("недоступен", StringComparison.OrdinalIgnoreCase);
        var results = Column(Strong("Требуют внимания"));
        if (vm.AttentionItems.Count == 0) results.Children.Add(Secondary("Проблем не обнаружено. Для актуальной проверки нажмите «Проверить сейчас»."));
        foreach (var item in vm.AttentionItems)
            results.Children.Add(ActionButton($"{item.Category}: {item.Subject} — {item.Title}", () =>
            {
                if (item.Game is not null) vm.SelectedGame = item.Game;
                request.Owner.Navigate("diagnostics-details", item.Game?.Id);
            }));
        return Shell("Диагностика", "Проверка локальных настроек и подтверждённых расхождений",
            ResponsiveCards(
                MetricCard("RTSS", rtssReady ? "Запущен" : "Недоступен", vm.RtssStatus,
                    rtssReady ? "SuccessMetricValueTextStyle" : "CautionMetricValueTextStyle"),
                MetricCard("NVIDIA DRS", nvidiaReady ? "Доступно" : "Недоступно", vm.NvidiaStatus,
                    nvidiaReady ? "SuccessMetricValueTextStyle" : "CautionMetricValueTextStyle"),
                MetricCard("Мониторы", vm.Displays.Count.ToString(), vm.DisplayStatus)),
            Message("Без записи", "Открытие страницы ничего не применяет к RTSS или NVIDIA."),
            Secondary(vm.DiagnosticsStatus), Card(results),
            ActionButton("Проверить сейчас", () => vm.DiagnosticsCommand.Execute(null), true));
    }

    private static PageComposition DiagnosticDetails(RouteRequest request)
    {
        var vm = Vm(request);
        var items = vm.AttentionItems.Where(item => request.ItemId is null || item.Game?.Id == request.ItemId).ToArray();
        var details = Column();
        if (items.Length == 0) details.Children.Add(Secondary("Для выбранной игры нет диагностических записей."));
        foreach (var item in items)
            details.Children.Add(Card(Column(Strong(item.Title), Secondary(item.Category + " · " + item.Subject),
                Secondary(item.Detail), Secondary(item.Advice),
                item.Game is null ? Secondary("") : ActionButton("Открыть игру", () =>
                    request.Owner.Navigate("game", item.Game.Id)))));
        return Shell("Подробности диагностики", "Основание и степень уверенности результатов", details,
            ActionButton("Проверить снова", () => vm.DiagnosticsCommand.Execute(null), true));
    }

    private static PageComposition Settings(RouteRequest request, string activeTab)
    {
        var vm = Vm(request);
        var pcgamingWikiUsername = new TextBox { Text = vm.PcgamingWikiBotUsername,
            PlaceholderText = "Имя пользователя@имя бота", MaxLength = 256 };
        var pcgamingWikiPassword = new PasswordBox { PlaceholderText = "Сгенерированный bot-пароль" };
        var pcgamingWikiCredentialStatus = Secondary(vm.PcgamingWikiCredentialsLabel);
        async Task CheckPcgamingWikiAccessAsync()
        {
            await vm.TestPcgamingWikiAccessAsync();
            pcgamingWikiCredentialStatus.Text = vm.PcgamingWikiCredentialsLabel;
        }
        void OpenPcgamingWikiBotPasswords()
        {
            try { Process.Start(new ProcessStartInfo("https://www.pcgamingwiki.com/wiki/Special:BotPasswords") { UseShellExecute = true }); }
            catch (Exception ex) { vm.Notice = "Не удалось открыть PCGamingWiki: " + ex.Message; }
        }

        var general = Card(Column(
            Strong("Поведение приложения"),
            Toggle("Проверять и повторно применять при запуске", vm.CheckAtStartup, value => vm.CheckAtStartup = value),
            Field("Оформление", Choice(vm.ThemeOptions, vm.Theme, value => vm.Theme = value)),
            Strong("Окно и запуск"),
            Toggle("Сворачивать в трей при закрытии и с геймпада", vm.MinimizeToTray, value => vm.MinimizeToTray = value),
            Toggle("Автозапуск вместе с Windows", vm.AutoStart, value => vm.AutoStart = value),
            Toggle("Запуск с повышением без повторного UAC", vm.SilentAdminLaunch, value => vm.SilentAdminLaunch = value, () => vm.SilentAdminLaunch),
            Strong("Интеграция RTSS"), Secondary(vm.RtssStatus),
            Field("Папка RTSS", Secondary(vm.RtssDirectory)),
            ActionButton("Указать RTSSHooks64.dll", () => vm.BrowseRtssCommand.Execute(null)),
            Field("Общий режим лимитера", Choice(vm.RtssLimiterModes, vm.RtssLimiterMode, value => vm.RtssLimiterMode = value))));

        var pcgamingWiki = Card(Column(Strong("Точный поиск PCGamingWiki"),
            Secondary("Точный поиск читает таблицу GameData через Cargo API. Без bot-пароля остаётся разбор статьи, который может пропустить пути."),
            Strong("Как получить bot-пароль"),
            Secondary("1. Войдите на PCGamingWiki. 2. Откройте Special:BotPasswords и создайте бота. 3. Разрешите Create, query and delete data through the Cargo extension. 4. Скопируйте bot-имя и сгенерированный пароль сюда. Основной пароль аккаунта не используйте."),
            ActionButton("Открыть страницу создания bot-пароля", OpenPcgamingWikiBotPasswords),
            Field("Bot-имя пользователя", pcgamingWikiUsername), Field("Bot-пароль", pcgamingWikiPassword),
            pcgamingWikiCredentialStatus,
            Buttons(
                ActionButton("Сохранить", () =>
                {
                    try
                    {
                        vm.SavePcgamingWikiCredentials(pcgamingWikiUsername.Text, pcgamingWikiPassword.Password);
                        pcgamingWikiPassword.Password = "";
                        pcgamingWikiCredentialStatus.Text = vm.PcgamingWikiCredentialsLabel;
                    }
                    catch (Exception ex) { vm.Notice = "Bot-пароль не сохранён: " + ex.Message; }
                }, true),
                ActionButton("Проверить доступ", () => _ = CheckPcgamingWikiAccessAsync()),
                ActionButton("Удалить сохранённый пароль", () =>
                {
                    try
                    {
                        vm.DeletePcgamingWikiCredentials();
                        pcgamingWikiPassword.Password = "";
                        pcgamingWikiCredentialStatus.Text = vm.PcgamingWikiCredentialsLabel;
                    }
                    catch (Exception ex) { vm.Notice = "Bot-пароль не удалён: " + ex.Message; }
                }, enabled: vm.PcgamingWikiCredentialsConfigured, destructive: true))));

        var tabItems = new (string Id, string Label, string Route)[]
        {
            ("general", "Общие", "settings"),
            ("gamepad", "Геймпад", "settings-controller"),
            ("backups", "Резервные копии и обновления", "settings-backup"),
            ("pcgamingwiki", "Точный поиск PCGamingWiki", "settings-pcgamingwiki")
        };
        var tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var tab in tabItems)
            tabStrip.Children.Add(ActionButton(tab.Label, () => request.Owner.Navigate(tab.Route), tab.Id == activeTab));
        var tabScroller = new ScrollViewer { Content = tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        UIElement content = activeTab switch
        {
            "gamepad" => ControllerSettingsContent(request),
            "backups" => BackupSettingsContent(request),
            "pcgamingwiki" => pcgamingWiki,
            _ => general
        };
        var page = Shell("Настройки", "", tabScroller, content);
        if (page.Content is ScrollViewer { Content: StackPanel layout }) layout.Spacing = 12;
        return page;
    }

    private static UIElement ControllerSettingsContent(RouteRequest request)
    {
        var status = Secondary("");
        var buttons = Secondary("");
        var sections = Secondary("");
        var tray = Secondary("");
        var content = Column(
            Card(Column(Strong("Подключение"), status,
                Secondary("Подключение и отключение контроллера отслеживаются во время работы приложения."))),
            Card(Column(Strong("Управление"), Secondary("Крестовина / левый стик — перемещение фокуса"),
                buttons, Secondary("В раскрытом списке крестовина меняет выбор; кнопка назад закрывает список."), sections, tray)));
        void Refresh()
        {
            status.Text = request.Owner.ControllerStatus;
            buttons.Text = request.Owner.IsPlayStationController
                ? "✕ — выбрать; ○ — назад или закрыть список; □ — ввод / удалить; △ — в трей или свернуть"
                : "A — выбрать; B — назад или закрыть список; X — ввод / удалить; Y — в трей или свернуть";
            sections.Text = request.Owner.IsPlayStationController
                ? "L1 / R1 — основные разделы; правый стик и L2 / R2 — прокрутка"
                : "LB / RB — основные разделы; правый стик и LT / RT — прокрутка";
            tray.Text = request.Owner.IsPlayStationController
                ? "Options + Create — вернуть окно из трея"
                : "Start + View — вернуть окно из трея";
        }
        request.Owner.ControllerStatusChanged += Refresh;
        if (content is FrameworkElement root) root.Unloaded += (_, _) => request.Owner.ControllerStatusChanged -= Refresh;
        Refresh();
        return content;
    }

    private static UIElement BackupSettingsContent(RouteRequest request)
    {
        var vm = Vm(request);
        return Column(
            Card(Column(Strong("Резервная копия настроек GPM"), Secondary("Данные хранятся в " + vm.DataDirectory),
                ActionButton("Экспортировать JSON", () => vm.BackupCommand.Execute(null), true))),
            Card(Column(Strong("Архив графических конфигураций"),
                Secondary("ZIP включает снимки, исходные и защитные копии с проверкой SHA-256. Импорт не меняет файлы игры."),
                Buttons(
                    ActionButton("Экспортировать ZIP", () => _ = vm.ExportGameConfigArchiveAsync(), true),
                    ActionButton("Импортировать ZIP", async () =>
                    {
                        if (vm.PickLocalFileAsync is null) return;
                        string? path = await vm.PickLocalFileAsync("Выберите архив графических конфигураций", "*.zip", vm.DataDirectory);
                        if (path is null) return;
                        if (await Confirm(request.Owner, "Импортировать графические конфигурации",
                            "Метаданные и защитные копии будут объединены с текущей библиотекой. Файлы игры и реестр не изменятся.", "Проверить и импортировать"))
                            await vm.ImportGameConfigArchiveAsync(path);
                    })))),
            Card(Column(Strong("Обновления"), Secondary("Проверка выполняется по нажатию."),
                ActionButton("Проверить обновления", () => _ = request.Owner.CheckUpdatesAsync()))));
    }
}
