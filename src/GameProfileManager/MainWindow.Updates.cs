using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace GameProfileManager;

public sealed partial class MainWindow
{
    public async Task CheckUpdatesAsync()
    {
        string? availableTag = null;
        Version? availableVersion = null;
        await _viewModel.RunUserActionAsync("Проверяем обновления…", async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("GameProfileManager/1.0");
                var release = await new GitHubReleaseLookup(client).LatestAsync();
                var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
                if (release.Version <= current)
                {
                    _viewModel.Notice = $"Установлена актуальная версия {current.ToString(3)}.";
                    return;
                }
                availableTag = release.Tag;
                availableVersion = release.Version;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
            { _viewModel.Notice = "Ошибка проверки обновлений. Проверьте подключение к GitHub и повторите попытку."; }
        });
        if (availableTag is null || availableVersion is null) return;
        _viewModel.Notice = $"Доступна версия {availableVersion}.";
        if (await PageFactory.ConfirmUpdate(this, availableVersion.ToString()))
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/Vladislav2505/GameProfileManager/releases/tag/" +
                    Uri.EscapeDataString(availableTag)) { UseShellExecute = true });
            }
            catch (Win32Exception) { _viewModel.Notice = "Не удалось открыть страницу обновления."; }
        }
    }
}
