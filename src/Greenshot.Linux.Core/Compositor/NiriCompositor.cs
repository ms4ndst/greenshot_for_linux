/*
 * Greenshot - a free and open source screenshot tool
 * Copyright (C) 2007-2026 Thomas Braun, Jens Klingen, Robin Krom
 *
 * For more information see: https://getgreenshot.org/
 * The Greenshot project is hosted on GitHub https://github.com/greenshot/greenshot
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.Text.Json;
using Greenshot.Linux.Core.Capturing;
using Greenshot.Linux.Core.Configuration;
using SkiaSharp;

namespace Greenshot.Linux.Core.Compositor;

/// <summary>
/// niri through "niri msg". niri doesn't tell where a window is on the screen (its windows scroll), so the capture screen can't
/// snap to windows; instead niri picks a window (pick-window) and captures it alone (screenshot-window).
/// </summary>
public sealed class NiriCompositor : ICompositor, IWindowCapturer
{
    public string Name => "niri";

    public bool KnowsWindows => false;

    public Task<ScreenLayout?> GetLayoutAsync() => Task.FromResult<ScreenLayout?>(null);

    public Task<WindowInfo?> GetActiveWindowAsync() => Task.FromResult<WindowInfo?>(null);

    public Task<IReadOnlyList<WindowInfo>> GetVisibleWindowsAsync() => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());

    public async Task<WindowInfo?> GetFocusedWindowAsync()
    {
        using var json = await QueryAsync(["focused-window"]).ConfigureAwait(false);
        return json == null ? null : ParseWindow(json.RootElement);
    }

    public async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync()
    {
        using var json = await QueryAsync(["windows"]).ConfigureAwait(false);
        if (json is not { RootElement.ValueKind: JsonValueKind.Array })
        {
            return Array.Empty<WindowInfo>();
        }

        // The focused window first, then the others by the time they had the focus, the latest first
        return json.RootElement.EnumerateArray()
            .Select(w => (Window: ParseWindow(w), FocusedAt: ReadFocusTime(w)))
            .Where(w => w.Window != null)
            .OrderByDescending(w => w.Window!.Focused)
            .ThenByDescending(w => w.FocusedAt)
            .Select(w => w.Window!)
            .ToList();
    }

    public async Task<WindowInfo?> PickWindowAsync(CancellationToken cancellationToken)
    {
        // Waits until the user clicks a window or presses Escape
        using var json = await QueryAsync(["pick-window"], TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        return json == null ? null : ParseWindow(json.RootElement);
    }

    public async Task<SKBitmap> CaptureWindowAsync(WindowInfo window, bool includeCursor, CancellationToken cancellationToken)
    {
        var path = Path.Combine(XdgDirectories.RuntimeDir, $"greenshot-window-{Guid.NewGuid():N}.png");
        try
        {
            // niri always puts the screenshot on the clipboard too, there is no option against it
            var result = await ProcessRunner.RunAsync("niri",
                ["msg", "action", "screenshot-window", "--id", window.Id ?? string.Empty, "--write-to-disk", "true",
                    "--show-pointer", includeCursor ? "true" : "false", "--path", path],
                timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new CaptureException($"niri could not capture the window: {result.Error.Trim()}");
            }

            // niri writes the file after the command returned
            var image = await WaitForImageAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new CaptureException("niri did not save the picture of the window");
            using (image)
            {
                return TrimShadow(image, window.Bounds.Width, window.Bounds.Height);
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// niri draws the window's shadow around it, half transparent: the window is the opaque part, of the window's size
    /// </summary>
    internal static SKBitmap TrimShadow(SKBitmap image, int windowWidth, int windowHeight)
    {
        if (windowWidth <= 0 || windowHeight <= 0 || (image.Width <= windowWidth && image.Height <= windowHeight))
        {
            return image.Copy();
        }

        int left = int.MaxValue, top = int.MaxValue;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).Alpha == 255)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                }
            }
        }

        // A see-through window (a translucent terminal) has no opaque pixels: then the picture is kept as it is
        if (left == int.MaxValue)
        {
            return image.Copy();
        }

        var area = SKRectI.Create(left, top, windowWidth, windowHeight);
        area.Intersect(new SKRectI(0, 0, image.Width, image.Height));
        using var subset = new SKBitmap();
        image.ExtractSubset(subset, area);
        return subset.Copy();
    }

    /// <summary>
    /// A window of "niri msg --json windows", "focused-window" or "pick-window"; the bounds have the window's size, not its place
    /// </summary>
    internal static WindowInfo? ParseWindow(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("id", out var id))
        {
            return null;
        }

        int width = 0, height = 0;
        if (window.TryGetProperty("layout", out var layout) && layout.TryGetProperty("window_size", out var size) && size.GetArrayLength() == 2)
        {
            width = size[0].GetInt32();
            height = size[1].GetInt32();
        }

        return new WindowInfo(
            ReadString(window, "title") ?? string.Empty,
            ReadString(window, "app_id"),
            new LayoutRect(0, 0, width, height),
            window.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : 0,
            id.GetRawText(),
            window.TryGetProperty("is_focused", out var focused) && focused.ValueKind == JsonValueKind.True);
    }

    private static double ReadFocusTime(JsonElement window) =>
        window.TryGetProperty("focus_timestamp", out var time) && time.ValueKind == JsonValueKind.Object
            ? time.GetProperty("secs").GetDouble() + time.GetProperty("nanos").GetDouble() / 1e9
            : 0;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<SKBitmap?> WaitForImageAsync(string path, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var image = SKBitmap.Decode(path);
                    if (image != null)
                    {
                        return image;
                    }
                }
                catch (IOException)
                {
                    // Still being written
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove {path}", ex);
        }
    }

    private static async Task<JsonDocument?> QueryAsync(string[] request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("niri", ["msg", "--json", .. request], timeout: timeout ?? TimeSpan.FromSeconds(3),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return JsonDocument.Parse(result.Output);
            }

            Log.Warn($"niri msg {string.Join(' ', request)} failed: {result.Error.Trim()}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"niri msg {string.Join(' ', request)} failed", ex);
        }

        return null;
    }
}
