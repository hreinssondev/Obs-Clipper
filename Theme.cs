using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OBSClipper;

// Dark theme colors and the owner-drawn controls that use them. Stock
// WinForms controls ignore dark colors in places (buttons, checkboxes,
// spinners), so the window uses these instead.
static class Theme
{
    public static readonly Color Background = Color.FromArgb(24, 25, 28);
    public static readonly Color Bar = Color.FromArgb(30, 31, 35);
    public static readonly Color Surface = Color.FromArgb(36, 38, 43);
    public static readonly Color SurfaceHover = Color.FromArgb(46, 48, 55);
    public static readonly Color SurfacePressed = Color.FromArgb(56, 59, 67);
    public static readonly Color Input = Color.FromArgb(26, 27, 31);
    public static readonly Color Border = Color.FromArgb(55, 58, 66);
    public static readonly Color Text = Color.FromArgb(232, 233, 237);
    public static readonly Color Muted = Color.FromArgb(148, 151, 162);
    public static readonly Color Off = Color.FromArgb(98, 101, 112);
    public static readonly Color Track = Color.FromArgb(68, 71, 80);
    public static readonly Color Accent = Color.FromArgb(88, 101, 242);
    public static readonly Color AccentHover = Color.FromArgb(110, 121, 246);
    public static readonly Color Success = Color.FromArgb(63, 185, 80);
    public static readonly Color Warning = Color.FromArgb(230, 162, 60);
    public static readonly Color Danger = Color.FromArgb(237, 76, 79);

    public static readonly Font Body = MakeFont(9.75f);
    public static readonly Font Strong = MakeFont(9.75f, FontStyle.Bold);
    public static readonly Font Caption = MakeFont(8f, FontStyle.Bold);

    static Font MakeFont(float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font("Segoe UI Variable Text", size, style);
        if (font.Name == "Segoe UI Variable Text") return font;
        font.Dispose();
        return new Font("Segoe UI", size, style);
    }

    public static Color ParentBack(Control c) => c.Parent?.BackColor ?? Background;

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        if (radius <= 0 || d > r.Width || d > r.Height)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill, Color border = default, float borderWidth = 1)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(r, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (border.A > 0)
        {
            using var pen = new Pen(border, borderWidth);
            g.DrawPath(pen, path);
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    public static void DarkTitleBar(IntPtr hwnd)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CAPTION_COLOR = 35;
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int color = Background.R | Background.G << 8 | Background.B << 16;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref color, sizeof(int));
    }

    public static void DarkScrollbars(Control control)
    {
        if (control.IsHandleCreated) SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        else control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    public static void UserPainted(Control control, Action<ControlStyles, bool> setStyle)
    {
        setStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
}

// A panel with rounded corners; BackColor is the fill.
class RoundedPanel : Panel
{
    public int Radius { get; set; } = 10;
    public Color BorderColor { get; set; }

    public RoundedPanel()
    {
        Theme.UserPainted(this, SetStyle);
        BackColor = Theme.Surface;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.ParentBack(this));
        Theme.FillRounded(e.Graphics, new RectangleF(0, 0, Width - 1, Height - 1), Radius, BackColor, BorderColor);
    }
}

enum ButtonKind { Secondary, Primary, Ghost, Danger, Key }

class ModernButton : Button
{
    bool hover, pressed, highlighted;

    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    public int Radius { get; set; } = 7;
    // Optional symbol drawn after the text in its own (usually larger) font.
    public string Suffix { get; set; } = "";
    public Font? SuffixFont { get; set; }
    const int SuffixGap = 6;

    public bool Highlighted
    {
        get => highlighted;
        set { highlighted = value; Invalidate(); }
    }

    public ModernButton()
    {
        Theme.UserPainted(this, SetStyle);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Padding = new Padding(14, 0, 14, 0);
        Height = 32;
    }

    protected override bool ShowFocusCues => false;

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = TextRenderer.MeasureText(Text, Font).Width;
        if (Suffix.Length > 0) width += SuffixGap + TextRenderer.MeasureText(Suffix, SuffixFont ?? Font).Width;
        return new Size(Math.Max(width + Padding.Horizontal, MinimumSize.Width), Math.Max(32, MinimumSize.Height));
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Theme.ParentBack(this);
        g.Clear(back);
        Color fill, text = Theme.Text, border = default;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = pressed ? Theme.Accent : hover ? Theme.AccentHover : Theme.Accent;
                text = Color.White;
                break;
            case ButtonKind.Ghost:
                fill = pressed ? Theme.SurfacePressed : hover ? Theme.SurfaceHover : back;
                text = hover ? Theme.Text : Theme.Muted;
                break;
            case ButtonKind.Danger:
                fill = pressed ? Color.FromArgb(90, 40, 44) : hover ? Color.FromArgb(70, 36, 40) : back;
                text = hover ? Theme.Danger : Theme.Muted;
                break;
            case ButtonKind.Key:
                fill = pressed ? Theme.SurfacePressed : hover ? Theme.SurfaceHover : Theme.Input;
                border = Theme.Border;
                break;
            default:
                fill = pressed ? Theme.SurfacePressed : hover ? Theme.SurfaceHover : Theme.Surface;
                border = Theme.Border;
                break;
        }
        if (!Enabled)
        {
            text = Theme.Off;
            if (Kind == ButtonKind.Primary) fill = Theme.Surface;
        }
        if (highlighted) border = Theme.Accent;
        Theme.FillRounded(g, new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Radius, fill, border, highlighted ? 2 : 1);
        if (Suffix.Length == 0)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }
        var suffixFont = SuffixFont ?? Font;
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        int textWidth = TextRenderer.MeasureText(g, Text, Font, Size, flags).Width;
        int suffixWidth = TextRenderer.MeasureText(g, Suffix, suffixFont, Size, flags).Width;
        int x = (Width - textWidth - SuffixGap - suffixWidth) / 2;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textWidth, Height), text, flags);
        TextRenderer.DrawText(g, Suffix, suffixFont, new Rectangle(x + textWidth + SuffixGap, -1, suffixWidth, Height), text, flags);
    }
}

