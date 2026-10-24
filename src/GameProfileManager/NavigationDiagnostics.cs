using System.Diagnostics;

namespace GameProfileManager;

internal readonly record struct PageBuildSample(double Milliseconds, long AllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections);

internal sealed record NavigationSample(string Route, string Action, double LoadedMilliseconds,
    double BuildMilliseconds, long BuildAllocatedBytes, int BuildGen0Collections,
    int BuildGen1Collections, int BuildGen2Collections, int NavigationGen0Collections,
    int NavigationGen1Collections, int NavigationGen2Collections);

internal sealed record NavigationProbe(string Action, long StartedAt, int Gen0, int Gen1, int Gen2);

internal sealed class NavigationDiagnostics
{
    private readonly Queue<NavigationSample> _samples = new();

    public NavigationProbe Begin(string action) => new(action, Stopwatch.GetTimestamp(),
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    public static PageBuildSample MeasureBuild(long startedAt, long allocatedBefore,
        int gen0Before, int gen1Before, int gen2Before) => new(
        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
        GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
        GC.CollectionCount(0) - gen0Before,
        GC.CollectionCount(1) - gen1Before,
        GC.CollectionCount(2) - gen2Before);

    public void Complete(NavigationProbe probe, string route, PageBuildSample build)
    {
        _samples.Enqueue(new NavigationSample(route, probe.Action,
            Stopwatch.GetElapsedTime(probe.StartedAt).TotalMilliseconds,
            build.Milliseconds, build.AllocatedBytes, build.Gen0Collections,
            build.Gen1Collections, build.Gen2Collections,
            GC.CollectionCount(0) - probe.Gen0,
            GC.CollectionCount(1) - probe.Gen1,
            GC.CollectionCount(2) - probe.Gen2));
        while (_samples.Count > 256) _samples.Dequeue();
    }

    public NavigationSample[] Snapshot() => _samples.ToArray();
}
