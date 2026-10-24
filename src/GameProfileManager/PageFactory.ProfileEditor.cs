using GameProfileManager.Core;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

internal static partial class PageFactory
{
    private static PageComposition ProfileEditor(RouteRequest request)
    {
        var vm = Vm(request);
        bool editing = request.Route == "profile-edit";
        PerformanceProfile? source = request.ItemId is Guid id
            ? vm.Profiles.FirstOrDefault(item => item.Id == id) : null;
        if (editing && source is null) return Shell("Редактирование профиля", "Профиль не найден.");
        var draft = source is null ? new PerformanceProfile() : ProfilePresets.EditableCopy(source);
        if (!editing && source is not null) draft.Name = source.Name + " — копия";
        if (editing) draft.Name = source!.Name;
        string initialName = draft.Name;
        string initialDescription = draft.Description;
        bool dirty = false;
        bool saved = false;
        bool fpsValid = true;

        var name = new TextBox { Text = draft.Name, PlaceholderText = "Название профиля",
            HorizontalAlignment = HorizontalAlignment.Stretch };
        name.TextChanged += (_, _) => { draft.Name = name.Text; dirty = true; };
        var description = new TextBox { Text = draft.Description, PlaceholderText = "Описание",
            HorizontalAlignment = HorizontalAlignment.Stretch };
        description.TextChanged += (_, _) => { draft.Description = description.Text; dirty = true; };
        var fps = new NumberBox { Value = draft.FpsLimit, Minimum = 1, Maximum = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        fps.ValueChanged += (_, _) =>
        {
            fpsValid = !double.IsNaN(fps.Value) && fps.Value is >= 1 and <= 1000;
            if (fpsValid) draft.FpsLimit = (int)fps.Value;
            dirty = true;
        };
        FrameworkElement manualFpsField = new StackPanel();
        FrameworkElement fractionField = new StackPanel();
        var fpsModeHint = Secondary("");
        var fractionPreview = new StackPanel { Spacing = 8 };
        var fpsMode = Choice(vm.FpsModes, draft.FpsMode, value =>
        {
            draft.FpsMode = value;
            dirty = true;
            UpdateFpsModePresentation();
        });
        var fraction = Choice(Enumerable.Range(1, 12).Select(value =>
            new ModeOption<int>(value, $"1/{value}")), draft.FpsDivisor,
            value => { draft.FpsDivisor = value; dirty = true; UpdateFractionPreview(); });
        manualFpsField = Field("Целевой лимит FPS", fps,
            "Значение будет одинаковым независимо от частоты экрана.");
        fractionField = Field("Доля частоты экрана", fraction,
            "GPM рассчитает FPS по эффективному дисплею игры.");

        var syncHint = Secondary("");
        var vrr = Choice(vm.VrrModes, draft.Vrr, value =>
        {
            draft.Vrr = value;
            dirty = true;
            UpdateSyncHint();
        });
        var vsync = Choice(vm.VSyncModes, draft.VSync, value =>
        {
            draft.VSync = value;
            dirty = true;
            UpdateSyncHint();
        });
        var limiterHint = Secondary("");
        var limiter = Choice(vm.ProfileRtssLimiterModes,
            draft.RtssLimiterMode is { } mode ? (int)mode : -1,
            value =>
            {
                draft.RtssLimiterMode = value < 0 ? null : (RtssLimiterMode)value;
                dirty = true;
                UpdateLimiterHint();
            });

        void UpdateFpsModePresentation()
        {
            bool usesFraction = draft.FpsMode == FpsLimitMode.DisplayFraction;
            manualFpsField.Visibility = usesFraction ? Visibility.Collapsed : Visibility.Visible;
            fractionField.Visibility = usesFraction ? Visibility.Visible : Visibility.Collapsed;
            fractionPreview.Visibility = usesFraction ? Visibility.Visible : Visibility.Collapsed;
            fpsModeHint.Text = usesFraction
                ? "Целевой FPS будет зависеть от частоты эффективного дисплея. Расчёт для каждого подключённого экрана показан ниже."
                : "Профиль будет применять заданный лимит FPS для игры.";
            UpdateFractionPreview();
        }

        void UpdateFractionPreview()
        {
            fractionPreview.Children.Clear();
            fractionPreview.Children.Add(Strong("Расчёт FPS по экранам"));
            if (vm.Displays.Count == 0)
            {
                fractionPreview.Children.Add(Secondary(vm.DisplayStatus));
                return;
            }

            foreach (var display in vm.Displays.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Name))
            {
                int? limit = DisplayFps.ExactLimit(display, draft.FpsDivisor);
                string result;
                string detail;
                if (display.DynamicRefresh)
                {
                    result = "Нет фиксированного FPS";
                    detail = $"{display.Name} · {display.RefreshLabel} · включена динамическая частота";
                }
                else if (limit is int fpsLimit)
                {
                    result = $"{fpsLimit} FPS";
                    detail = $"{display.Name} · {display.RefreshLabel} ÷ {draft.FpsDivisor}";
                }
                else
                {
                    result = "Нет целого значения FPS";
                    detail = $"{display.Name} · {display.RefreshLabel} нельзя точно разделить на {draft.FpsDivisor}";
                }

                var row = new Grid { ColumnSpacing = 16, MinHeight = 40 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Secondary(detail));
                var value = Strong(result);
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                fractionPreview.Children.Add(row);
            }
        }

        void UpdateSyncHint()
        {
            string vsyncLabel = vm.VSyncModes.FirstOrDefault(item => item.Value == draft.VSync)?.Label ?? ProfileLabels.VSync(draft.VSync);
            syncHint.Text = draft.Vrr == VrrMode.GSync
                ? $"G-SYNC включён. V-Sync: {vsyncLabel}. Оба параметра применяются вместе через профиль NVIDIA."
                : $"Выбрана фиксированная частота. V-Sync: {vsyncLabel}; параметр задаётся в профиле NVIDIA.";
        }

        void UpdateLimiterHint()
        {
            limiterHint.Text = draft.RtssLimiterMode is null
                ? $"Используется общий режим RTSS: {ProfileLabels.Rtss(vm.RtssLimiterMode)}. Изменение общего режима затронет профили с наследованием."
                : $"Для этого профиля задан отдельный режим RTSS: {ProfileLabels.Rtss(draft.RtssLimiterMode.Value)}.";
        }

        bool Save()
        {
            draft.Name = name.Text;
            draft.Description = description.Text;
            if (!fpsValid)
            {
                vm.Notice = "Ошибка: введите FPS от 1 до 1000.";
                return false;
            }
            if (!vm.SaveProfileDraft(editing ? source!.Id : null, draft, out string error))
            {
                vm.Notice = "Ошибка: " + error;
                return false;
            }
            saved = true;
            return true;
        }

        var profileDetails = Card(Column(Strong("Основные данные"),
            Field("Название", name), Field("Описание", description)));
        var fpsSettings = Card(Column(Strong("Ограничение FPS"),
            Field("Способ ограничения", fpsMode), fpsModeHint,
            manualFpsField, fractionField, fractionPreview));
        var syncSettings = Card(Column(Strong("Синхронизация NVIDIA"),
            Field("Режим дисплея", vrr), Field("V-Sync", vsync), syncHint));
        var rtssSettings = Card(Column(Strong("Лимитер RTSS"),
            Field("Режим RTSS для профиля", limiter), limiterHint));

        UpdateFpsModePresentation();
        UpdateSyncHint();
        UpdateLimiterHint();

        void DisplaysChanged(object? sender, NotifyCollectionChangedEventArgs args) => UpdateFractionPreview();
        void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.DisplayStatus)) UpdateFractionPreview();
        }

        vm.Displays.CollectionChanged += DisplaysChanged;
        vm.PropertyChanged += ViewModelChanged;
        var content = Shell(editing ? "Редактирование профиля" : "Создание профиля",
            editing ? "Настройте связанные параметры; доступны только поля выбранного режима FPS." :
                "Создайте профиль для игры. Режим FPS управляет отображением связанных параметров.",
            ResponsivePair(profileDetails, fpsSettings),
            ResponsivePair(syncSettings, rtssSettings),
            Message("Отдельное применение", "Сохранение профиля не записывает настройки RTSS/NVIDIA. Примените профиль к назначенной игре."),
            Buttons(ActionButton("Отмена", () => request.Owner.Navigate("profiles")),
                ActionButton("Сохранить профиль", () =>
                {
                    if (Save()) request.Owner.Navigate("profiles");
                }, true)));
        if (content.Content is FrameworkElement root)
        {
            root.Unloaded += (_, _) =>
            {
                vm.Displays.CollectionChanged -= DisplaysChanged;
                vm.PropertyChanged -= ViewModelChanged;
            };
        }

        async Task<bool> CanLeave()
        {
            if (saved || !dirty && name.Text == initialName && description.Text == initialDescription) return true;
            var dialog = new ContentDialog
            {
                XamlRoot = request.Owner.Content.XamlRoot,
                Title = "Несохранённые изменения",
                Content = "Сохранить изменения профиля перед переходом?",
                PrimaryButtonText = "Сохранить",
                SecondaryButtonText = "Не сохранять",
                CloseButtonText = "Остаться",
                DefaultButton = ContentDialogButton.Close
            };
            return await request.Owner.ShowDialogAsync(dialog) switch
            {
                ContentDialogResult.Primary => Save(),
                ContentDialogResult.Secondary => true,
                _ => false
            };
        }
        return new PageComposition(content.Content, CanLeave);
    }
}
