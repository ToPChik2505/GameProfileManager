using GameProfileManager.Core;
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
            ActionButton("Обновить список", async () =>
            {
                await vm.RefreshMonitorListAsync();
                request.Owner.Navigate("monitors");
            }));
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

    private static PageComposition Settings(RouteRequest request)
    {
        var vm = Vm(request);
        var general = Card(Column(
            Strong("Поведение приложения"),
            Toggle("Проверять и повторно применять при запуске", vm.CheckAtStartup, value => vm.CheckAtStartup = value),
            Field("Оформление", Choice(vm.ThemeOptions, vm.Theme, value => vm.Theme = value))));
        var startup = Card(Column(
            Strong("Окно и запуск"),
            Toggle("Сворачивать в трей при закрытии", vm.MinimizeToTray, value => vm.MinimizeToTray = value),
            Toggle("Автозапуск вместе с Windows", vm.AutoStart, value => vm.AutoStart = value),
            Toggle("Запуск с повышением без повторного UAC", vm.SilentAdminLaunch,
                value => vm.SilentAdminLaunch = value)));
        var integration = Card(Column(
            Strong("Интеграция RTSS"), Secondary(vm.RtssStatus),
            Field("Папка RTSS", Secondary(vm.RtssDirectory)),
            ActionButton("Указать RTSSHooks64.dll", () => vm.BrowseRtssCommand.Execute(null)),
            Field("Общий режим лимитера", Choice(vm.RtssLimiterModes, vm.RtssLimiterMode,
                value => vm.RtssLimiterMode = value))));
        return Shell("Настройки", "Параметры приложения и интеграций",
            ResponsiveCards(general, startup, integration),
            Buttons(ActionButton("Контроллер SDL3", () => request.Owner.Navigate("settings-controller")),
                ActionButton("Резервные копии и обновления", () => request.Owner.Navigate("settings-backup"))));
    }

    private static PageComposition ControllerSettings(RouteRequest request)
    {
        return Shell("Контроллер SDL3", "Подключение и навигация геймпадом",
            Card(Column(Strong("Состояние"), Secondary(request.Owner.ControllerStatus),
                Secondary("Подключение и отключение контроллера отслеживаются во время работы приложения."))),
            Card(Column(Strong("Управление"), Secondary("Крестовина / левый стик — перемещение фокуса"),
                Secondary("A / ✕ — выбрать; B / ○ — назад"),
                Secondary("LB / RB — основные разделы; правый стик и LT / RT — прокрутка"),
                Secondary("Start + View / Options + Create — вернуть окно из трея"))));
    }

    private static PageComposition BackupSettings(RouteRequest request)
    {
        var vm = Vm(request);
        return Shell("Резервные копии и обновления", "Сохранность пользовательских данных",
            ResponsivePair(
                Card(Column(Strong("Резервная копия"),
                    Secondary("Данные хранятся в " + vm.DataDirectory),
                    ActionButton("Экспортировать JSON", () => vm.BackupCommand.Execute(null), true))),
                Card(Column(Strong("Обновления"),
                    Secondary("Проверка выполняется только по нажатию."),
                    ActionButton("Проверить обновления", () => _ = request.Owner.CheckUpdatesAsync())))));
    }
}
