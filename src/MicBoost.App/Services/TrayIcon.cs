using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MicBoost.App.Services;

public enum TrayState
{
    Normal,
    Muted,
    Bypassed,
    Problem,
}

/// <summary>Notification-area icon with quick controls.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _mute;
    private readonly ToolStripMenuItem _bypass;
    private readonly ToolStripMenuItem _presets;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private readonly List<IntPtr> _handles = new();
    private bool _balloonShown;

    public TrayIcon(Icon baseIcon, Action show, Action toggleMute, Action toggleBypass, Action exit)
    {
        foreach (TrayState s in Enum.GetValues(typeof(TrayState))) _icons[s] = MakeIcon(baseIcon, s);

        _mute = new ToolStripMenuItem("Mute", null, (_, _) => toggleMute());
        _bypass = new ToolStripMenuItem("Bypass processing", null, (_, _) => toggleBypass());
        _presets = new ToolStripMenuItem("Presets");
        var open = new ToolStripMenuItem("Open MicBoost", null, (_, _) => show()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };

        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false, ShowCheckMargin = true };
        menu.ForeColor = Color.FromArgb(236, 236, 239);
        menu.Items.AddRange(new ToolStripItem[]
        {
            open, new ToolStripSeparator(), _mute, _bypass, _presets, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => exit()),
        });

        _icon = new NotifyIcon
        {
            Icon = _icons[TrayState.Normal],
            Text = "MicBoost",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
    }

    public void Update(TrayState state, bool muted, bool bypassed, string tooltip, string muteHotkey, string bypassHotkey)
    {
        _icon.Icon = _icons[state];
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _mute.Checked = muted;
        _mute.ShortcutKeyDisplayString = muteHotkey;
        _bypass.Checked = bypassed;
        _bypass.ShortcutKeyDisplayString = bypassHotkey;
    }

    public void SetPresets(IEnumerable<string> names, string? active, Action<string> apply)
    {
        _presets.DropDownItems.Clear();
        foreach (var name in names)
        {
            var n = name;
            _presets.DropDownItems.Add(new ToolStripMenuItem(n, null, (_, _) => apply(n)) { Checked = n == active });
        }
    }

    /// <summary>Tells the user once per session that closing the window keeps MicBoost running.</summary>
    public void ShowStillRunningOnce()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(3000, "MicBoost is still running", "The boost stays active. Right-click the tray icon to exit.", ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        foreach (var h in _handles) DestroyIcon(h);
    }

    private Icon MakeIcon(Icon baseIcon, TrayState state)
    {
        int size = SystemInformation.SmallIconSize.Width >= 20 ? 32 : 16;
        using var source = new Icon(baseIcon, size, size);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawIcon(source, new Rectangle(0, 0, size, size));
            Color? dot = state switch
            {
                TrayState.Muted => Color.FromArgb(242, 84, 91),
                TrayState.Bypassed => Color.FromArgb(154, 156, 166),
                TrayState.Problem => Color.FromArgb(245, 177, 76),
                _ => null,
            };
            if (dot is { } c)
            {
                float d = size * 0.5f;
                using var ring = new SolidBrush(Color.FromArgb(15, 16, 18));
                using var fill = new SolidBrush(c);
                g.FillEllipse(ring, size - d - 1, size - d - 1, d + 1, d + 1);
                g.FillEllipse(fill, size - d + 1, size - d + 1, d - 3, d - 3);
            }
        }
        IntPtr h = bmp.GetHicon();
        _handles.Add(h);
        return Icon.FromHandle(h);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(31, 32, 38);
        private static readonly Color Hover = Color.FromArgb(42, 44, 51);
        private static readonly Color Line = Color.FromArgb(44, 46, 54);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Color.FromArgb(54, 211, 153);
        public override Color CheckSelectedBackground => Color.FromArgb(54, 211, 153);
        public override Color CheckPressedBackground => Color.FromArgb(54, 211, 153);
    }
}
