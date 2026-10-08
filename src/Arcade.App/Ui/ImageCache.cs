using System.Collections.Concurrent;
using System.Diagnostics;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Arcade.App.Ui;

/// <summary>
/// Loads images (artwork, backgrounds) without stalling the UI: files are decoded on the thread
/// pool and uploaded on the render thread a few per frame. Least recently used textures are freed
/// beyond <see cref="Capacity"/>.
/// </summary>
sealed class ImageCache(GL gl) : IDisposable
{
    const int Capacity = 48;
    const int UploadsPerFrame = 2;

    sealed class Entry
    {
        public Texture? Texture;
        public bool Failed;
        public long LastUsed;
        public long LoadedAt;
    }

    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<(string Path, ImageResult? Image)> _decoded = new();

    /// <summary>Returns the texture if loaded; otherwise starts loading it and returns null.</summary>
    public Texture? Get(string path) => Get(path, out _);

    /// <param name="fade">0→1 over the first quarter second after the image appears, for a fade-in.</param>
    public Texture? Get(string path, out float fade)
    {
        fade = 1;
        if (!_entries.TryGetValue(path, out var entry))
        {
            _entries[path] = entry = new Entry();
            ThreadPool.QueueUserWorkItem(_ => _decoded.Enqueue((path, Decode(path))));
        }
        entry.LastUsed = Stopwatch.GetTimestamp();
        if (entry.Texture != null)
            fade = Math.Clamp((float)Stopwatch.GetElapsedTime(entry.LoadedAt).TotalSeconds / 0.25f, 0, 1);
        return entry.Texture;
    }

    /// <summary>Forgets a cached image so it is read again, e.g. after the file was replaced.</summary>
    public void Invalidate(string path)
    {
        if (_entries.Remove(path, out var entry))
            entry.Texture?.Dispose();
    }

    static ImageResult? Decode(string path)
    {
        try
        {
            var image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            Texture.Premultiply(image.Data);
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Call once per frame on the render thread.</summary>
    public void Pump()
    {
        for (var i = 0; i < UploadsPerFrame && _decoded.TryDequeue(out var item); i++)
        {
            if (!_entries.TryGetValue(item.Path, out var entry))
                continue; // invalidated while decoding
            if (item.Image == null)
            {
                entry.Failed = true;
                continue;
            }
            entry.Texture = new Texture(gl, item.Image.Width, item.Image.Height, item.Image.Data, mipmaps: true);
            entry.LoadedAt = Stopwatch.GetTimestamp();
        }

        if (_entries.Count > Capacity)
        {
            foreach (var (path, entry) in _entries.OrderBy(e => e.Value.LastUsed).Take(_entries.Count - Capacity).ToList())
            {
                entry.Texture?.Dispose();
                _entries.Remove(path);
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            entry.Texture?.Dispose();
        _entries.Clear();
    }
}
