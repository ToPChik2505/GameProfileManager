using System.Diagnostics;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;

namespace GameProfileManager.Core;

public sealed class SyncService(SqliteStateStore store)
{
    public string? NvidiaStatus { get; private set; }
    public string? RtssStatus { get; private set; }

    public void CheckIntegrations(AppState state)
    {
        try
        {
            using var rtss = new RtssClient(state.RtssDirectory);
            RtssStatus = (Process.GetProcessesByName("RTSS").Length > 0 ? "RTSS запущен: " : "RTSS установлен, но процесс не запущен: ") + rtss.InstallDirectory;
        }
        catch (Exception ex) { RtssStatus = "Недоступен: " + ex.Message; }
        try { using var nvidia = new NvidiaClient(); NvidiaStatus = "NVAPI DRS доступен"; }
        catch (Exception ex) { NvidiaStatus = "Недоступен: " + ex.Message; }
    }

    public void CheckSaved(AppState state)
    {
        foreach (var game in state.Games)
        {
            var profile = state.Profiles.FirstOrDefault(p => p.Id == game.ProfileId);
            if (profile is null || string.IsNullOrWhiteSpace(game.ExePath))
            {
                game.Status = ApplyStatus.Pending;
                game.Detail = state.Backups.Any(b => b.ManagedGameId == game.Id)
                    ? "Профиль отключён. Нажмите «Применить», чтобы восстановить исходные параметры."
                    : "Профиль не выбран: параметры не применяются";
                continue;
            }
            if (!File.Exists(game.ExePath)) { game.Status = ApplyStatus.Error; game.Detail = "Файл игры не найден"; continue; }
            if (state.Games.Count(g => !string.IsNullOrWhiteSpace(g.ExePath) &&
                    Path.GetFileName(g.ExePath).Equals(Path.GetFileName(game.ExePath), StringComparison.OrdinalIgnoreCase)) > 1)
            { game.Status = ApplyStatus.Unsupported; game.Detail = "Несколько записей используют одно имя .exe в RTSS"; continue; }
            bool rtssMatched = false, nvidiaMatched = false;
            string rtssDetail = "", nvidiaDetail = "";
            try
            {
                using var rtss = new RtssClient(state.RtssDirectory);
                rtssMatched = rtss.ProfileExists(Path.GetFileName(game.ExePath)) && rtss.ReadFps(Path.GetFileName(game.ExePath)) == profile.FpsLimit;
                rtssDetail = rtssMatched ? "подтверждён" : "отличается";
            }
            catch (Exception ex) { rtssDetail = ex.Message; store.Log(ex); }
            try
            {
                using var nvidia = new NvidiaClient();
                var values = nvidia.Read(game.ExePath);
                nvidiaMatched = values.Vrr == (profile.Vrr == VrrMode.FixedRefresh ? NvidiaClient.FixedRefresh : NvidiaClient.AllowGSync) &&
                    values.VSync == MapVSync(profile.VSync) && values.Fps == 0;
                nvidiaDetail = nvidiaMatched ? "подтверждён" : "отличается";
            }
            catch (Exception ex) { nvidiaDetail = ex.Message; store.Log(ex); }
            game.Status = rtssMatched && nvidiaMatched ? ApplyStatus.Applied :
                rtssDetail is "отличается" || nvidiaDetail is "отличается" ? ApplyStatus.Pending : ApplyStatus.Unsupported;
            game.Detail = $"RTSS: {rtssDetail}; NVIDIA: {nvidiaDetail}" + (game.Status == ApplyStatus.Applied ? "; работа VRR в игре не измерена" : "");
        }
    }

