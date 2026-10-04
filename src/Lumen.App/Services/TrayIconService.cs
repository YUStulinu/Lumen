using System.Drawing;
using System.IO;
using System.Windows;
using Lumen.App.Infrastructure;
using Lumen.Core.Settings;
using Forms = System.Windows.Forms;

namespace Lumen.App.Services;

/// <summary>
/// The icon in the notification area and its context menu.
/// </summary>
/// <remarks>
/// WPF has no tray icon control, so we borrow WinForms' NotifyIcon. Mixing the two works because
/// both are built on Win32 windows and the same message loop: WPF's Dispatcher pumps the
/// messages NotifyIcon's hidden window receives. All callbacks therefore arrive on the UI thread.
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly ISettingsProvider _settings;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _askItem;
    private readonly Forms.ToolStripMenuItem _snipItem;
    private readonly Dictionary<RoutingMode, Forms.ToolStripMenuItem> _modeItems = [];
    private readonly Icon _iconImage;

    public TrayIconService(ISettingsProvider settings)
    {
        _settings = settings;
        _iconImage = LoadIcon();

        _askItem = new Forms.ToolStripMenuItem("Ask about selection", null, (_, _) => AskRequested?.Invoke(this, EventArgs.Empty));
        _snipItem = new Forms.ToolStripMenuItem("Read screen region (OCR)", null, (_, _) => SnipRequested?.Invoke(this, EventArgs.Empty));

        var modeMenu = new Forms.ToolStripMenuItem("Assistant");
        AddMode(modeMenu, RoutingMode.LocalFirst, "Local first, Claude as fallback");
        AddMode(modeMenu, RoutingMode.CloudFirst, "Claude first, local as fallback");
        AddMode(modeMenu, RoutingMode.LocalOnly, "Local only (offline, private)");
        AddMode(modeMenu, RoutingMode.CloudOnly, "Claude only");

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_askItem);
        menu.Items.Add(_snipItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(modeMenu);
        menu.Items.Add("Settings…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Open logs folder", null, (_, _) => OpenFolder(AppPaths.LogDirectory));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Lumen", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon
        {
            Icon = _iconImage,
            Text = "Lumen - AI assistant",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Left click: open the assistant without capturing anything.</summary>
    public event EventHandler? OpenRequested;

    public event EventHandler? AskRequested;

    public event EventHandler? SnipRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler<RoutingMode>? RoutingModeSelected;

    public void Show()
    {
        RefreshMenu();
        _icon.Visible = true;
    }

    public void ShowNotification(string title, string text, bool isError = false) =>
        _icon.ShowBalloonTip(5000, title, text, isError ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);

    private void AddMode(Forms.ToolStripMenuItem parent, RoutingMode mode, string text)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => RoutingModeSelected?.Invoke(this, mode));
        _modeItems[mode] = item;
        parent.DropDownItems.Add(item);
    }

    private void RefreshMenu()
    {
        AppSettings s = _settings.Current;
        _askItem.ShortcutKeyDisplayString = s.Hotkeys.Ask;
        _snipItem.ShortcutKeyDisplayString = s.Hotkeys.Snip;
        foreach ((RoutingMode mode, Forms.ToolStripMenuItem item) in _modeItems)
        {
            item.Checked = mode == s.RoutingMode;
        }
    }

    /// <summary>Loads the embedded .ico at the size Windows uses for tray icons at the current DPI.</summary>
    private static Icon LoadIcon()
    {
        Stream? stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lumen.ico"))?.Stream;
        if (stream is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using (stream)
        {
            return new Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void Dispose()
    {
        // Hide first: otherwise a "ghost" icon stays in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _iconImage.Dispose();
    }
}
