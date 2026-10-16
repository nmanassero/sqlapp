using System.Drawing.Drawing2D;
using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>
/// Estructura de la tabla seleccionada en origen y en destino, lado a lado y alineada por nombre de
/// columna. Resalta columnas faltantes o con tipo / nulabilidad / identity distintos.
/// </summary>
internal sealed class StructurePanel : UserControl
{
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 26, Font = Theme.Bold, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly DataGridView _grid = new();
    private readonly Label _srcIndexes = new() { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(8, 4, 8, 4), ForeColor = Color.FromArgb(70, 70, 70) };
    private readonly Label _tgtIndexes = new() { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(8, 4, 8, 4), ForeColor = Color.FromArgb(70, 70, 70) };
    private readonly Dictionary<TableName, (TableDefinition? Src, TableDefinition? Tgt)> _cache = new();
    private int _version;

    private const int SrcName = 0, SrcType = 1, SrcNull = 2, SrcExtra = 3, Mid = 4, TgtName = 5, TgtType = 6, TgtNull = 7, TgtExtra = 8;

    private sealed record Line(ColumnDefinition? Src, ColumnDefinition? Tgt, bool SrcKey, bool TgtKey)
    {
        public bool Missing => Src is null || Tgt is null;
        public bool Different => !Missing && (Describe(Src!) != Describe(Tgt!) || SrcKey != TgtKey);
    }

    public StructurePanel()
    {
        Font = Theme.Base;
        BackColor = Color.White;

        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.ReadOnly = true;
        g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false;
        g.RowHeadersVisible = false;
        g.BackgroundColor = Color.White;
        g.BorderStyle = BorderStyle.None;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Color.FromArgb(238, 238, 238);
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 26;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
        g.DefaultCellStyle.SelectionForeColor = Color.Black;
        g.RowTemplate.Height = 21;
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true);

        void Col(string header, float weight, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
        {
            var c = new DataGridViewTextBoxColumn
            {
                HeaderText = header, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = weight, DefaultCellStyle = { Alignment = align },
            };
            g.Columns.Add(c);
        }
        Col("Columna (origen)", 22); Col("Tipo", 16); Col("Nulos", 7); Col("Extra", 20);
        Col("", 3);
        g.Columns[Mid].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        g.Columns[Mid].Width = 30;
        Col("Columna (destino)", 22); Col("Tipo", 16); Col("Nulos", 7); Col("Extra", 20);
        g.CellPainting += Grid_CellPainting;

        var indexes = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 54, ColumnCount = 2, BackColor = Theme.SourceBack };
        indexes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        indexes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        indexes.Controls.Add(_srcIndexes, 0, 0);
        indexes.Controls.Add(_tgtIndexes, 1, 0);

