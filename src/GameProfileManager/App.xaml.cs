using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using GameProfileManager.Application;
using GameProfileManager.Infrastructure;
using GameProfileManager.Core;
using GameProfileManager.Integrations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

public partial class App : Microsoft.UI.Xaml.Application
{
    private SingleInstanceCoordinator? _singleInstance;
    internal static MainWindow? MainWindowInstance { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        bool autoStart = commandLine.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        int smokeArgument = Array.IndexOf(commandLine, "--ui-smoke");
        bool smoke = smokeArgument >= 0 && smokeArgument + 1 < commandLine.Length;
        string? smokeDirectory = smoke ? Path.GetFullPath(commandLine[smokeArgument + 1]) : null;
        _singleInstance = new SingleInstanceCoordinator(smoke ? "GameProfileManager.UiSmoke." + Environment.ProcessId : "GameProfileManager");
        if (_singleInstance.TrySignalExisting(!autoStart)) { Exit(); return; }

        if (!smoke && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (ElevatedLaunchTask.TryRun(autoStart)) { Exit(); return; }
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    Verb = "runas", UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory,
                    Arguments = autoStart ? "--autostart" : ""
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                ShowStartupError("Для применения профилей нужны права администратора. Запуск отменён.");
                return;
            }
            catch (Exception ex)
            {
                ShowStartupError("Не удалось запросить права администратора: " + ex.Message);
                return;
            }
            Exit();
            return;
        }

        if (!_singleInstance.TryStart(() => MainWindowInstance?.DispatcherQueue.TryEnqueue(
                () => MainWindowInstance?.RestoreFromSecondLaunch()), !autoStart))
        {
            Exit();
            return;
        }

        try
        {
            string? startupNotice = null;
            if (!smoke)
            {
                ElevatedLaunchTask.UpgradeIfNeeded();
                startupNotice = MainViewModel.UpgradeAutoStartEntry();
            }
            var store = new SqliteStateStore(smoke ? Path.Combine(smokeDirectory!, "state") : null);
            SyncService? sync = smoke ? new SyncService(store,
                _ => new SmokeRtssClient(), () => new SmokeNvidiaClient(), () => []) : null;
            MainWindowInstance = new MainWindow(new MainViewModel(store, sync));
            if (startupNotice is not null) MainWindowInstance.ViewModel.Notice = startupNotice;
            ThemeManager.Apply(MainWindowInstance.ViewModel.Theme);
            if (!autoStart || !MainWindowInstance.StartHiddenIfTrayAvailable())
            {
                MainWindowInstance.Activate();
                if (!smoke) MainWindowInstance.MaximizeForApplicationStartup();
            }
            if (smoke) MainWindowInstance.DispatcherQueue.TryEnqueue(async () =>
            {
                try { await MainWindowInstance.RunSmokeAsync(smokeDirectory!); }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(smokeDirectory!, "smoke-error.txt"), ex.ToString());
                    MainWindowInstance.Close();
                }
            });
        }
        catch (StateLoadException ex)
        {
            ShowStartupError(
                $"{ex.Message}\n\nПриложение завершит работу без сохранения пустого состояния.\n\nПричина: {ex.InnerException?.Message}",
                "Ошибка загрузки данных");
        }
    }

    private void ShowStartupError(string message, string title = "Game Profile Manager")
    {
        var errorWindow = new Window { Title = title };
        var stack = new StackPanel { Spacing = 18, Padding = new Thickness(24) };
        stack.Children.Add(new InfoBar
        {
            Title = title, Message = message, Severity = InfoBarSeverity.Error,
            IsOpen = true, IsClosable = false
        });
        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => errorWindow.Close();
        stack.Children.Add(close);
        errorWindow.Content = stack;
        errorWindow.Closed += (_, _) => Exit();
        errorWindow.Activate();
    }

    internal void ReleaseInstance()
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
