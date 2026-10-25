using System.Runtime.InteropServices;
using System.Security.Principal;
using GameProfileManager.Integrations;

namespace GameProfileManager;

// Задача Планировщика создаётся только уже повышенным приложением.
// Её действие ограничено этим EXE в защищённом каталоге установки.
internal static class ElevatedLaunchTask
{
    private static string UserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");

    private static string TaskName => "GameProfileManager.Elevated." + UserSid;
    private static string AutoStartTaskName => TaskName + ".Autostart";

    public static bool IsInstalled
    {
        get
        {
            try { return WithTask(task => IsOwnTask(task)); }
            catch (Exception) { return false; }
        }
    }

    public static bool IsAutoStartInstalled
    {
        get
        {
            try { return WithTask(AutoStartTaskName, task => IsOwnAutoStartTask(task)); }
            catch (Exception) { return false; }
        }
    }

    public static bool TryRun(bool autoStart = false)
    {
        if (Environment.ProcessPath is not { } exe || !IsProtectedInstall(exe)) return false;
        try
        {
            return WithTask(autoStart ? AutoStartTaskName : TaskName, task =>
            {
                if (autoStart ? !IsOwnAutoStartTask(task) : !IsOwnTask(task)) return false;
                task.Run("");
                return true;
            });
        }
        catch (Exception) { return false; }
    }

    // Старые задачи с $(Arg0) принимали произвольный аргумент от запускающего их пользователя.
    // Заменяем лишь задачу, которая в остальном совпадает с нашим защищённым EXE.
    public static void UpgradeIfNeeded()
    {
        try
        {
            if (WithTask(task => ManualTaskMismatch(task, allowLegacyArguments: true) is null && SupportsArguments(task)))
                Install();
        }
        catch (Exception) { }
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
        action.Arguments = "";
        action.WorkingDirectory = Path.GetDirectoryName(exe)!;
        folder.RegisterTaskDefinition(TaskName, definition, 6, null, null, 3, null); // CREATE_OR_UPDATE
        dynamic registeredTask;
        try { registeredTask = folder.GetTask(TaskName); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Планировщик создал задачу, но приложение не может прочитать её обратно: " + ex.Message, ex);
        }
        if (ManualTaskMismatch(registeredTask) is { } mismatch)
            throw new InvalidOperationException("Созданная задача запуска не прошла проверку: " + mismatch);
    }

    public static void InstallAutoStart()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Путь к приложению неизвестен.");
        if (!IsProtectedInstall(exe))
            throw new InvalidOperationException("Для повышенного автозапуска сначала поместите опубликованное приложение в Program Files.");

        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "Автозапуск Game Profile Manager с повышенными правами для текущего пользователя.";
        definition.Principal.UserId = UserSid;
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        definition.Principal.RunLevel = 1;  // TASK_RUNLEVEL_HIGHEST
        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
        dynamic trigger = definition.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = UserSid;
        trigger.Enabled = true;
        dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = exe;
        action.Arguments = "--autostart";
        action.WorkingDirectory = Path.GetDirectoryName(exe)!;
        folder.RegisterTaskDefinition(AutoStartTaskName, definition, 6, null, null, 3, null); // CREATE_OR_UPDATE

        dynamic registeredTask;
        try { registeredTask = folder.GetTask(AutoStartTaskName); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Планировщик создал задачу автозапуска, но приложение не может прочитать её обратно: " + ex.Message, ex);
        }
        if (AutoStartTaskMismatch(registeredTask) is { } mismatch)
            throw new InvalidOperationException("Созданная задача автозапуска не прошла проверку: " + mismatch);
    }

    public static void Uninstall()
    {
        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        try { folder.DeleteTask(TaskName, 0); }
        catch (Exception ex) when (IsTaskNotFound(ex)) { }
    }

    public static void UninstallAutoStart()
    {
        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        try { folder.DeleteTask(AutoStartTaskName, 0); }
        catch (Exception ex) when (IsTaskNotFound(ex)) { }
    }

    private static bool IsTaskNotFound(Exception exception) =>
        (exception is COMException || exception is FileNotFoundException) &&
        unchecked((uint)exception.HResult) == 0x80070002;

    private static bool WithTask(Func<dynamic, bool> action)
        => WithTask(TaskName, action);

    private static bool WithTask(string taskName, Func<dynamic, bool> action)
    {
        dynamic service = NewService();
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        dynamic task = folder.GetTask(taskName);
        return action(task);
    }

    private static bool IsOwnTask(dynamic task) => ManualTaskMismatch(task) is null;

    private static bool IsOwnAutoStartTask(dynamic task) => AutoStartTaskMismatch(task) is null;

    private static bool SupportsArguments(dynamic task) =>
        string.Equals((string?)task.Definition.Actions.Item(1).Arguments, "$(Arg0)", StringComparison.Ordinal);

    private static string? ManualTaskMismatch(dynamic task, bool allowLegacyArguments = false)
    {
        if (TaskMismatch(task, allowLegacyArguments: allowLegacyArguments) is { } mismatch) return mismatch;
        if (task.Definition.Triggers.Count != 0) return "у задачи запуска появились неизвестные триггеры";
        return null;
    }

    private static string? AutoStartTaskMismatch(dynamic task)
    {
        if (TaskMismatch(task, expectedArguments: "--autostart") is { } mismatch) return mismatch;
        dynamic triggers = task.Definition.Triggers;
        if (triggers.Count != 1) return "число триггеров автозапуска отличается от одного";
        dynamic trigger = triggers.Item(1);
        if (trigger.Type != 9) return "триггер автозапуска не является входом пользователя";
        if (!trigger.Enabled) return "триггер автозапуска отключён";
        if (!IsCurrentUser((string?)trigger.UserId)) return "учётная запись триггера отличается от текущего пользователя";
        return null;
    }

    private static string? TaskMismatch(dynamic task, bool allowLegacyArguments = false,
        string expectedArguments = "")
    {
        string? exe = Environment.ProcessPath;
        if (exe is null || !IsProtectedInstall(exe)) return "приложение запущено вне защищённой папки установки";
        dynamic definition = task.Definition;
        if (definition.Actions.Count != 1) return "число действий отличается от одного";
        if (!definition.Settings.Enabled) return "задача отключена";
        if (!definition.Settings.AllowDemandStart) return "задача не разрешает запуск по запросу";
        dynamic action = definition.Actions.Item(1);
        if (action.Type != 0) return "действие не является запуском EXE";
        string? arguments = (string?)action.Arguments;
        if (!string.Equals(arguments ?? "", expectedArguments, StringComparison.Ordinal) &&
            !(allowLegacyArguments && arguments == "$(Arg0)"))
            return "у действия появились неизвестные аргументы";
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
        try { ProtectedInstallPath.Validate(exe); return true; }
        catch (Exception) { return false; }
    }

    private static dynamic NewService() => Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
        ?? throw new InvalidOperationException("Планировщик заданий Windows недоступен."))!;
}
