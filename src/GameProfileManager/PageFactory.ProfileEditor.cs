using GameProfileManager.Core;
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
        if (editing && source!.IsBuiltIn) return Shell("Редактирование профиля", "Стандартный профиль защищён. Создайте его копию.");

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
        var fpsMode = Choice(vm.FpsModes, draft.FpsMode, value => { draft.FpsMode = value; dirty = true; });
        var fraction = Choice(Enumerable.Range(1, 12).Select(value =>
            new ModeOption<int>(value, $"1/{value}")), draft.FpsDivisor,
            value => { draft.FpsDivisor = value; dirty = true; });
        var vrr = Choice(vm.VrrModes, draft.Vrr, value => { draft.Vrr = value; dirty = true; });
        var vsync = Choice(vm.VSyncModes, draft.VSync, value => { draft.VSync = value; dirty = true; });
        var limiter = Choice(vm.ProfileRtssLimiterModes,
            draft.RtssLimiterMode is { } mode ? (int)mode : -1,
            value => { draft.RtssLimiterMode = value < 0 ? null : (RtssLimiterMode)value; dirty = true; });

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

        var content = Shell(editing ? "Редактирование профиля" : "Создание профиля",
            editing ? "Изменения сохранятся после нажатия кнопки «Сохранить профиль»." :
                "Создайте отдельный профиль для игровых EXE.",
            ResponsivePair(
                Card(Column(Strong("Основные данные"),
                    Field("Название", name), Field("Описание", description),
                    Field("Режим FPS", fpsMode), Field("Лимит FPS", fps))),
                Card(Column(Strong("Дисплей и синхронизация"),
                    Field("Доля частоты экрана", fraction),
                    Field("Режим монитора NVIDIA", vrr), Field("V-Sync", vsync),
                    Field("Режим лимитера RTSS", limiter)))),
            Message("Отдельное применение", "Сохранение профиля не записывает настройки RTSS/NVIDIA. Примените профиль к назначенной игре."),
            Buttons(ActionButton("Отмена", () => request.Owner.Navigate("profiles")),
                ActionButton("Сохранить профиль", () =>
                {
                    if (Save()) request.Owner.Navigate("profiles");
                }, true)));

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
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => Save(),
                ContentDialogResult.Secondary => true,
                _ => false
            };
        }
        return new PageComposition(content.Content, CanLeave);
    }
}
