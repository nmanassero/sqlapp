using System.Diagnostics;
using System.Drawing.Drawing2D;
using CheckBoxState = System.Windows.Forms.VisualStyles.CheckBoxState;
using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>Lista de tablas emparejadas origen/destino, comparación de datos y despliegue.</summary>
internal sealed class ResultsPage : UserControl
{
    private const int ColType = 0, ColSrcRows = 1, ColSrcName = 2, ColCheck = 3, ColTgtName = 4, ColTgtRows = 5, ColDiff = 6, ColWhere = 7, ColStatus = 8;

    private readonly AppSettings _settings;
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new() { PlaceholderText = "Buscar tabla...", Width = 220 };
    private readonly Button _compare = new() { Text = "Comparar datos", AutoSize = true };
    private readonly Button _viewDiff = new() { Text = "Ver diferencias", AutoSize = true };
    private readonly Button _selectAll = new() { Text = "Seleccionar todas", AutoSize = true };
    private readonly Button _selectNone = new() { Text = "Ninguna", AutoSize = true };
    private readonly Button _back = new() { Text = "‹ Editar conexiones", AutoSize = true };
    private readonly Button _reload = new() { Text = "Recargar tablas", AutoSize = true };
    private readonly Panel _notice = new() { Dock = DockStyle.Top, Height = 30, BackColor = Theme.WarningBack, Visible = false };
    private readonly Label _noticeText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(30, 0, 0, 0) };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, Font = new Font("Consolas", 9f) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 160, Visible = false };
    private readonly Label _statusLabel = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _cancel = new() { Text = "Cancelar", AutoSize = true, Visible = false };
    private readonly Label _selectedLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Width = 110, Height = 40, ForeColor = Color.FromArgb(90, 90, 90) };
    private readonly ArrowButton _deploy = new() { Text = "Desplegar..." };

    private readonly List<TableItem> _items = new();
    private readonly Dictionary<TableGroup, GroupHeader> _groups = new();
    private SqlConnectionInfo _source = new(), _target = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public ResultsPage(AppSettings settings)
    {
        _settings = settings;
        Font = Theme.Base;
        BackColor = Color.White;

        // Contenido central del encabezado: "Seleccionadas X de N" + Desplegar.
        CenterContent = new Panel { Size = new Size(280, 44), BackColor = Theme.SourceBack };
        _selectedLabel.Location = new Point(0, 2);
        _deploy.Location = new Point(122, 2);
        CenterContent.Controls.Add(_selectedLabel);
        CenterContent.Controls.Add(_deploy);
        _deploy.Click += async (_, _) => await DeployAsync();

        // Barra de herramientas
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0), WrapContents = false, BackColor = Color.White };
        toolbar.Controls.AddRange(new Control[] { _back, _reload, Spacer(16), _search, Spacer(16), _selectAll, _selectNone, Spacer(16), _compare, _viewDiff });
        _search.Margin = new Padding(3, 4, 3, 3);
        _search.TextChanged += (_, _) => Rebuild();
        _selectAll.Click += (_, _) => { foreach (var i in VisibleItems().Where(i => i.Selectable)) i.Selected = true; Rebuild(); };
        _selectNone.Click += (_, _) => { foreach (var i in _items) i.Selected = false; Rebuild(); };
        _compare.Click += async (_, _) => await CompareSelectedAsync();
        _viewDiff.Click += (_, _) => ShowDiff(CurrentItem);
        _back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        _reload.Click += async (_, _) => await LoadAsync(_source, _target);

        var close = new Label { Text = "×", Font = new Font("Segoe UI", 12f), Dock = DockStyle.Right, Width = 30, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, ForeColor = Theme.TargetBack };
        close.Click += (_, _) => _notice.Visible = false;
        _noticeText.Paint += (_, e) => Theme.DrawWarning(e.Graphics, new Rectangle(8, (_noticeText.Height - 16) / 2, 18, 16));
        _notice.Controls.Add(_noticeText);
        _notice.Controls.Add(close);

        // Parte inferior: estado + log
        var statusBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 3, 8, 0), WrapContents = false };
        statusBar.Controls.AddRange(new Control[] { _progress, _cancel, _statusLabel });
        _progress.Margin = new Padding(3, 7, 3, 3);
        _cancel.Click += (_, _) => { _cts?.Cancel(); _statusLabel.Text = "Cancelando..."; };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 170 };
        bottom.Controls.Add(_log);
        bottom.Controls.Add(statusBar);
        var splitter = new Splitter { Dock = DockStyle.Bottom, Height = 4, BackColor = Theme.Separator };

        SetupGrid();
        Controls.Add(_grid);
        Controls.Add(splitter);
        Controls.Add(bottom);
        Controls.Add(_notice);
        Controls.Add(toolbar);
        UpdateSelectionInfo();
    }

    public event EventHandler? BackRequested;

    /// <summary>Panel que el formulario ubica en el centro de la banda superior.</summary>
    public Panel CenterContent { get; }

    private static Control Spacer(int w) => new Panel { Width = w, Height = 1 };

    // ------------------------------------------------------------------ grilla

    private void SetupGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.BackgroundColor = Color.White;
        g.BorderStyle = BorderStyle.None;
        g.AllowUserToAddRows = g.AllowUserToDeleteRows = g.AllowUserToResizeRows = false;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Color.FromArgb(238, 238, 238);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 30;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.Font = Theme.Base;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.White;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
        g.DefaultCellStyle.SelectionForeColor = Color.Black;
        g.RowTemplate.Height = 22;
        g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(g, true);

        DataGridViewTextBoxColumn Col(string header, int width, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                HeaderText = header, Width = width, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = align },
                HeaderCell = { Style = { Alignment = align } },
            };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = 140; }
            return c;
        }

        g.Columns.Add(Col("Tipo", 74));
        g.Columns.Add(Col("Filas", 90, DataGridViewContentAlignment.MiddleRight));
        g.Columns.Add(Col("Tabla (origen)", 200, DataGridViewContentAlignment.MiddleRight, fill: true));
        g.Columns.Add(Col("", 34, DataGridViewContentAlignment.MiddleCenter));
        g.Columns.Add(Col("Tabla (destino)", 200, fill: true));
        g.Columns.Add(Col("Filas", 90, DataGridViewContentAlignment.MiddleRight));
        g.Columns.Add(Col("Diferencias de datos", 190));
        var where = Col("Filtro WHERE (opcional)", 200);
        where.ReadOnly = false;
        where.ToolTipText = "Condición sin la palabra WHERE, p.ej. Fecha >= '2024-01-01'. Se aplica en origen y a las filas de destino que se reemplazan o eliminan.";
        g.Columns.Add(where);
        g.Columns.Add(Col("Estado", 220));

        g.CellPainting += Grid_CellPainting;
        g.RowPostPaint += (_, e) =>
        {
            // El texto del grupo abarca las columnas Filas + Tabla (origen); se dibuja después de las celdas.
            if (g.Rows[e.RowIndex].Tag is not GroupHeader gh) return;
            var a = g.GetCellDisplayRectangle(ColSrcRows, e.RowIndex, false);
            var b = g.GetCellDisplayRectangle(ColSrcName, e.RowIndex, false);
            if (a.Width == 0 && b.Width == 0) return;
            var span = a.Width == 0 ? b : b.Width == 0 ? a : Rectangle.Union(a, b);
            span.Inflate(-4, 0);
            TextRenderer.DrawText(e.Graphics, gh.Text, Theme.Bold, span, Color.FromArgb(50, 50, 50),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        g.CellMouseClick += Grid_CellMouseClick;
        g.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != ColWhere && e.ColumnIndex != ColCheck) ShowDiff(ItemAt(e.RowIndex)); };
        g.CellBeginEdit += (_, e) => { if (ItemAt(e.RowIndex) is not { Selectable: true } || _busy) e.Cancel = true; };
        g.CellEndEdit += (_, e) =>
        {
            if (ItemAt(e.RowIndex) is { } item)
            {
                string w = Convert.ToString(g.Rows[e.RowIndex].Cells[ColWhere].Value)?.Trim() ?? "";
                if (w != item.Where)
                {
                    item.Where = w;
                    item.Counts = null;   // la comparación anterior ya no corresponde
                    item.Status = w.Length > 0 ? "Filtro cambiado: vuelva a comparar" : "";
                    _ = item.DisposeTransferAsync();
                    BeginInvoke(Rebuild);
                }
            }
        };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && !g.IsCurrentCellInEditMode && CurrentItem is { Selectable: true } it && !_busy)
            {
                it.Selected = !it.Selected;
                e.Handled = true;
                Rebuild();
            }
        };
    }

    private TableItem? ItemAt(int row) => row >= 0 && row < _grid.Rows.Count ? _grid.Rows[row].Tag as TableItem : null;
    private TableItem? CurrentItem => _grid.CurrentRow == null ? null : ItemAt(_grid.CurrentRow.Index);

    private IEnumerable<TableItem> VisibleItems()
    {
        string f = _search.Text.Trim();
        return f.Length == 0 ? _items : _items.Where(i => i.Name.ToString().Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reconstruye las filas de la grilla agrupadas, conservando scroll y selección.</summary>
    private void Rebuild()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var current = CurrentItem;
        int firstRow = _grid.FirstDisplayedScrollingRowIndex;

        foreach (var gh in _groups.Values) gh.Items.Clear();
        foreach (var item in VisibleItems())
        {
            if (!_groups.TryGetValue(item.Group, out var gh))
                _groups[item.Group] = gh = new GroupHeader { Group = item.Group };
            gh.Items.Add(item);
        }

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        var rows = new List<DataGridViewRow>();
        foreach (var gh in _groups.Values.Where(x => x.Items.Count > 0).OrderBy(x => x.Group))
        {
            var hr = new DataGridViewRow { Tag = gh, Height = 26, ReadOnly = true };
            hr.CreateCells(_grid);
            rows.Add(hr);
            if (gh.Collapsed) continue;
            foreach (var it in gh.Items.OrderBy(i => i.Name.Schema).ThenBy(i => i.Name.Name))
            {
                var r = new DataGridViewRow { Tag = it };
                r.CreateCells(_grid,
                    "Tabla",
                    it.SourceRows?.ToString("N0") ?? "",
                    it.InSource ? it.Name.ToString() : "",
                    "",
                    it.InTarget ? it.Name.ToString() : "",
                    it.TargetRows?.ToString("N0") ?? "",
                    "",
                    it.Where,
                    it.Error ?? it.Status);
                r.ReadOnly = !it.Selectable;
                if (it.Group == TableGroup.Identical) r.DefaultCellStyle.ForeColor = Theme.IdenticalText;
                if (it.Error != null) r.Cells[ColStatus].Style.ForeColor = Theme.Delete;
                if (it.Error != null) r.Cells[ColStatus].ToolTipText = it.Error;
                rows.Add(r);
            }
        }
        _grid.Rows.AddRange(rows.ToArray());
        _grid.ResumeLayout();

        if (firstRow >= 0 && firstRow < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = firstRow;
        if (current != null)
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Tag == current) { _grid.CurrentCell = r.Cells[ColSrcName]; break; }
        UpdateSelectionInfo();
    }

    private void UpdateSelectionInfo()
    {
        int sel = _items.Count(i => i.Selected), total = _items.Count(i => i.Selectable);
        _selectedLabel.Text = $"Seleccionadas\n{sel} de {total}";
        _deploy.Enabled = sel > 0 && !_busy;
        _compare.Enabled = sel > 0 && !_busy;
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics == null) return;
        var row = _grid.Rows[e.RowIndex];
        var g = e.Graphics;
        var b = e.CellBounds;

        if (row.Tag is GroupHeader gh)
        {
            using (var back = new SolidBrush(Theme.GroupBack)) g.FillRectangle(back, b);
            if (e.ColumnIndex == ColType)
            {
                // [−]/[+] y ícono de estado
                var box = new Rectangle(b.X + 8, b.Y + (b.Height - 13) / 2, 13, 13);
                g.FillRectangle(Brushes.White, box);
                using (var p = new Pen(Theme.TargetBack)) g.DrawRectangle(p, box);
                using (var p = new Pen(Theme.TargetBack, 2))
                {
                    g.DrawLine(p, box.X + 3, box.Y + 6.5f, box.Right - 3, box.Y + 6.5f);
                    if (gh.Collapsed) g.DrawLine(p, box.X + 6.5f, box.Y + 3, box.X + 6.5f, box.Bottom - 3);
                }
                DrawGroupGlyph(g, new Rectangle(box.Right + 10, box.Y - 1, 16, 16), gh.Group);
            }
            else if (e.ColumnIndex == ColCheck && gh.Items.Any(i => i.Selectable))
            {
                int sel = gh.Items.Count(i => i.Selected);
                var state = sel == 0 ? CheckBoxState.UncheckedNormal : sel == gh.Items.Count(i => i.Selectable) ? CheckBoxState.CheckedNormal : CheckBoxState.MixedNormal;
                DrawCheck(g, b, state);
            }
            using (var p = new Pen(Color.FromArgb(215, 215, 215))) g.DrawLine(p, b.Left, b.Bottom - 1, b.Right, b.Bottom - 1);
            e.Handled = true;
            return;
        }

        if (row.Tag is not TableItem it) return;

        bool hatch = (!it.InSource && e.ColumnIndex is ColSrcRows or ColSrcName) || (!it.InTarget && e.ColumnIndex is ColTgtRows or ColTgtName);
        if (hatch)
        {
            using var hb = new HatchBrush(HatchStyle.LightUpwardDiagonal, Theme.Hatch, Color.White);
            g.FillRectangle(hb, b);
            e.Handled = true;
            return;
        }

        if (e.ColumnIndex == ColType)
        {
            e.PaintBackground(b, true);
            bool faded = it.Group == TableGroup.Identical;
            TextRenderer.DrawText(g, "Tabla", Theme.Base, new Rectangle(b.X + 6, b.Y, 44, b.Height), faded ? Theme.IdenticalText : Color.Black, TextFormatFlags.VerticalCenter);
            Theme.DrawTableIcon(g, new Rectangle(b.X + 50, b.Y + (b.Height - 13) / 2, 15, 13), faded);
            e.Handled = true;
        }
        else if (e.ColumnIndex == ColCheck)
        {
            e.PaintBackground(b, true);
            if (it.Selectable)
                DrawCheck(g, b, it.Selected ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);
            if (it.Group == TableGroup.Identical && !it.Selected)
                TextRenderer.DrawText(g, "=", Theme.Base, b, Theme.IdenticalText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            e.Handled = true;
        }
        else if (e.ColumnIndex == ColDiff)
        {
            e.PaintBackground(b, true);
            DrawDiff(g, b, it);
            e.Handled = true;
        }
    }

    private static void DrawCheck(Graphics g, Rectangle cell, CheckBoxState state)
    {
        var sz = CheckBoxRenderer.GetGlyphSize(g, state);
        CheckBoxRenderer.DrawCheckBox(g, new Point(cell.X + (cell.Width - sz.Width) / 2, cell.Y + (cell.Height - sz.Height) / 2), state);
    }

    private static void DrawGroupGlyph(Graphics g, Rectangle r, TableGroup group)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        switch (group)
        {
            case TableGroup.Identical:
                using (var p = new Pen(Theme.Insert, 2.5f))
                    g.DrawLines(p, new[] { new Point(r.X + 2, cy), new Point(cx - 1, r.Bottom - 4), new Point(r.Right - 2, r.Y + 3) });
                break;
            case TableGroup.Different:
                using (var p = new Pen(Theme.Update, 2f))
                {
                    g.DrawLine(p, r.X + 2, cy - 3, r.Right - 2, cy - 3);
                    g.DrawLine(p, r.X + 2, cy + 3, r.Right - 2, cy + 3);
                    g.DrawLine(p, r.Right - 4, r.Y + 1, r.X + 4, r.Bottom - 1);
                }
                break;
            case TableGroup.OnlySource:
            case TableGroup.OnlyTarget:
                using (var b = new SolidBrush(group == TableGroup.OnlySource ? Theme.OnlySourceMark : Theme.OnlyTargetMark))
                    g.FillPolygon(b, group == TableGroup.OnlySource
                        ? new[] { new Point(r.Right - 3, r.Y + 2), new Point(r.Right - 3, r.Bottom - 2), new Point(r.X + 3, cy) }
                        : new[] { new Point(r.X + 3, r.Y + 2), new Point(r.X + 3, r.Bottom - 2), new Point(r.Right - 3, cy) });
                break;
            case TableGroup.Error:
                using (var p = new Pen(Theme.Delete, 2.5f))
                {
                    g.DrawLine(p, r.X + 3, r.Y + 3, r.Right - 3, r.Bottom - 3);
                    g.DrawLine(p, r.Right - 3, r.Y + 3, r.X + 3, r.Bottom - 3);
                }
                break;
            default:
                using (var p = new Pen(Color.Gray, 1.5f)) g.DrawEllipse(p, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                break;
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    private static void DrawDiff(Graphics g, Rectangle b, TableItem it)
    {
        var c = it.Counts;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        var r = new Rectangle(b.X + 6, b.Y, b.Width - 6, b.Height);
        if (c == null)
        {
            if (it.InSource && it.InTarget)
                TextRenderer.DrawText(g, "sin comparar", Theme.Small, r, Theme.IdenticalText, flags);
            return;
        }
        if (c.TargetMissing)
        {
            TextRenderer.DrawText(g, $"tabla nueva: {c.ToInsert:N0} {(c.ToInsert == 1 ? "fila" : "filas")}", Theme.Base, r, Theme.Insert, flags);
            return;
        }
        if (!c.HasDifferences)
        {
            TextRenderer.DrawText(g, "= idénticas", Theme.Base, r, Theme.IdenticalText, flags);
            return;
        }
        int x = r.X;
        foreach (var (txt, color) in new[] { ($"+{c.ToInsert:N0}", Theme.Insert), ($"~{c.ToUpdate:N0}", Theme.Update), ($"−{c.ToDelete:N0}", Theme.Delete) })
        {
            var sz = TextRenderer.MeasureText(g, txt, Theme.Bold, Size.Empty, flags);
            TextRenderer.DrawText(g, txt, Theme.Bold, new Rectangle(x, r.Y, sz.Width + 2, r.Height), color, flags);
            x += sz.Width + 12;
        }
    }

    private void Grid_CellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.Button != MouseButtons.Left || _busy) return;
        var tag = _grid.Rows[e.RowIndex].Tag;
        if (tag is GroupHeader gh)
        {
            if (e.ColumnIndex == ColCheck)
            {
                bool all = gh.Items.Where(i => i.Selectable).All(i => i.Selected);
                foreach (var i in gh.Items.Where(i => i.Selectable)) i.Selected = !all;
            }
            else gh.Collapsed = !gh.Collapsed;
            Rebuild();
        }
        else if (tag is TableItem { Selectable: true } it && e.ColumnIndex == ColCheck)
        {
            it.Selected = !it.Selected;
            _grid.InvalidateRow(e.RowIndex);
            // Actualiza el checkbox del encabezado del grupo.
            for (int i = e.RowIndex; i >= 0; i--)
                if (_grid.Rows[i].Tag is GroupHeader) { _grid.InvalidateRow(i); break; }
            UpdateSelectionInfo();
        }
    }

    // ------------------------------------------------------------------ operaciones

    private void Log(string text) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");

    private void ShowNotice(string? text)
    {
        _noticeText.Text = text;
        _notice.Visible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy, string status = "")
    {
        _busy = busy;
        _progress.Visible = _cancel.Visible = busy;
        _statusLabel.Text = status;
        foreach (var c in new Control[] { _back, _reload, _selectAll, _selectNone, _viewDiff, _search }) c.Enabled = !busy;
        _grid.Columns[ColWhere].ReadOnly = busy;
        UpdateSelectionInfo();
    }

    private TransferOptions CurrentOptions(TableItem item, TransferMode mode) => new()
    {
        Mode = mode,
        Where = string.IsNullOrWhiteSpace(item.Where) ? null : item.Where,
        DeleteExtraRows = _settings.DeleteExtraRows,
        FireTriggers = _settings.FireTriggers,
        CheckConstraints = _settings.CheckConstraints,
        TableLock = _settings.TableLock,
        AllowDirectSameServer = _settings.AllowDirectSameServer,
        BatchSize = _settings.BatchSize,
    };

    public async Task LoadAsync(SqlConnectionInfo source, SqlConnectionInfo target)
    {
        await DisposeTransfersAsync();
        _source = source;
        _target = target;
        var previous = _items.ToDictionary(i => i.Name, i => (i.Selected, i.Where));
        _items.Clear();
        _groups.Clear();
        ShowNotice(null);
        Rebuild();
        SetBusy(true, "Leyendo tablas...");
        try
        {
            var srcTask = ListAsync(source);
            var tgtTask = ListAsync(target);
            var src = await srcTask;
            var tgt = await tgtTask;
            var map = new Dictionary<TableName, TableItem>();
            foreach (var t in src) map[t.Table] = new TableItem { Name = t.Table, SourceRows = t.RowCount };
            foreach (var t in tgt)
            {
                // Emparejamiento sin distinguir mayúsculas.
                var key = map.Keys.FirstOrDefault(k => string.Equals(k.Schema, t.Table.Schema, StringComparison.OrdinalIgnoreCase)
                                                       && string.Equals(k.Name, t.Table.Name, StringComparison.OrdinalIgnoreCase));
                if (key != null) map[key].TargetRows = t.RowCount;
                else map[t.Table] = new TableItem { Name = t.Table, TargetRows = t.RowCount };
            }
            foreach (var it in map.Values)
            {
                if (previous.TryGetValue(it.Name, out var p)) { it.Selected = p.Selected && it.Selectable; it.Where = p.Where; }
                _items.Add(it);
            }
            Log($"Origen {source.Display}: {src.Count} tablas. Destino {target.Display}: {tgt.Count} tablas.");
            ShowNotice("Los números de filas son aproximados (sys.partitions). Seleccione tablas y use \"Comparar datos\" para ver diferencias, o \"Desplegar...\" directamente.");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Error al leer tablas", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            Rebuild();
        }
    }

    private static async Task<List<TableSummary>> ListAsync(SqlConnectionInfo c)
    {
        await using var conn = await c.OpenAsync();
        return await MetadataReader.ListTablesAsync(conn);
    }

    private async Task CompareSelectedAsync()
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count == 0) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Comparando...");
        var total = Stopwatch.StartNew();
        try
        {
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                await item.DisposeTransferAsync();
                item.Error = null;
                item.Counts = null;
                item.Status = "Comparando...";
                Rebuild();
                var sw = Stopwatch.StartNew();
                var progress = new Progress<string>(m => { item.Status = m; _statusLabel.Text = $"{item.Name}: {m}"; RefreshItem(item); });
                try
                {
                    item.Transfer = await TableTransfer.PrepareAsync(_source, _target, item.Name, CurrentOptions(item, TransferMode.Sync), ct);
                    foreach (var w in item.Transfer.Warnings) Log($"{item.Name}: {w}");
                    item.Counts = await item.Transfer.CompareAsync(progress, ct);
                    var c = item.Counts;
                    item.Status = $"Comparada en {sw.Elapsed.TotalSeconds:N1} s" + (item.Transfer.UsesDirectRead ? " (lectura directa)" : "");
                    Log(c.TargetMissing
                        ? $"{item.Name}: no existe en destino, {c.ToInsert:N0} filas a copiar."
                        : $"{item.Name}: +{c.ToInsert:N0} ~{c.ToUpdate:N0} −{c.ToDelete:N0} ={c.Identical:N0} ({sw.Elapsed.TotalSeconds:N1} s)");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await item.DisposeTransferAsync();
                    item.Error = ex.Message;
                    item.Status = "";
                    Log($"{item.Name}: ERROR {ex.Message}");
                }
                Rebuild();
            }
            Log($"Comparación terminada en {total.Elapsed.TotalSeconds:N1} s.");
        }
        catch (OperationCanceledException)
        {
            foreach (var i in selected.Where(i => i.Counts == null && i.Error == null)) i.Status = "Cancelado";
            Log("Comparación cancelada.");
        }
        finally
        {
            SetBusy(false);
            Rebuild();
        }
    }

    private void RefreshItem(TableItem item)
    {
        foreach (DataGridViewRow r in _grid.Rows)
            if (r.Tag == item)
            {
                r.Cells[ColStatus].Value = item.Error ?? item.Status;
                _grid.InvalidateRow(r.Index);
                break;
            }
    }

    private async Task DeployAsync()
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count == 0) return;
        using (var dlg = new DeployForm(_settings, selected, _source, _target))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
        }
        _settings.Save();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Desplegando...");
        var total = Stopwatch.StartNew();
        int ok = 0, failed = 0;
        try
        {
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                item.Error = null;
                var progress = new Progress<string>(m => { item.Status = m; _statusLabel.Text = $"{item.Name}: {m}"; RefreshItem(item); });
                try
                {
                    var opt = CurrentOptions(item, _settings.Mode);
                    var t = item.Transfer;
                    // Se reutiliza la comparación (datos ya transferidos) si corresponde al mismo modo y filtro.
                    bool reuse = t != null && opt.Mode == TransferMode.Sync && t.Options.Mode == TransferMode.Sync
                                 && t.Options.Where == opt.Where && t.Options.AllowDirectSameServer == opt.AllowDirectSameServer;
                    if (!reuse)
                    {
                        await item.DisposeTransferAsync();
                        t = item.Transfer = await TableTransfer.PrepareAsync(_source, _target, item.Name, opt, ct);
                    }
                    else
                    {
                        t!.SetApplyOptions(opt.DeleteExtraRows, opt.FireTriggers, opt.CheckConstraints, opt.TableLock, opt.BatchSize);
                    }
                    var r = await t.ExecuteAsync(progress, ct);
                    foreach (var m in r.Messages) Log($"{item.Name}: {m}");
                    item.Status = $"Desplegada: +{r.Inserted:N0} ~{r.Updated:N0} −{r.Deleted:N0} en {r.Elapsed.TotalSeconds:N1} s";
                    item.Counts = null;
                    item.Selected = false;
                    Log($"{item.Name}: {item.Status}");
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.Error = "Error (sin cambios, se revirtió la transacción): " + ex.Message;
                    Log($"{item.Name}: ERROR {ex.Message}");
                    failed++;
                }
                finally
                {
                    await item.DisposeTransferAsync();
                }
                Rebuild();
            }
        }
        catch (OperationCanceledException)
        {
            Log("Despliegue cancelado. La tabla en curso se revirtió; las anteriores quedaron desplegadas.");
        }
        finally
        {
            SetBusy(false);
        }
        Log($"Despliegue terminado en {total.Elapsed.TotalSeconds:N1} s: {ok} correctas, {failed} con error.");
        await RefreshTargetCountsAsync();
        Rebuild();
        if (failed > 0)
            MessageBox.Show(this, $"{failed} tabla(s) con error. Vea el detalle en la columna Estado y en el registro.", "Despliegue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task RefreshTargetCountsAsync()
    {
        try
        {
            var tgt = await ListAsync(_target);
            foreach (var it in _items)
            {
                var t = tgt.FirstOrDefault(x => string.Equals(x.Table.Schema, it.Name.Schema, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(x.Table.Name, it.Name.Name, StringComparison.OrdinalIgnoreCase));
                it.TargetRows = t?.RowCount;
            }
        }
        catch (Exception ex)
        {
            Log("No se pudieron actualizar los conteos de destino: " + ex.Message);
        }
    }

    private void ShowDiff(TableItem? item)
    {
        if (item == null || _busy) return;
        if (item.Transfer == null || item.Counts == null)
        {
            MessageBox.Show(this, "Primero compare los datos de esta tabla (seleccionela y use \"Comparar datos\").", "Ver diferencias", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (item.Counts.TargetMissing)
        {
            MessageBox.Show(this, "La tabla no existe en destino: se creará y se copiarán todas las filas.", "Ver diferencias", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var f = new DiffForm(item.Transfer, item.Counts);
        f.ShowDialog(this);
    }

    public async Task DisposeTransfersAsync()
    {
        foreach (var i in _items) await i.DisposeTransferAsync();
    }
}
