using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NhlWorkbench.Core.Dds;

namespace NhlWorkbench.App.Services;

/// <summary>What the app remembers between runs.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NHL Legacy Workbench", "settings.json");

    public string? GamePath { get; set; }

    public string? ProjectPath { get; set; }

    /// <summary>Mod folders used lately, newest first.</summary>
    public List<string> RecentMods { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs the user re-picking their folders next time.
        }
    }
}

public static class Imaging
{
    /// <summary>A display-ready copy, shrunk to at most <paramref name="maxSize"/> pixels across. Safe to make off the UI thread.</summary>
    public static BitmapSource ToBitmap(Rgba32Image image, int maxSize = int.MaxValue)
    {
        int factor = Math.Max(1, Math.Max(image.Width, image.Height) / Math.Max(1, maxSize));
        int width = Math.Max(1, image.Width / factor);
        int height = Math.Max(1, image.Height / factor);
        var bgra = new byte[width * height * 4];

        // Averages in premultiplied form, so transparent pixels don't discolor the edges.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int red = 0, green = 0, blue = 0, alpha = 0;
                for (int sy = y * factor; sy < (y + 1) * factor; sy++)
                {
                    for (int sx = x * factor; sx < (x + 1) * factor; sx++)
                    {
                        int source = ((sy * image.Width) + sx) * 4;
                        int a = image.Pixels[source + 3];
                        red += image.Pixels[source] * a;
                        green += image.Pixels[source + 1] * a;
                        blue += image.Pixels[source + 2] * a;
                        alpha += a;
                    }
                }

                int target = ((y * width) + x) * 4;
                if (alpha > 0)
                {
                    bgra[target] = (byte)(blue / alpha);
                    bgra[target + 1] = (byte)(green / alpha);
                    bgra[target + 2] = (byte)(red / alpha);
                    bgra[target + 3] = (byte)(alpha / (factor * factor));
                }
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}

public static class Dialogs
{
    public static bool Confirm(string title, string message, string confirmText)
    {
        var dialog = new Views.ThemedDialog(title, message, confirmText, null) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }

    public static void Tell(string title, string message)
    {
        new Views.ThemedDialog(title, message, "OK", null, showCancel: false) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>Returns the text entered, or null if cancelled.</summary>
    public static string? Ask(string title, string message, string confirmText, string initial)
    {
        var dialog = new Views.ThemedDialog(title, message, confirmText, initial) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Answer : null;
    }
}