    public void ApplyAll(AppState state)
    {
        var duplicateExeNames = state.Games.Where(g => !string.IsNullOrWhiteSpace(g.ExePath))
            .GroupBy(g => Path.GetFileName(g.ExePath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var game in state.Games)
        {
            var profile = state.Profiles.FirstOrDefault(p => p.Id == game.ProfileId);
            if (profile is null)
            {
                if (state.Backups.Any(b => b.ManagedGameId == game.Id)) Restore(state, game);
                else { game.Status = ApplyStatus.Pending; game.Detail = "Профиль не выбран: параметры не применяются"; }
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.ExePath)) { game.Status = ApplyStatus.Pending; game.Detail = "Выберите игровой .exe"; continue; }
            if (!File.Exists(game.ExePath)) { game.Status = ApplyStatus.Error; game.Detail = "Файл игры не найден"; continue; }
            if (duplicateExeNames.Contains(Path.GetFileName(game.ExePath))) { game.Status = ApplyStatus.Unsupported; game.Detail = "RTSS сопоставляет профиль по имени .exe; обнаружено совпадение у разных игр"; continue; }
            if (profile.FpsLimit is < 1 or > 1000 || string.IsNullOrWhiteSpace(profile.Name)) { game.Status = ApplyStatus.Error; game.Detail = "Некорректный профиль"; continue; }
            var backup = state.Backups.FirstOrDefault(b => b.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
            if (backup is null) { backup = new SettingBackup { ExePath = game.ExePath, ManagedGameId = game.Id }; state.Backups.Add(backup); }
            bool nvidiaApplied = false, rtssApplied = false;
            Exception? nvidiaError = null, rtssError = null;
            try
            {
                using var nvidia = new NvidiaClient();
                if (!backup.NvidiaCaptured)
                {
                    var previous = nvidia.Read(game.ExePath);
                    backup.NvidiaVrr = previous.Vrr; backup.NvidiaVrrExplicit = previous.VrrExplicit;
                    backup.NvidiaVSync = previous.VSync; backup.NvidiaVSyncExplicit = previous.VSyncExplicit;
                    backup.NvidiaFpsLimit = previous.Fps; backup.NvidiaFpsLimitExplicit = previous.FpsExplicit;
                    backup.NvidiaProfileExisted = previous.ProfileExisted; backup.ManagedGameId = game.Id;
                    backup.NvidiaCaptured = true;
                    store.Save(state); // durable before DRS write
                }
                nvidia.Apply(game, profile);
                nvidiaApplied = true;
            }
            catch (Exception ex) { nvidiaError = ex; store.Log(ex); }
            try
            {
                using var rtss = new RtssClient(state.RtssDirectory);
                string exeName = Path.GetFileName(game.ExePath);
                if (!backup.RtssCaptured)
                {
                    backup.RtssProfileExisted = rtss.ProfileExists(exeName);
                    backup.RtssFps = rtss.ReadFps(exeName);
                    backup.RtssCaptured = true;
                    store.Save(state); // durable before RTSS write
                }
                rtss.WriteFps(exeName, profile.FpsLimit);
                rtssApplied = true;
            }
            catch (Exception ex) { rtssError = ex; store.Log(ex); }
            bool running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExePath)).Length > 0;
            game.Status = nvidiaApplied && rtssApplied ? (running ? ApplyStatus.RestartRequired : ApplyStatus.Applied) :
                nvidiaError is NotSupportedException or DllNotFoundException || rtssError is NotSupportedException or FileNotFoundException or DllNotFoundException
                    ? ApplyStatus.Unsupported : ApplyStatus.Error;
            game.Detail = $"NVIDIA: {(nvidiaApplied ? "сохранено и проверено" : nvidiaError?.Message)}; RTSS: {(rtssApplied ? "сохранено и проверено" : rtssError?.Message)}" +
                (running && nvidiaApplied && rtssApplied ? "; перезапустите игру" : "");
            if (nvidiaApplied && rtssApplied) game.LastApplied = DateTimeOffset.Now;
            store.Save(state);
        }
        state.LastSync = DateTimeOffset.Now;
        store.Save(state);
    }

    public void Restore(AppState state, GameEntry game)
    {
        var backup = state.Backups.FirstOrDefault(b => b.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Резервные значения для этой игры не найдены.");
        var errors = new List<string>();
        if (backup.NvidiaCaptured)
        {
            try { using var nvidia = new NvidiaClient(); nvidia.Restore(game.ExePath, backup); backup.NvidiaCaptured = false; }
            catch (Exception ex) { errors.Add("NVIDIA: " + ex.Message); store.Log(ex); }
        }
        if (backup.RtssCaptured)
        {
            try { using var rtss = new RtssClient(state.RtssDirectory); rtss.Restore(Path.GetFileName(game.ExePath), backup.RtssProfileExisted, backup.RtssFps); backup.RtssCaptured = false; }
            catch (Exception ex) { errors.Add("RTSS: " + ex.Message); store.Log(ex); }
        }
        if (!backup.NvidiaCaptured && !backup.RtssCaptured) state.Backups.Remove(backup);
        game.Status = errors.Count == 0 ? ApplyStatus.Pending : ApplyStatus.Error;
        game.Detail = errors.Count == 0 ? "Исходные значения восстановлены" : string.Join("; ", errors);
        store.Save(state);
    }
    private static uint MapVSync(VSyncMode value) => value switch { VSyncMode.On => NvidiaClient.VSyncOn, VSyncMode.Off => NvidiaClient.VSyncOff, _ => NvidiaClient.VSyncApplication };
}
