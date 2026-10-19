using System.Globalization;

namespace GameProfileManager.Core;

public sealed record DisplayInfo(
    string Id, string Name, string DeviceName, int Width, int Height,
    uint RefreshNumerator, uint RefreshDenominator, bool IsPrimary,
    string Connection, bool DynamicRefresh, bool? AdvancedColor)
{
    public decimal RefreshHz => RefreshDenominator == 0 ? 0 : (decimal)RefreshNumerator / RefreshDenominator;
    public int? NominalRefreshHz
    {
        get
        {
            decimal rounded = decimal.Round(RefreshHz, 0, MidpointRounding.AwayFromZero);
            return rounded is >= 1 and <= 1000 && Math.Abs(RefreshHz - rounded) <= 0.01m
                ? (int)rounded : null;
        }
    }
    public string SignalRefreshLabel => RefreshHz.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) + " Гц";
    public string RefreshLabel => NominalRefreshHz is int nominal && RefreshHz != nominal
        ? nominal.ToString(CultureInfo.InvariantCulture) + " Гц"
        : SignalRefreshLabel;
    public string RefreshPrecisionNote => NominalRefreshHz is int nominal && RefreshHz != nominal
        ? $"Сигнал: {SignalRefreshLabel}. Для долей используется номинальная частота {nominal} Гц."
        : RefreshHz != decimal.Truncate(RefreshHz)
            ? "Windows может показывать округлённую частоту; здесь указана частота сигнала."
            : "";
    public string ResolutionLabel => $"{Width} × {Height}";
    public string RoleLabel => IsPrimary ? "Основной экран Windows" : "Дополнительный экран";
    public string DynamicRefreshLabel => DynamicRefresh ? "Включена" : "Выключена";
    public string ColorLabel => AdvancedColor switch { true => "Расширенный цвет включён", false => "Расширенный цвет выключен", _ => "Данные о цвете недоступны" };
    public string Description => $"{Name}  ·  {ResolutionLabel}  ·  {RefreshLabel}";
}

public sealed record FpsResolution(int? Fps, decimal? ExactFps, DisplayInfo? Display, string Detail)
{
    public string DisplayText => Fps is null ? "—" : Fps.Value.ToString(CultureInfo.InvariantCulture);
}

public static class DisplayFps
{
    public static int? ExactLimit(DisplayInfo display, int divisor)
    {
        if (display.DynamicRefresh || display.RefreshDenominator == 0 || divisor is < 1 or > 12) return null;
        ulong denominator = (ulong)display.RefreshDenominator * (uint)divisor;
        ulong fps;
        if (display.RefreshNumerator % denominator == 0)
            fps = display.RefreshNumerator / denominator;
        else if (display.NominalRefreshHz is int nominal && nominal % divisor == 0)
            fps = (uint)(nominal / divisor);
        else return null;
        return fps is >= 1 and <= 1000 ? (int)fps : null;
    }

    public static DisplayInfo? ChooseDisplay(AppState state, GameEntry? game, IReadOnlyList<DisplayInfo> displays)
    {
        string id = !string.IsNullOrWhiteSpace(game?.DisplayId) ? game.DisplayId : state.DefaultDisplayId;
        return string.IsNullOrWhiteSpace(id)
            ? displays.FirstOrDefault(display => display.IsPrimary) ?? displays.FirstOrDefault()
            : displays.FirstOrDefault(display => display.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public static FpsResolution Resolve(AppState state, GameEntry? game, PerformanceProfile profile, IReadOnlyList<DisplayInfo> displays)
    {
        if (profile.FpsMode == FpsLimitMode.Manual)
            return profile.FpsLimit is >= 1 and <= 1000
                ? new(profile.FpsLimit, null, null, $"Ручной лимит: {profile.FpsLimit} FPS")
                : new(null, null, null, "Ручной лимит должен быть от 1 до 1000 FPS");
        if (profile.FpsDivisor is < 1 or > 12)
            return new(null, null, null, "Делитель должен быть от 1 до 12");

        var display = ChooseDisplay(state, game, displays);
        if (display is null)
            return new(null, null, null, "Выбранный экран недоступен. Проверьте подключение монитора.");
        if (display.DynamicRefresh)
            return new(null, null, display, "Для доли герцовки выберите фиксированную частоту в Windows: включена динамическая частота.");
        int? exact = ExactLimit(display, profile.FpsDivisor);
        if (exact is null)
            return new(null, display.RefreshHz / profile.FpsDivisor, display,
                $"{display.Name}: {display.RefreshLabel} нельзя разделить на {profile.FpsDivisor} в целое число FPS. Выберите другую долю или ручной лимит.");
        if (display.NominalRefreshHz is int nominal && display.RefreshHz != nominal)
            return new(exact, display.RefreshHz / profile.FpsDivisor, display,
                $"{display.Name}: режим {nominal} Гц (сигнал {display.SignalRefreshLabel}) ÷ {profile.FpsDivisor} = {exact} FPS. Лимит рассчитан по номинальной частоте; сигнал немного отличается.");
        return new(exact, exact, display,
            $"{display.Name}: {display.RefreshLabel} ÷ {profile.FpsDivisor} = {exact} FPS без округления");
    }
}
