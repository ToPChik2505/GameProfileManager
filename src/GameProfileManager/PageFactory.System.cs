using GameProfileManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

internal static partial class PageFactory
{
    private static PageComposition Monitors(RouteRequest request)
    {
        var vm = Vm(request);
        var displays = Column(Strong("Подключённые дисплеи"));
        if (vm.Displays.Count == 0) displays.Children.Add(Secondary(vm.DisplayStatus));
        foreach (var display in vm.Displays)
            displays.Children.Add(Card(Column(Strong(display.Name),
                Secondary(display.ResolutionLabel + " · " + display.RefreshLabel),
                Secondary(display.Connection + (display.IsPrimary ? " · основной экран Windows" : "")),
                Secondary(display.RefreshPrecisionNote))));
        var options = vm.DefaultDisplayOptions.Select(item => new ModeOption<string>(item.Value, item.Label));
        return Shell("Мониторы", "Обнаруженные дисплеи и экран для расчёта FPS",
            ResponsivePair(Card(displays), Card(Column(
                Field("Экран по умолчанию", Choice(options, vm.DefaultDisplayId, value => vm.DefaultDisplayId = value)),
                Secondary("Приложение не меняет разрешение, частоту или HDR Windows.")))),
            ActionButton("Обновить список", async () =>
            {
                await vm.RefreshMonitorListAsync();
                request.Owner.Navigate("monitors");
            }));
    }

    private static PageComposition Diagnostics(RouteRequest request)
    {
        var vm = Vm(request);
        var results = Column(Strong("Требуют внимания"));
        if (vm.AttentionItems.Count == 0) results.Children.Add(Secondary("Проблем не обнаружено. Для актуальной проверки нажмите «Проверить сейчас»."));
        foreach (var item in vm.AttentionItems)
            results.Children.Add(ActionButton($"{item.Category}: {item.Subject} — {item.Title}", () =>
            {
                if (item.Game is not null) vm.SelectedGame = item.Game;
                request.Owner.Navigate("diagnostics-details", item.Game?.Id);
            }));
        return Shell("Диагностика", "Проверка локальных настроек и подтверждённых расхождений",
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
            Strong("Общие"),
            Toggle("Проверять и повторно применять при запуске", vm.CheckAtStartup, value => vm.CheckAtStartup = value),
            Toggle("Сворачивать в трей при закрытии", vm.MinimizeToTray, value => vm.MinimizeToTray = value),
            Toggle("Автозапуск вместе с Windows", vm.AutoStart, value => vm.AutoStart = value),
            Toggle("Запуск с повышением без повторного UAC", vm.SilentAdminLaunch,
                value => vm.SilentAdminLaunch = value),
            Field("Оформление", Choice(vm.ThemeOptions, vm.Theme, value => vm.Theme = value))));
        var integration = Card(Column(
            Strong("Интеграция RTSS"), Secondary(vm.RtssStatus),
            Field("Папка RTSS", Secondary(vm.RtssDirectory)),
            ActionButton("Указать RTSSHooks64.dll", () => vm.BrowseRtssCommand.Execute(null)),
            Field("Общий режим лимитера", Choice(vm.RtssLimiterModes, vm.RtssLimiterMode,
                value => vm.RtssLimiterMode = value))));
        return Shell("Настройки", "Параметры приложения и интеграций",
            ResponsivePair(general, integration),
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
