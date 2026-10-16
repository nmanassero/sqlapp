using System.Drawing.Drawing2D;

namespace SqlTableSync.App;

/// <summary>
/// Banda superior Origen (gris) → Destino (azul) con la flecha entre ambos.
/// En modo resultados deja un hueco central para "Seleccionadas" y el botón Desplegar.
/// </summary>
internal sealed class HeaderBar : Control
{
    private string _srcServer = "", _srcDb = "", _tgtServer = "", _tgtDb = "";
    private bool _compact;
    private const int Tip = 28;

    public HeaderBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Dock = DockStyle.Top;
        Height = 104;
        Font = Theme.Base;
    }

    /// <summary>Control que se ubica en el centro (sólo en modo compacto).</summary>
    public Control? CenterContent { get; private set; }

    /// <summary>Modo resultados: banda más baja, sin títulos "Origen"/"Destino".</summary>
    public bool Compact
    {
        get => _compact;
        set { _compact = value; Height = value ? 72 : 104; PerformLayout(); Invalidate(); }
    }

    public void SetCenterContent(Control c)
    {
        if (CenterContent != null) Controls.Remove(CenterContent);
        CenterContent = c;
        Controls.Add(c);
        PerformLayout();
    }

    public void SetEndpoints(string srcServer, string srcDb, string tgtServer, string tgtDb)
    {
        _srcServer = srcServer; _srcDb = srcDb; _tgtServer = tgtServer; _tgtDb = tgtDb;
        Invalidate();
    }

    /// <summary>X donde empieza la zona azul.</summary>
    private int Split => _compact && CenterContent != null
        ? CenterContent.Right + 6
        : Width / 2 - Tip / 2;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (CenterContent != null)
        {
            CenterContent.Visible = _compact;
            CenterContent.Left = Math.Max(260, Width / 2 - CenterContent.Width / 2 - 40);
            CenterContent.Top = (Height - CenterContent.Height) / 2;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SourceBack);
        int split = Split;
        int h = Height;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var blue = new SolidBrush(Theme.TargetBack))
        {
            var pts = new[] { new Point(split, 0), new Point(Width, 0), new Point(Width, h), new Point(split, h), new Point(split + Tip, h / 2) };
            g.FillPolygon(blue, pts);
        }
        g.SmoothingMode = SmoothingMode.Default;

        int iconH = _compact ? 38 : 40, iconW = (int)(iconH * 0.75);
        int iconTop = _compact ? (h - iconH) / 2 : h - iconH - 12;

        // Origen (izquierda)
        Theme.DrawDatabase(g, new Rectangle(14, iconTop, iconW, iconH), Color.FromArgb(70, 70, 70), Theme.SourceBack);
        int tx = 14 + iconW + 10;
        TextRenderer.DrawText(g, _srcServer, Theme.HeaderServer, new Point(tx, iconTop - 1), Color.FromArgb(60, 60, 60));
        TextRenderer.DrawText(g, _srcDb, _compact ? Theme.HeaderServer : Theme.HeaderDb, new Point(tx, iconTop + iconH / 2), Color.FromArgb(40, 40, 40));

        // Destino (derecha, alineado a la derecha)
        var right = new Rectangle(Width - 14 - iconW, iconTop, iconW, iconH);
        Theme.DrawDatabase(g, right, Color.White, Theme.TargetBack);
        int rx = right.Left - 10;
        DrawRight(g, _tgtServer, Theme.HeaderServer, rx, iconTop - 1, Color.White);
        DrawRight(g, _tgtDb, Theme.HeaderDb, rx, iconTop + iconH / 2, Color.White);

        if (!_compact)
        {
            var srcTitle = TextRenderer.MeasureText("Origen", Theme.Title);
            TextRenderer.DrawText(g, "Origen", Theme.Title, new Point(split - srcTitle.Width - 18, 12), Color.Black);
            TextRenderer.DrawText(g, "Destino", Theme.Title, new Point(split + Tip + 22, 12), Color.White);
        }
    }

    private static void DrawRight(Graphics g, string text, Font f, int right, int top, Color c)
    {
        var sz = TextRenderer.MeasureText(text, f);
        TextRenderer.DrawText(g, text, f, new Point(right - sz.Width, top), c);
    }
}

/// <summary>Botón azul con forma de flecha ("➜ Desplegar...").</summary>
internal sealed class ArrowButton : Button
{
    public ArrowButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = new Font("Segoe UI", 11f);
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        Size = new Size(150, 40);
    }

    private bool _hover;
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.SourceBack);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int tip = 14;
        var color = !Enabled ? Color.FromArgb(160, 180, 205) : _hover ? Theme.AccentHover : Theme.Accent;
        using var b = new SolidBrush(color);
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new Point(0, 0), new Point(Width - tip, 0), new Point(Width - 1, Height / 2),
            new Point(Width - tip, Height - 1), new Point(0, Height - 1),
        });
        g.FillPath(b, path);
        // Flecha → dibujada + texto
        var textSize = TextRenderer.MeasureText(g, Text, Font);
        int total = 18 + 6 + textSize.Width;
        int x = (Width - tip / 2 - total) / 2, cy = Height / 2;
        using (var p = new Pen(ForeColor, 2.5f))
        {
            g.DrawLine(p, x, cy, x + 15, cy);
            g.DrawLine(p, x + 9, cy - 6, x + 16, cy);
            g.DrawLine(p, x + 9, cy + 6, x + 16, cy);
        }
        TextRenderer.DrawText(g, Text, Font, new Point(x + 24, cy - textSize.Height / 2), ForeColor);
    }
}