        Controls.Add(_grid);
        Controls.Add(indexes);
        Controls.Add(_title);
        ShowMessage("Haga clic en una tabla para ver su estructura en origen y destino.");
    }

    public void ClearCache() => _cache.Clear();

    private void ShowMessage(string text)
    {
        _title.Text = text;
        _title.ForeColor = Color.FromArgb(90, 90, 90);
        _grid.Rows.Clear();
        _srcIndexes.Text = _tgtIndexes.Text = "";
    }

    /// <summary>Carga y muestra la estructura de la tabla (con caché por tabla).</summary>
    public async Task ShowAsync(TableItem? item, SqlConnectionInfo source, SqlConnectionInfo target)
    {
        int version = ++_version;
        if (item == null) { ShowMessage("Haga clic en una tabla para ver su estructura en origen y destino."); return; }

        if (!_cache.TryGetValue(item.Name, out var defs))
        {
            ShowMessage($"Leyendo estructura de {item.Name}...");
            try
            {
                var srcTask = item.InSource ? ReadAsync(source, item.Name) : Task.FromResult<TableDefinition?>(null);
                var tgtTask = item.InTarget ? ReadAsync(target, item.Name) : Task.FromResult<TableDefinition?>(null);
                defs = (await srcTask, await tgtTask);
                _cache[item.Name] = defs;
            }
            catch (Exception ex)
            {
                if (version == _version) ShowMessage("No se pudo leer la estructura: " + ex.Message);
                return;
            }
        }
        if (version != _version) return;   // el usuario ya eligió otra tabla
        Render(item.Name, defs.Src, defs.Tgt);
    }

    private static async Task<TableDefinition?> ReadAsync(SqlConnectionInfo c, TableName t)
    {
        await using var conn = await c.OpenAsync();
        return await MetadataReader.GetTableAsync(conn, t);
    }

    private void Render(TableName name, TableDefinition? src, TableDefinition? tgt)
    {
        var lines = new List<Line>();
        var srcKey = KeyColumns(src);
        var tgtKey = KeyColumns(tgt);
        foreach (var c in src?.Columns ?? new())
        {
            var t = tgt?.Column(c.Name);
            lines.Add(new Line(c, t, srcKey.Contains(c.Name), t != null && tgtKey.Contains(t.Name)));
        }
        foreach (var t in tgt?.Columns ?? new())
            if (src?.Column(t.Name) is null) lines.Add(new Line(null, t, false, tgtKey.Contains(t.Name)));

        int missing = lines.Count(l => l.Missing), different = lines.Count(l => l.Different);
        _title.ForeColor = Color.Black;
        _title.Text = src == null ? $"{name}: sólo existe en destino ({tgt?.Columns.Count} columnas)"
                    : tgt == null ? $"{name}: sólo existe en origen ({src.Columns.Count} columnas); se creará con esta estructura"
                    : missing + different == 0 ? $"{name}: estructura idéntica ({src.Columns.Count} columnas)"
                    : $"{name}: {different} columna(s) distintas, {missing} columna(s) en un solo lado";
        if (src != null && tgt != null && missing + different > 0) _title.ForeColor = Theme.Update;

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var l in lines)
        {
            int i = _grid.Rows.Add(
                l.Src is null ? "" : l.Src.Name + (l.SrcKey ? "  (PK)" : ""), l.Src is null ? "" : TypeText(l.Src), l.Src is null ? "" : (l.Src.IsNullable ? "Sí" : "No"), l.Src is null ? "" : Extra(l.Src),
                "",
                l.Tgt is null ? "" : l.Tgt.Name + (l.TgtKey ? "  (PK)" : ""), l.Tgt is null ? "" : TypeText(l.Tgt), l.Tgt is null ? "" : (l.Tgt.IsNullable ? "Sí" : "No"), l.Tgt is null ? "" : Extra(l.Tgt));
            var row = _grid.Rows[i];
            row.Tag = l;
            if (l.Different)
            {
                // Resalta sólo las celdas que difieren.
                var s = l.Src!; var t = l.Tgt!;
                Mark(row, SrcType, TgtType, TypeText(s) != TypeText(t));
                Mark(row, SrcNull, TgtNull, s.IsNullable != t.IsNullable);
                Mark(row, SrcExtra, TgtExtra, Extra(s) != Extra(t));
                Mark(row, SrcName, TgtName, l.SrcKey != l.TgtKey);
            }
            if (l.Src is { IsCopyable: false } || l.Tgt is { IsCopyable: false })
                row.DefaultCellStyle.ForeColor = Theme.IdenticalText;
        }
        _grid.ResumeLayout();
        _srcIndexes.Text = src == null ? "" : "Origen — " + IndexSummary(src);
        _tgtIndexes.Text = tgt == null ? "" : "Destino — " + IndexSummary(tgt);
    }

    private static void Mark(DataGridViewRow row, int a, int b, bool diff)
    {
        if (!diff) return;
        var back = Color.FromArgb(255, 238, 200);
        row.Cells[a].Style.BackColor = row.Cells[b].Style.BackColor = back;
        row.Cells[a].Style.Font = row.Cells[b].Style.Font = Theme.Bold;
    }

    private static HashSet<string> KeyColumns(TableDefinition? d) =>
        new((d?.PrimaryKey?.KeyColumns ?? new()).Select(k => k.Column), StringComparer.OrdinalIgnoreCase);

    private static string TypeText(ColumnDefinition c)
    {
        if (c.IsComputed) return "calculada";
        return c.IsClr ? c.TypeName : c.SqlType;
    }

    private static string Extra(ColumnDefinition c)
    {
        var parts = new List<string>();
        if (c.IsIdentity) parts.Add($"IDENTITY({c.IdentitySeed},{c.IdentityIncrement})");
        if (c.IsComputed) parts.Add("AS " + c.ComputedDefinition + (c.IsPersisted ? " PERSISTED" : ""));
        if (c.IsRowVersion) parts.Add("rowversion (no se copia)");
        if (c.GeneratedAlwaysType != 0) parts.Add("período temporal (no se copia)");
        if (c.DefaultDefinition != null) parts.Add("DEFAULT " + c.DefaultDefinition);
        if (c.IsRowGuidCol) parts.Add("ROWGUIDCOL");
        return string.Join("  ", parts);
    }

    private static string Describe(ColumnDefinition c) => $"{TypeText(c)}|{c.IsNullable}|{Extra(c)}";

    private static string IndexSummary(TableDefinition d)
    {
        var parts = new List<string>();
        foreach (var ix in d.Indexes)
        {
            string kind = ix.IsPrimaryKey ? "PK" : ix.IsUnique ? "UNIQUE" : "IX";
            parts.Add($"{kind} {ix.Name} ({string.Join(", ", ix.KeyColumns.Select(k => k.Column))})");
        }
        if (parts.Count == 0) parts.Add("sin índices (heap)");
        if (d.EnabledTriggers.Count > 0) parts.Add($"triggers: {string.Join(", ", d.EnabledTriggers)}");
        if (d.ReferencedByForeignKeys > 0) parts.Add($"referenciada por {d.ReferencedByForeignKeys} FK");
        return string.Join("  ·  ", parts);
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics == null || _grid.Rows[e.RowIndex].Tag is not Line l) return;
        bool srcSide = e.ColumnIndex is >= SrcName and <= SrcExtra;
        bool tgtSide = e.ColumnIndex is >= TgtName and <= TgtExtra;
        if ((srcSide && l.Src is null) || (tgtSide && l.Tgt is null))
        {
            using var hb = new HatchBrush(HatchStyle.LightUpwardDiagonal, Theme.Hatch, Color.White);
            e.Graphics.FillRectangle(hb, e.CellBounds);
            e.Handled = true;
        }
        else if (e.ColumnIndex == Mid)
        {
            e.PaintBackground(e.CellBounds, true);
            var color = l.Missing ? Theme.OnlyTargetMark : l.Different ? Theme.Update : Theme.IdenticalText;
            var b = e.CellBounds;
            int cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            using (var p = new Pen(color, 1.6f))
            {
                // "=" o "≠" dibujado (no depende de la fuente)
                e.Graphics.DrawLine(p, cx - 5, cy - 2, cx + 5, cy - 2);
                e.Graphics.DrawLine(p, cx - 5, cy + 2, cx + 5, cy + 2);
                if (l.Missing || l.Different) e.Graphics.DrawLine(p, cx + 3, cy - 6, cx - 3, cy + 6);
            }
            e.Handled = true;
        }
    }
}
