using System.Drawing.Drawing2D;

namespace SqlTableSync.App;

internal static class Theme
{
    public static readonly Color SourceBack = Color.FromArgb(240, 240, 240);
    public static readonly Color TargetBack = Color.FromArgb(43, 87, 151);
    public static readonly Color Accent = Color.FromArgb(56, 132, 218);
    public static readonly Color AccentHover = Color.FromArgb(40, 112, 196);
    public static readonly Color Separator = Color.FromArgb(222, 222, 222);
    public static readonly Color GroupBack = Color.FromArgb(232, 232, 232);
    public static readonly Color IdenticalText = Color.FromArgb(150, 150, 150);
    public static readonly Color OnlySourceMark = Color.FromArgb(56, 132, 218);
    public static readonly Color OnlyTargetMark = Color.FromArgb(200, 90, 60);
    public static readonly Color Hatch = Color.FromArgb(236, 236, 236);
    public static readonly Color Insert = Color.FromArgb(30, 140, 60);
    public static readonly Color Update = Color.FromArgb(200, 130, 0);
    public static readonly Color Delete = Color.FromArgb(200, 50, 50);
    public static readonly Color WarningBack = Color.FromArgb(255, 251, 230);

    public static readonly Font Base = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Font HeaderServer = new("Segoe UI", 10f);
    public static readonly Font HeaderDb = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 8.25f);

    /// <summary>Ícono de base de datos (cilindro con 3 discos), como en el encabezado de Redgate.</summary>
    public static void DrawDatabase(Graphics g, Rectangle r, Color color, Color gap)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float eh = r.Height * 0.24f;
        float segment = (r.Height - eh) / 3f;
        using var brush = new SolidBrush(color);
        using var pen = new Pen(gap, Math.Max(1.5f, r.Height / 18f));
        for (int i = 2; i >= 0; i--)
        {
            float y = r.Top + i * segment;
            var body = new RectangleF(r.Left, y + eh / 2, r.Width, segment);
            g.FillRectangle(brush, body);
            g.FillEllipse(brush, r.Left, y + segment, r.Width, eh);
            g.FillEllipse(brush, r.Left, y, r.Width, eh);
            if (i > 0) g.DrawArc(pen, r.Left, y, r.Width, eh, 0, 180);
        }
        g.DrawEllipse(pen, r.Left, r.Top, r.Width, eh);
        g.FillEllipse(brush, r.Left, r.Top, r.Width, eh);
        g.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>Pequeño ícono de tabla (grilla) para la lista de resultados.</summary>
    public static void DrawTableIcon(Graphics g, Rectangle r, bool faded)
    {
        var line = faded ? Color.FromArgb(170, 170, 170) : Color.FromArgb(40, 90, 160);
        var head = faded ? Color.FromArgb(200, 200, 200) : Color.FromArgb(90, 140, 210);
        using var pen = new Pen(line);
        using var hb = new SolidBrush(head);
        g.FillRectangle(Brushes.White, r);
        g.FillRectangle(hb, r.X, r.Y, r.Width, r.Height / 4);
        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        for (int i = 1; i < 3; i++)
        {
            int x = r.X + i * r.Width / 3;
            g.DrawLine(pen, x, r.Y, x, r.Bottom - 1);
        }
        for (int i = 1; i < 4; i++)
        {
            int y = r.Y + i * r.Height / 4;
            g.DrawLine(pen, r.X, y, r.Right - 1, y);
        }
    }

    /// <summary>Triángulo amarillo de advertencia.</summary>
    public static void DrawWarning(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb(245, 190, 0)))
            g.FillPolygon(b, new[] { new Point(r.X + r.Width / 2, r.Y), new Point(r.Right, r.Bottom), new Point(r.X, r.Bottom) });
        using (var p = new Pen(Color.Black, 2f))
        {
            int cx = r.X + r.Width / 2;
            g.DrawLine(p, cx, r.Y + 5, cx, r.Bottom - 6);
            g.DrawLine(p, cx, r.Bottom - 4, cx, r.Bottom - 2);
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>Flecha circular de "actualizar".</summary>
    public static void DrawRefresh(Graphics g, Rectangle r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Math.Max(2.5f, r.Width / 7f);
        var c = RectangleF.Inflate(r, -w, -w);
        using var p = new Pen(color, w);
        g.DrawArc(p, c, 200, 130);
        g.DrawArc(p, c, 20, 130);
        using var b = new SolidBrush(color);
        PointF Tip(float deg, bool clockwise)
        {
            double a = deg * Math.PI / 180;
            return new PointF(c.X + c.Width / 2 + (float)Math.Cos(a) * c.Width / 2, c.Y + c.Height / 2 + (float)Math.Sin(a) * c.Height / 2);
        }
        foreach (float end in new[] { 330f, 150f })
        {
            var t = Tip(end, true);
            double a = end * Math.PI / 180;
            // Dirección tangente (sentido horario) y normal.
            float tx = (float)-Math.Sin(a), ty = (float)Math.Cos(a);
            float nx = (float)Math.Cos(a), ny = (float)Math.Sin(a);
            float s = w * 1.6f;
            g.FillPolygon(b, new[]
            {
                new PointF(t.X + tx * s, t.Y + ty * s),
                new PointF(t.X + nx * s, t.Y + ny * s),
                new PointF(t.X - nx * s, t.Y - ny * s),
            });
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    public static Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand,
            AutoSize = false,
            Height = 34,
            Width = 140,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = TargetBack;
        return b;
    }
}
