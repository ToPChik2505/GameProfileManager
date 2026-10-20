using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace GameProfileManager;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // UI smoke и другие хосты используют ресурсы WPF, но не должны
        // запускать ещё один экземпляр приложения.
        if (Assembly.GetEntryAssembly() != typeof(App).Assembly)
        {
            base.OnStartup(e);
            return;
        }
        _singleInstance = new SingleInstanceCoordinator();
        if (_singleInstance.TrySignalExisting())
        {
            Shutdown();
            return;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            // Предварительно проверенная задача может запускать установленную
            // защищённую копию с повышением без повторного подтверждения UAC.
            if (ElevatedLaunchTask.TryRun())
            {
                Shutdown();
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show("Для применения профилей нужны права администратора. Запуск отменён.",
                    "Game Profile Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось запросить права администратора: " + ex.Message,
                    "Game Profile Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }
        if (!_singleInstance.TryStart(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainWindow is GameProfileManager.MainWindow window)
                    window.RestoreFromSecondLaunch();
            }))))
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
