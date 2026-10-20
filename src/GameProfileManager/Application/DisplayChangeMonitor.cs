using GameProfileManager.Core;

namespace GameProfileManager.Application;

/// <summary>
/// Подтверждает изменение экрана двумя одинаковыми наблюдениями, чтобы
/// промежуточная конфигурация Windows не запускала внешнюю запись профилей.
/// </summary>
public sealed class DisplayChangeMonitor
{
    private string _current = "";
    private string _candidate = "";
    private int _candidateObservations;

    public void Reset(IReadOnlyList<DisplayInfo> displays)
    {
        _current = Fingerprint(displays);
        _candidate = "";
        _candidateObservations = 0;
    }

    public bool Observe(IReadOnlyList<DisplayInfo> displays)
    {
        string fingerprint = Fingerprint(displays);
        if (fingerprint == _current)
        {
            _candidate = "";
            _candidateObservations = 0;
            return false;
        }
        if (fingerprint != _candidate)
        {
            _candidate = fingerprint;
            _candidateObservations = 1;
            return false;
        }
        if (++_candidateObservations < 2) return false;
        _current = fingerprint;
        _candidate = "";
        _candidateObservations = 0;
        return true;
    }

    private static string Fingerprint(IEnumerable<DisplayInfo> displays) => string.Join("|",
        displays.OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase).Select(display =>
            $"{display.Id}:{display.Name}:{display.Width}:{display.Height}:" +
            $"{display.RefreshNumerator}/{display.RefreshDenominator}:{display.IsPrimary}:" +
            $"{display.DynamicRefresh}:{display.AdvancedColor}:{display.Connection}"));
}