class ToggleSwitch : CheckBox
{
    const int TrackWidth = 36, TrackHeight = 20, Gap = 10;
    bool hover;

    public ToggleSwitch()
    {
        Theme.UserPainted(this, SetStyle);
        AutoSize = false;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (string.IsNullOrEmpty(Text)) return new Size(TrackWidth + 2, 26);
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(TrackWidth + Gap + text.Width + 2, Math.Max(26, text.Height + 6));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        float y = (Height - TrackHeight) / 2f;
        var track = new RectangleF(1, y, TrackWidth, TrackHeight);
        Color trackColor = !Enabled ? Theme.Surface
            : Checked ? (hover ? Theme.AccentHover : Theme.Accent)
            : (hover ? Theme.SurfacePressed : Theme.Track);
        Theme.FillRounded(g, track, TrackHeight / 2f, trackColor);
        float knob = TrackHeight - 6;
        float x = Checked ? track.Right - knob - 3 : track.X + 3;
        using (var brush = new SolidBrush(Enabled ? Color.White : Theme.Off))
            g.FillEllipse(brush, x, y + 3, knob, knob);
        if (!string.IsNullOrEmpty(Text))
            TextRenderer.DrawText(g, Text, Font, new Rectangle(TrackWidth + Gap + 1, 0, Width - TrackWidth - Gap, Height),
                Enabled ? ForeColor : Theme.Off, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
    }
}

// Number box with − and + buttons that step by 5 (15, 20, 25 …); any number can be typed.
class Stepper : RoundedPanel
{
    readonly TextBox box;
    readonly ModernButton minus, plus;
    int value;
    bool active = true;

    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 600;
    public int Step { get; set; } = 5;
    public event EventHandler? ValueChanged;

    public Stepper()
    {
        BackColor = Theme.Input;
        BorderColor = Theme.Border;
        Radius = 7;
        Size = new Size(90, 32);
        minus = new ModernButton { Text = "−", Kind = ButtonKind.Ghost, Radius = 5, Padding = Padding.Empty, Bounds = new Rectangle(3, 3, 26, 26) };
        plus = new ModernButton { Text = "+", Kind = ButtonKind.Ghost, Radius = 5, Padding = Padding.Empty, Bounds = new Rectangle(Width - 29, 3, 26, 26) };
        box = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body,
            TextAlign = HorizontalAlignment.Center, Bounds = new Rectangle(31, 7, Width - 62, 20),
        };
        minus.Click += (_, _) => Value = ((value + Step - 1) / Step - 1) * Step;
        plus.Click += (_, _) => Value = (value / Step + 1) * Step;
        box.Leave += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            Commit();
            e.SuppressKeyPress = true;
        };
        box.MouseWheel += (_, e) => { if (active) Value += e.Delta > 0 ? 1 : -1; };
        Controls.Add(minus);
        Controls.Add(box);
        Controls.Add(plus);
    }

    public int Value
    {
        get => value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            bool changed = v != this.value;
            this.value = v;
            box.Text = v.ToString();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Dims the control without disabling it, since disabled text boxes turn light gray.
    public bool Active
    {
        get => active;
        set
        {
            active = value;
            box.ReadOnly = !value;
            box.ForeColor = value ? Theme.Text : Theme.Off;
            minus.Enabled = plus.Enabled = value;
        }
    }

    void Commit()
    {
        if (int.TryParse(box.Text, out int typed)) Value = typed;
        else box.Text = value.ToString();
    }
}

class InputBox : RoundedPanel
{
    public TextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body, Dock = DockStyle.Fill,
    };

    public InputBox()
    {
        BackColor = Theme.Input;
        BorderColor = Theme.Border;
        Radius = 7;
        Padding = new Padding(10, 8, 10, 4);
        Height = 32;
        Controls.Add(Box);
        Box.Enter += (_, _) => { BorderColor = Theme.Accent; Invalidate(); };
        Box.Leave += (_, _) => { BorderColor = Theme.Border; Invalidate(); };
    }
}

class StatusLabel : Control
{
    Color dot = Theme.Off;

    public Color DotColor
    {
        get => dot;
        set { dot = value; Invalidate(); }
    }

    public StatusLabel()
    {
        Theme.UserPainted(this, SetStyle);
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        string.IsNullOrEmpty(Text) ? new Size(0, 32) : new Size(TextRenderer.MeasureText(Text, Font).Width + 18, 32);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        if (string.IsNullOrEmpty(Text)) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, 1, Height / 2f - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(16, 0, Width - 16, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
    }
}

class TabButton : Control
{
    bool selected, hover;

    public bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    public TabButton(string text)
    {
        Theme.UserPainted(this, SetStyle);
        Text = text;
        Font = Theme.Strong;
        Cursor = Cursors.Hand;
        Size = new Size(TextRenderer.MeasureText(text, Font).Width + 28, 40);
        Margin = new Padding(0, 0, 4, 0);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.ParentBack(this));
        var color = selected || hover ? Theme.Text : Theme.Muted;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - 4), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (selected) Theme.FillRounded(g, new RectangleF(10, Height - 3, Width - 20, 3), 1.5f, Theme.Accent);
    }
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Off;
        base.OnRenderItemText(e);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfaceHover;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
    }
}
