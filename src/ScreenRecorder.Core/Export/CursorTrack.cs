using System.Text.Json;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.Core.Export;

/// <summary>
/// The recorded cursor, loaded compactly (an 8-hour recording at 30 fps is ~860k samples),
/// with its images, ready to draw onto frames.
/// </summary>
public sealed class CursorTrack
{
    private readonly int[] _x, _y;
    private readonly ushort[] _shape;   // 0 = hidden
    private readonly Dictionary<int, Image> _images;

    public long Count => _x.Length;

    public sealed record Image(int Width, int Height, int HotX, int HotY, byte[] Bgra);

    private CursorTrack(int[] x, int[] y, ushort[] shape, Dictionary<int, Image> images)
    {
        _x = x; _y = y; _shape = shape; _images = images;
    }

    public static CursorTrack Empty { get; } = new(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<ushort>(), new());

    public static CursorTrack Load(string projectDir)
    {
        var images = new Dictionary<int, Image>();
        foreach (var s in JsonlReader.ReadAll<CursorShape>(Path.Combine(projectDir, ProjectManifest.ShapesFile)))
        {
            try
            {
                var (w, h, px) = Png.ReadBgra(Path.Combine(projectDir, s.File));
                images[s.Id] = new Image(w, h, s.HotspotX, s.HotspotY, px);
            }
            catch { /* a damaged image just means that cursor shape isn't drawn */ }
        }

        string path = Path.Combine(projectDir, ProjectManifest.CursorFile);
        var xs = new List<int>(); var ys = new List<int>(); var shapes = new List<ushort>();
        if (File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
            {
                CursorSample? s;
                try { s = JsonSerializer.Deserialize<CursorSample>(line); } catch (JsonException) { continue; }
                if (s == null || s.Frame < 0) continue;
                // Samples are written once per frame, in order; fill any gap by repeating the last one.
                while (xs.Count < s.Frame)
                {
                    xs.Add(xs.Count > 0 ? xs[^1] : 0); ys.Add(ys.Count > 0 ? ys[^1] : 0); shapes.Add(shapes.Count > 0 ? shapes[^1] : (ushort)0);
                }
                if (xs.Count > s.Frame) continue;
                xs.Add(s.X); ys.Add(s.Y); shapes.Add(s.Visible ? (ushort)Math.Clamp(s.Shape, 0, ushort.MaxValue) : (ushort)0);
            }
        }
        return new CursorTrack(xs.ToArray(), ys.ToArray(), shapes.ToArray(), images);
    }

    /// <summary>Alpha-blends the cursor for <paramref name="frame"/> onto a BGRA image of the full movie size.</summary>
    public void Draw(long frame, Span<byte> bgra, int width, int height) => Draw(frame, bgra, width, height, 1.0);

    /// <summary>Draws onto an image scaled by <paramref name="scale"/> relative to the movie (for previews).</summary>
    public void Draw(long frame, Span<byte> bgra, int width, int height, double scale)
    {
        if (frame < 0 || frame >= _x.Length) return;
        int id = _shape[frame];
        if (id == 0 || !_images.TryGetValue(id, out var img)) return;
        int left = (int)Math.Round((_x[frame] - img.HotX) * scale);
        int top = (int)Math.Round((_y[frame] - img.HotY) * scale);
        int dw = Math.Max(1, (int)Math.Round(img.Width * scale)), dh = Math.Max(1, (int)Math.Round(img.Height * scale));
        for (int dy = 0; dy < dh; dy++)
        {
            int y = top + dy;
            if ((uint)y >= (uint)height) continue;
            int sy = dh == img.Height ? dy : dy * img.Height / dh;
            for (int dx = 0; dx < dw; dx++)
            {
                int x = left + dx;
                if ((uint)x >= (uint)width) continue;
                int sx = dw == img.Width ? dx : dx * img.Width / dw;
                int s = (sy * img.Width + sx) * 4;
                int a = img.Bgra[s + 3];
                if (a == 0) continue;
                int d = (y * width + x) * 4;
                if (a == 255)
                {
                    bgra[d] = img.Bgra[s]; bgra[d + 1] = img.Bgra[s + 1]; bgra[d + 2] = img.Bgra[s + 2];
                }
                else
                {
                    int ia = 255 - a;
                    bgra[d] = (byte)((img.Bgra[s] * a + bgra[d] * ia) / 255);
                    bgra[d + 1] = (byte)((img.Bgra[s + 1] * a + bgra[d + 1] * ia) / 255);
                    bgra[d + 2] = (byte)((img.Bgra[s + 2] * a + bgra[d + 2] * ia) / 255);
                }
                bgra[d + 3] = 255;
            }
        }
    }
}
