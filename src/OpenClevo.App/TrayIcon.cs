using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenClevo.Core;

namespace OpenClevo.App;

/// <summary>Notification-area icon that shows the CPU temperature and offers quick power-mode switching.</summary>
sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon = new();
    readonly HardwareService _hw;
    readonly Dictionary<PowerMode, ToolStripMenuItem> _modeItems = new();
    IntPtr _hIcon;
    string _lastText = "";

    public TrayIcon(HardwareService hw, Action show, Action exit)
    {
        _hw = hw;
        var menu = new ContextMenuStrip();
        menu.Items.Add("OpenClevo'yu aç", null, (_, _) => show());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var (mode, name) in Ui.Modes)
        {
            var item = new ToolStripMenuItem(name);
            item.Click += (_, _) => Safe(() => _hw.SetPowerMode(mode));
            _modeItems[mode] = item;
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
        _icon.Visible = true;

        hw.Updated += Update;
        hw.SettingsChanged += Update;
        Update();
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _icon.ShowBalloonTip(3000, "OpenClevo", ex.Message, ToolTipIcon.Error); }
    }

    void Update()
    {
        foreach (var (m, item) in _modeItems) item.Checked = m == _hw.PowerMode;
        var lines = new List<string> { _hw.PowerMode is { } pm ? "OpenClevo · " + Ui.ModeName(pm) : "OpenClevo" };
        if (_hw.Error != null) lines.Add("Sürücüye erişilemiyor");
        else lines.AddRange(_hw.Fans.Select(f => $"{Ui.FanName(f.Index)} {f.TemperatureC}°C · {f.Rpm} rpm"));
        var tip = string.Join("\n", lines);
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        int temp = _hw.Fans.FirstOrDefault().TemperatureC;
        string text = _hw.Error != null ? "!" : temp.ToString();
        if (text != _lastText) { SetIcon(text, temp); _lastText = text; }
    }

    void SetIcon(string text, int temp)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color bg = temp >= 90 ? Color.FromArgb(255, 93, 93) : temp >= 75 ? Color.FromArgb(255, 165, 50) : Color.FromArgb(79, 140, 255);
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, 12, 12, 180, 90); path.AddArc(19, 0, 12, 12, 270, 90);
                path.AddArc(19, 19, 12, 12, 0, 90); path.AddArc(0, 19, 12, 12, 90, 90);
                path.CloseFigure();
                using var b = new SolidBrush(bg);
                g.FillPath(b, path);
            }
            using var font = new Font("Segoe UI", text.Length > 2 ? 13 : 18, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
        }
        IntPtr h = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(h);
        old?.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = h;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
