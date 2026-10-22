using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using GameProfileManager.Core;

namespace GameProfileManager.Presentation;

/// <summary>Загружает иконки вне UI-потока и удерживает ограниченный кэш изображений.</summary>
internal sealed class GameIconLoader
{
    private const int CacheCapacity = 64;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<Exception> _log;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, ImageSource Image)> _recency = new();
    private readonly Dictionary<string, List<Action<ImageSource?>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public GameIconLoader(DispatcherQueue dispatcher, Action<Exception> log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    public void Request(GameEntry game, Action<ImageSource?> completed)
    {
        string exePath = game.ExePath;
        string? steamAppId = game.SteamAppId;
        string key = exePath + "\0" + steamAppId;
        if (_cache.TryGetValue(key, out var cached))
        {
            _recency.Remove(cached);
            _recency.AddFirst(cached);
            completed(cached.Value.Image);
            return;
        }
        if (_inFlight.TryGetValue(key, out var callbacks))
        {
            callbacks.Add(completed);
            return;
        }
        _inFlight.Add(key, [completed]);
        _ = LoadAsync(exePath, steamAppId, key);
    }

    private async Task LoadAsync(string exePath, string? steamAppId, string key)
    {
        string? path = null;
        try
        {
            await _slots.WaitAsync();
            try { path = await Task.Run(() => FileIcon.LoadPath(exePath, steamAppId)); }
            finally { _slots.Release(); }
        }
        catch (Exception ex) { _log(ex); }

        _dispatcher.TryEnqueue(() =>
        {
            ImageSource? image = null;
            if (path is not null)
            {
                try { image = new BitmapImage(new Uri(path)); }
                catch (Exception ex) { _log(ex); }
            }
            if (!_inFlight.Remove(key, out var callbacks)) return;
            if (image is not null) AddToCache(key, image);
            foreach (var callback in callbacks) callback(image);
        });
    }

    private void AddToCache(string key, ImageSource image)
    {
        if (_cache.Remove(key, out var existing)) _recency.Remove(existing);
        var node = _recency.AddFirst((key, image));
        _cache.Add(key, node);
        if (_cache.Count <= CacheCapacity) return;
        var last = _recency.Last!;
        _recency.RemoveLast();
        _cache.Remove(last.Value.Key);
    }
}
