using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GameProfileManager;

public enum ControllerAction
{
    Up, Down, Left, Right, Accept, Back, PreviousPage, NextPage,
    PageUp, PageDown, Secondary, Tertiary, Menu, ToggleWindow
}

public enum ControllerButton
{
    South = 0, East = 1, West = 2, North = 3, Back = 4, Start = 6,
    LeftShoulder = 9, RightShoulder = 10,
    DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14
}

public readonly record struct ControllerSnapshot(ulong Buttons, short LeftX = 0, short LeftY = 0,
    short RightY = 0, short LeftTrigger = 0, short RightTrigger = 0)
{
    public bool IsDown(ControllerButton button) => (Buttons & (1UL << (int)button)) != 0;
}

/// <summary>Maps SDL's positional gamepad controls to UI actions, with repeat only for navigation.</summary>
public sealed class ControllerActionMapper
{
    private readonly Dictionary<ControllerAction, long> _nextRepeat = new();
    private readonly HashSet<ControllerAction> _held = new();
    private long _toggleStart = -1;
    private bool _toggleSent;

    public IReadOnlyList<ControllerAction> Update(ControllerSnapshot state, long milliseconds)
    {
        var actions = new List<ControllerAction>(4);
        bool up = state.IsDown(ControllerButton.DpadUp) || state.LeftY < -16000;
        bool down = state.IsDown(ControllerButton.DpadDown) || state.LeftY > 16000;
        bool left = state.IsDown(ControllerButton.DpadLeft) || state.LeftX < -16000;
        bool right = state.IsDown(ControllerButton.DpadRight) || state.LeftX > 16000;
        Repeat(ControllerAction.Up, up && !down, milliseconds, actions);
        Repeat(ControllerAction.Down, down && !up, milliseconds, actions);
        Repeat(ControllerAction.Left, left && !right, milliseconds, actions);
        Repeat(ControllerAction.Right, right && !left, milliseconds, actions);
        Repeat(ControllerAction.PageUp, state.LeftTrigger > 24000 || state.RightY < -20000, milliseconds, actions);
        Repeat(ControllerAction.PageDown, state.RightTrigger > 24000 || state.RightY > 20000, milliseconds, actions);
        Edge(ControllerAction.Accept, state.IsDown(ControllerButton.South), actions);
        bool toggleChord = state.IsDown(ControllerButton.Start) && state.IsDown(ControllerButton.Back);
        if (toggleChord)
        {
            if (_toggleStart < 0) _toggleStart = milliseconds;
            if (!_toggleSent && milliseconds - _toggleStart >= 700)
            {
                actions.Add(ControllerAction.ToggleWindow);
                _toggleSent = true;
            }
        }
        else
        {
            _toggleStart = -1;
            _toggleSent = false;
        }
        Edge(ControllerAction.Back, state.IsDown(ControllerButton.East) ||
            state.IsDown(ControllerButton.Back) && !toggleChord, actions);
        Edge(ControllerAction.PreviousPage, state.IsDown(ControllerButton.LeftShoulder), actions);
        Edge(ControllerAction.NextPage, state.IsDown(ControllerButton.RightShoulder), actions);
        Edge(ControllerAction.Secondary, state.IsDown(ControllerButton.West), actions);
        Edge(ControllerAction.Tertiary, state.IsDown(ControllerButton.North), actions);
        Edge(ControllerAction.Menu, state.IsDown(ControllerButton.Start) && !toggleChord, actions);
        return actions;
    }

    private void Edge(ControllerAction action, bool pressed, List<ControllerAction> actions)
    {
        if (!pressed) { _held.Remove(action); return; }
        if (_held.Add(action)) actions.Add(action);
    }

    private void Repeat(ControllerAction action, bool pressed, long now, List<ControllerAction> actions)
    {
        if (!pressed) { _nextRepeat.Remove(action); return; }
        if (!_nextRepeat.TryGetValue(action, out long next))
        {
            actions.Add(action);
            _nextRepeat[action] = now + 330;
        }
        else if (now >= next)
        {
            actions.Add(action);
            _nextRepeat[action] = now + (action is ControllerAction.PageUp or ControllerAction.PageDown ? 220 : 85);
        }
    }
}

internal interface IControllerSurface
{
    void HandleControllerAction(ControllerAction action);
}

