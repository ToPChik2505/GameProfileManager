using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GameProfileManager;

// Задача Планировщика создаётся только уже повышенным приложением.
// Её действие ограничено этим EXE в защищённом каталоге установки.
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
        dynamic registeredTask;
        try { registeredTask = folder.GetTask(TaskName); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Планировщик создал задачу, но приложение не может прочитать её обратно: " + ex.Message, ex);
        }
        if (TaskMismatch(registeredTask) is { } mismatch)
            throw new InvalidOperationException("Созданная задача запуска не прошла проверку: " + mismatch);
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

    private static bool IsOwnTask(dynamic task) => TaskMismatch(task) is null;

    private static string? TaskMismatch(dynamic task)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || !IsProtectedInstall(exe)) return "приложение запущено вне защищённой папки установки";
        dynamic definition = task.Definition;
        if (definition.Actions.Count != 1) return "число действий отличается от одного";
        if (!definition.Settings.Enabled) return "задача отключена";
        dynamic action = definition.Actions.Item(1);
        if (action.Type != 0) return "действие не является запуском EXE";
        if (!string.IsNullOrEmpty((string?)action.Arguments)) return "у действия появились аргументы";
        if (!string.Equals(Path.GetFullPath((string)action.Path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
            return "путь к EXE отличается от установленного приложения";
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath((string)action.WorkingDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(exe)!)), StringComparison.OrdinalIgnoreCase))
            return "рабочая папка отличается от папки приложения";
        if (!IsCurrentUser((string?)definition.Principal.UserId)) return "учётная запись задачи отличается от текущего пользователя";
        if (definition.Principal.LogonType != 3) return "тип входа не является интерактивным";
        if (definition.Principal.RunLevel != 1) return "задача не настроена на максимальные права";
        return null;
    }

    private static bool IsCurrentUser(string? registeredUser)
    {
        if (string.IsNullOrWhiteSpace(registeredUser)) return false;
        if (string.Equals(registeredUser, UserSid, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(registeredUser).Translate(typeof(SecurityIdentifier));
            return string.Equals(sid.Value, UserSid, StringComparison.OrdinalIgnoreCase);
        }
        catch (IdentityNotMappedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsProtectedInstall(string exe)
    {
        // Действие не должно указывать на доступную пользователю папку загрузок или проекта.
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
