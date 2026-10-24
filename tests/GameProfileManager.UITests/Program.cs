using System.Diagnostics;
using System.Text.Json;
using GameProfileManager;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;

if (args.Length is < 1 or > 2) throw new ArgumentException("Expected a fresh UI Smoke output directory and optional published EXE path.");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("UI Smoke output directory must be empty.");
Directory.CreateDirectory(output);

var store = new SqliteStateStore(Path.Combine(output, "state"));
var state = store.Load();
if (state.Theme != "System") throw new InvalidOperationException("New installations must follow the system theme.");
state.CheckAtStartup = false;
state.MinimizeToTray = false;
string appExe = args.Length == 2 ? Path.GetFullPath(args[1])
    : Path.ChangeExtension(typeof(MainWindow).Assembly.Location, ".exe");
if (!File.Exists(appExe)) throw new FileNotFoundException("WinUI application is missing", appExe);
string sampleExe = Path.Combine(output, "UiSample.exe");
File.Copy(appExe, sampleExe);
state.Games.Add(new GameEntry { Name = "Тестовая игра", ExePath = sampleExe,
    ProfileId = state.Profiles[0].Id });
store.Save(state);

var startInfo = new ProcessStartInfo(appExe)
{
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(appExe)!,
    ArgumentList = { "--ui-smoke", output }
};
startInfo.Environment["TEMP"] = output;
startInfo.Environment["TMP"] = output;
using var process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("Could not start WinUI application.");

if (!process.WaitForExit(120000))
{
    process.Kill(entireProcessTree: true);
    throw new TimeoutException("WinUI UI Smoke did not finish within 120 seconds.");
}
string errorPath = Path.Combine(output, "smoke-error.txt");
if (File.Exists(errorPath)) throw new InvalidOperationException(File.ReadAllText(errorPath));
string resultPath = Path.Combine(output, "smoke-result.json");
if (!File.Exists(resultPath)) throw new InvalidOperationException($"WinUI UI Smoke returned {process.ExitCode} without a result file.");
using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
var root = result.RootElement;
if (root.GetProperty("routes").GetArrayLength() != 15 ||
    !root.GetProperty("graphicsUnavailable").GetBoolean() ||
    !root.GetProperty("iconLoaded").GetBoolean() ||
    !root.GetProperty("shellChromeReady").GetBoolean() ||
    !root.GetProperty("gameColumnsLabeled").GetBoolean() ||
    !root.GetProperty("loaderShownAndHidden").GetBoolean() ||
    !root.GetProperty("importReturnWorks").GetBoolean() ||
    !root.GetProperty("controllerReturnWorks").GetBoolean() ||
    !root.GetProperty("controllerFocusAndAcceptWorks").GetBoolean() ||
    !root.GetProperty("dualSenseLayoutWorks").GetBoolean() ||
    !root.GetProperty("controllerDropdownCloses").GetBoolean() ||
    !root.GetProperty("controllerMenuWorks").GetBoolean() ||
    !root.GetProperty("monitorAppearsImmediately").GetBoolean() ||
    !root.GetProperty("exeSelectionWorks").GetBoolean() ||
    !root.GetProperty("restoreClearsProfile").GetBoolean() ||
    !root.GetProperty("draftIsolated").GetBoolean() ||
    !root.GetProperty("editorSaved").GetBoolean() ||
    !root.GetProperty("discardPreserved").GetBoolean() ||
    !root.GetProperty("narrowSizeAccepted").GetBoolean() ||
    root.GetProperty("externalWriteAttempts").GetInt32() != 0 ||
    root.GetProperty("screenshots").GetInt32() != 48)
    throw new InvalidOperationException("WinUI UI Smoke assertions failed.");
Console.WriteLine("PASS WinUI launched with temporary SQLite and fake integration clients");
Console.WriteLine("PASS 15 routes in both themes, overview at 6 sizes, games/game/editor at 960, 640, and 420 epx");
Console.WriteLine("PASS graphics unavailable and no external writes during navigation");
Console.WriteLine("PASS game EXE icon extraction and WinUI binding");
Console.WriteLine("PASS title icon, compact sidebar, labeled game data and shared activity loader");
Console.WriteLine("PASS always-visible back arrow, controller/import return, keyboard and mouse back dispatch");
Console.WriteLine("PASS WinUI desktop controller focus and native button activation");
Console.WriteLine("PASS DualSense button labels and controller dropdown close");
Console.WriteLine("PASS immediate monitor fields, EXE selection and profile reset");
Console.WriteLine("PASS profile editor draft isolation and explicit save to temporary SQLite");
Console.WriteLine("Evidence: " + resultPath);