internal sealed class SdlControllerInput : IDisposable
{
    private const uint InitGamepad = 0x00002000;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Dictionary<uint, OpenPad> _pads = new();
    private bool _initialized;
    private long _nextEnumeration;
    private string _status = "";
    private bool _playStation;

    public event Action<ControllerAction>? ActionReceived;
    public event Action<string, bool, bool>? StatusChanged;

    public SdlControllerInput()
    {
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        if (_initialized) return;
        try
        {
            if (!SDL_Init(InitGamepad))
            {
                SetStatus("Контроллер недоступен: " + GetError(), false);
                return;
            }
            _initialized = true;
            Enumerate();
            _timer.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            SetStatus("Контроллер недоступен: SDL3.dll не загружен", false);
        }
    }

    private void Poll()
    {
        if (!_initialized) return;
        try
        {
            SDL_PumpEvents();
            SDL_UpdateGamepads();
            long now = Environment.TickCount64;
            if (now >= _nextEnumeration)
            {
                Enumerate();
                _nextEnumeration = now + 700;
            }
            foreach (var pad in _pads.Values.ToArray())
            {
                if (!SDL_GamepadConnected(pad.Handle)) continue;
                var snapshot = Read(pad.Handle);
                foreach (var action in pad.Mapper.Update(snapshot, now))
                {
                    SetStatus(pad.Name, true, pad.Type is 5 or 6 or 7);
                    // A modal editor may open in the callback. Dispatch after polling returns so
                    // the dialog never re-enters the current SDL polling stack.
                    _timer.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_initialized) ActionReceived?.Invoke(action);
                    }), DispatcherPriority.Input);
                }
            }
        }
        catch (Exception ex)
        {
            _timer.Stop();
            SetStatus("Контроллер недоступен: " + ex.Message, false);
        }
    }

    private void Enumerate()
    {
        int count;
        IntPtr ids = SDL_GetGamepads(out count);
        if (ids == IntPtr.Zero)
        {
            SetStatus("Контроллер недоступен: " + GetError(), false);
            return;
        }
        var connected = new HashSet<uint>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                uint id = unchecked((uint)Marshal.ReadInt32(ids, i * sizeof(uint)));
                connected.Add(id);
                if (_pads.ContainsKey(id)) continue;
                IntPtr handle = SDL_OpenGamepad(id);
                if (handle == IntPtr.Zero) continue;
                string name = Marshal.PtrToStringUTF8(SDL_GetGamepadName(handle)) ?? "Геймпад";
                int type = SDL_GetGamepadType(handle);
                _pads.Add(id, new OpenPad(handle, name, type));
                SetStatus(name, true, type is 5 or 6 or 7);
            }
        }
        finally { SDL_free(ids); }
        foreach (uint id in _pads.Keys.Where(id => !connected.Contains(id)).ToArray())
        {
            SDL_CloseGamepad(_pads[id].Handle);
            _pads.Remove(id);
        }
        if (_pads.Count == 0) SetStatus("", false);
    }

    private static ControllerSnapshot Read(IntPtr pad)
    {
        ulong buttons = 0;
        foreach (ControllerButton button in Enum.GetValues<ControllerButton>())
            if (SDL_GetGamepadButton(pad, (int)button)) buttons |= 1UL << (int)button;
        return new ControllerSnapshot(buttons, SDL_GetGamepadAxis(pad, 0), SDL_GetGamepadAxis(pad, 1),
            SDL_GetGamepadAxis(pad, 3), SDL_GetGamepadAxis(pad, 4), SDL_GetGamepadAxis(pad, 5));
    }

    private void SetStatus(string status, bool connected, bool playStation = false)
    {
        if (_status == status && _playStation == playStation) return;
        _status = status;
        _playStation = playStation;
        StatusChanged?.Invoke(status, connected, playStation);
    }

    private static string GetError() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "неизвестная ошибка SDL";

    public void Dispose()
    {
        _timer.Stop();
        foreach (var pad in _pads.Values) SDL_CloseGamepad(pad.Handle);
        _pads.Clear();
        if (_initialized) SDL_QuitSubSystem(InitGamepad);
        _initialized = false;
    }

    private sealed record OpenPad(IntPtr Handle, string Name, int Type)
    {
        public ControllerActionMapper Mapper { get; } = new();
    }

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_Init(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_PumpEvents();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_UpdateGamepads();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GamepadConnected(IntPtr gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_GetGamepadType(IntPtr gamepad);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_GetError();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_free(IntPtr pointer);
}
