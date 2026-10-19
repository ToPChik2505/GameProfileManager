using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GameProfileManager;

// A Task Scheduler task is created only by an already elevated application.
// Its action is limited to this executable in a protected installation folder.
internal static class ElevatedLaunchTask
{
    private static string UserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");

    private static string TaskName => "GameProfileManager.Elevated." + UserSid;

    public static bool IsInstalled
    {
        get
        {
            try { return WithTask(task => IsOwnTask(task)); }
            catch (Exception) { return false; }
        }
    }

    public static bool TryRun()
    {
        if (Environment.ProcessPath is not { } exe || !IsProtectedInstall(exe)) return false;
        try
        {
            return WithTask(task =>
            {
                if (!IsOwnTask(task)) return false;
                task.Run(null);
                return true;
            });
        }
        catch (Exception) { return false; }
    }

    public static void Install()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Путь к приложению неизвестен.");
        if (!IsProtectedInstall(exe))
            throw new InvalidOperationException("Для запуска без повторного UAC сначала поместите опубликованное приложение в Program Files и запустите его оттуда.");

        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "Запуск Game Profile Manager с правами администратора по запросу текущего пользователя.";
        definition.Principal.UserId = UserSid;
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        definition.Principal.RunLevel = 1;  // TASK_RUNLEVEL_HIGHEST
        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
        dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = exe;
        action.WorkingDirectory = Path.GetDirectoryName(exe)!;
        folder.RegisterTaskDefinition(TaskName, definition, 6, null, null, 3, null); // CREATE_OR_UPDATE
        if (!IsInstalled) throw new InvalidOperationException("Планировщик не подтвердил созданную задачу запуска.");
    }

    public static void Uninstall()
    {
        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        try { folder.DeleteTask(TaskName, 0); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { }
    }

    private static bool WithTask(Func<dynamic, bool> action)
    {
        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic task = folder.GetTask(TaskName);
        return action(task);
    }

    private static bool IsOwnTask(dynamic task)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || !IsProtectedInstall(exe)) return false;
        dynamic definition = task.Definition;
        if (definition.Actions.Count != 1 || !definition.Settings.Enabled) return false;
        dynamic action = definition.Actions.Item(1);
        if (action.Type != 0 || !string.IsNullOrEmpty((string)action.Arguments)) return false;
        return string.Equals(Path.GetFullPath((string)action.Path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath((string)action.WorkingDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(exe)!)), StringComparison.OrdinalIgnoreCase)
            && string.Equals((string)definition.Principal.UserId, UserSid, StringComparison.OrdinalIgnoreCase)
            && definition.Principal.LogonType == 3 && definition.Principal.RunLevel == 1;
    }

    private static bool IsProtectedInstall(string exe)
    {
        // The action must not point to a user-writable Downloads/project folder.
        string full = Path.GetFullPath(exe);
        return new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    private static dynamic NewService() => Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
        ?? throw new InvalidOperationException("Планировщик заданий Windows недоступен."))!;
}
