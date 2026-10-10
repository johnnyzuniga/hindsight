using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ResMan.Core;
using Windows.UI.ViewManagement;

namespace ResMan.App.Services;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// The System / Light / Dark choice. Applied by setting RequestedTheme on the window's root, so every
/// ThemeResource repaints in place; remembered in %LOCALAPPDATA%\ResMan\ui.json.
/// </summary>
public sealed class ThemeService
{
    private static string File => Path.Combine(ResManPaths.UserDir, "ui.json");
    private readonly UISettings _ui = new();
    private FrameworkElement? _root;
    private AppWindow? _window;

    public ThemeMode Mode { get; private set; } = Load();
    public event Action? Changed;

    public void Attach(FrameworkElement root, AppWindow window)
    {
        _root = root;
        _window = window;
        // Only matters while following Windows: repaint when the user flips the system theme.
        _ui.ColorValuesChanged += (_, _) => root.DispatcherQueue.TryEnqueue(() => { if (Mode == ThemeMode.System) Apply(); });
        Apply();
    }

    public void Set(ThemeMode mode)
    {
        Mode = mode;
        Save(mode);
        Apply();
    }

    /// <summary>Applies a theme for this session only (e.g. a demo started with --theme), without saving it.</summary>
    public void Preview(ThemeMode mode)
    {
        Mode = mode;
        Apply();
    }

    public bool IsDark => _root?.ActualTheme == ElementTheme.Dark;

    private void Apply()
    {
        if (_root is null) return;
        _root.RequestedTheme = Mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            // Default resolves against the app's theme, which follows Windows, so read it explicitly.
            _ => SystemIsDark() ? ElementTheme.Dark : ElementTheme.Light,
        };
        // Caption button colors follow the page's band; MainWindow repaints them on Changed.
        Changed?.Invoke();
    }

    private bool SystemIsDark()
    {
        // The Background system color is near-black in dark mode.
        var bg = _ui.GetColorValue(UIColorType.Background);
        return bg.R + bg.G + bg.B < 384;
    }

    private static ThemeMode Load()
    {
        try
        {
            if (System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllText(File))?["theme"]?.GetValue<string>() is { } s
                && Enum.TryParse<ThemeMode>(s, true, out var m)) return m;
        }
        catch { }
        return ThemeMode.System;
    }

    private static void Save(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(ResManPaths.UserDir);
            System.IO.File.WriteAllText(File, new JsonObject { ["theme"] = mode.ToString().ToLowerInvariant() }.ToJsonString());
        }
        catch (Exception ex) { Log.Write($"Couldn't save theme: {ex.Message}"); }
    }
}
