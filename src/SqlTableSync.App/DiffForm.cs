using System.Data;
using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>Muestra una muestra de las filas a insertar, actualizar y eliminar.</summary>
internal sealed class DiffForm : Form
{
    private const int MaxRows = 1000;
    private readonly TableTransfer _transfer;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly HashSet<DiffKind> _loaded = new();

    public DiffForm(TableTransfer transfer, ComparisonCounts counts)
    {
        _transfer = transfer;
        Text = $"Diferencias de datos — {transfer.Table}";
        Font = Theme.Base;
        Size = new Size(1100, 650);
        StartPosition = FormStartPosition.CenterParent;
        Icon = Owner?.Icon;

        AddTab(DiffKind.Insert, $"Insertar en destino ({counts.ToInsert:N0})");
        AddTab(DiffKind.Update, $"Actualizar ({counts.ToUpdate:N0})");
        AddTab(DiffKind.Delete, $"Eliminar de destino ({counts.ToDelete:N0})");
        Controls.Add(_tabs);
        Controls.Add(new Label
        {
            Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0),
            Text = $"Se muestran hasta {MaxRows:N0} filas por pestaña. En 'Actualizar' se resaltan los valores distintos.",
            ForeColor = Color.FromArgb(90, 90, 90),
        });
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        _tabs.SelectedIndexChanged += async (_, _) => await LoadCurrentAsync();
        Shown += async (_, _) => await LoadCurrentAsync();
    }

    private void AddTab(DiffKind kind, string title)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        grid.DataError += (_, e) => e.ThrowException = false;
        if (kind == DiffKind.Update) grid.CellFormatting += HighlightChanges;
        _tabs.TabPages.Add(new TabPage(title) { Tag = kind, Controls = { grid } });
    }

    private async Task LoadCurrentAsync()
    {
        var page = _tabs.SelectedTab;
        if (page?.Tag is not DiffKind kind || !_loaded.Add(kind)) return;
        var grid = (DataGridView)page.Controls[0];
        UseWaitCursor = true;
        try
        {
            var dt = await _transfer.GetDiffAsync(kind, MaxRows);
            grid.DataSource = dt;
        }
        catch (Exception ex)
        {
            _loaded.Remove(kind);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    /// <summary>Colorea las columnas "(origen)"/"(destino)" cuyo valor difiere.</summary>
    private static void HighlightChanges(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (sender is not DataGridView g || e.RowIndex < 0 || g.DataSource is not DataTable dt) return;
        string name = dt.Columns[e.ColumnIndex].ColumnName;
        string? other = name.EndsWith(" (origen)") ? name[..^9] + " (destino)"
                      : name.EndsWith(" (destino)") ? name[..^10] + " (origen)" : null;
        if (other == null || !dt.Columns.Contains(other)) return;
        var row = ((DataRowView)g.Rows[e.RowIndex].DataBoundItem).Row;
        if (!Equals(row[e.ColumnIndex], row[other]))
        {
            e.CellStyle!.BackColor = name.EndsWith(" (origen)") ? Color.FromArgb(220, 245, 225) : Color.FromArgb(255, 228, 225);
            e.CellStyle.Font = Theme.Bold;
        }
    }
}
