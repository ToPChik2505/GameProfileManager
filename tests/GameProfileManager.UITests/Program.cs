using System.Diagnostics;
using System.Text.Json;
using GameProfileManager;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;

if (args.Length != 1) throw new ArgumentException("Expected a fresh UI Smoke output directory.");
string output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("UI Smoke output directory must be empty.");
Directory.CreateDirectory(output);

var store = new SqliteStateStore(Path.Combine(output, "state"));
var state = store.Load();
if (state.Theme != "System") throw new InvalidOperationException("New installations must follow the system theme.");
state.CheckAtStartup = false;
state.MinimizeToTray = false;
string sampleExe = Path.Combine(output, "UiSample.exe");
File.WriteAllBytes(sampleExe, [0x4D, 0x5A]);
state.Games.Add(new GameEntry { Name = "Тестовая игра", ExePath = sampleExe });
store.Save(state);

string appExe = Path.ChangeExtension(typeof(MainWindow).Assembly.Location, ".exe");
if (!File.Exists(appExe)) throw new FileNotFoundException("WinUI application is missing", appExe);
using var process = Process.Start(new ProcessStartInfo(appExe)
{
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(appExe)!,
    ArgumentList = { "--ui-smoke", output }
}) ?? throw new InvalidOperationException("Could not start WinUI application.");

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
    !root.GetProperty("draftIsolated").GetBoolean() ||
    !root.GetProperty("editorSaved").GetBoolean() ||
    !root.GetProperty("discardPreserved").GetBoolean() ||
    root.GetProperty("externalWriteAttempts").GetInt32() != 0 ||
    root.GetProperty("screenshots").GetInt32() != 33)
    throw new InvalidOperationException("WinUI UI Smoke assertions failed.");
Console.WriteLine("PASS WinUI launched with temporary SQLite and fake integration clients");
Console.WriteLine("PASS 15 routes in both themes, 3 laptop sizes, graphics unavailable, no external writes during navigation");
Console.WriteLine("PASS profile editor draft isolation and explicit save to temporary SQLite");
Console.WriteLine("Evidence: " + resultPath);
