using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>Confirmación y opciones del despliegue.</summary>
internal sealed class DeployForm : Form
{
    private readonly AppSettings _settings;
    private readonly RadioButton _sync = new() { Text = "Sincronizar (comparar e insertar / actualizar / eliminar sólo lo distinto)", AutoSize = true };
    private readonly RadioButton _replace = new() { Text = "Reemplazar datos (vaciar la tabla, o las filas del filtro, y cargar todo)", AutoSize = true };
    private readonly RadioButton _recreate = new() { Text = "Recrear tabla (DROP + CREATE con la estructura de origen y cargar)", AutoSize = true };
    private readonly CheckBox _deleteExtra = new() { Text = "Eliminar en destino las filas que no existen en origen", AutoSize = true };
    private readonly CheckBox _fireTriggers = new() { Text = "Ejecutar triggers de destino", AutoSize = true };
    private readonly CheckBox _checkConstraints = new() { Text = "Verificar FKs y CHECK durante la carga", AutoSize = true };
    private readonly CheckBox _tableLock = new() { Text = "Bloqueo de tabla (TABLOCK) en la carga: más rápido", AutoSize = true };
    private readonly CheckBox _direct = new() { Text = "Misma instancia: copiar directo entre bases (INSERT ... SELECT)", AutoSize = true };
    private readonly NumericUpDown _batch = new() { Minimum = 0, Maximum = 10_000_000, Increment = 10_000, Width = 100, ThousandsSeparator = true };
    private readonly Label _modeHelp = new() { AutoSize = false, Height = 48, ForeColor = Color.FromArgb(90, 90, 90) };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 150 };
    private readonly List<TableItem> _items;

    public DeployForm(AppSettings settings, List<TableItem> items, SqlConnectionInfo source, SqlConnectionInfo target)
    {
        _settings = settings;
        _items = items;
        Text = "Desplegar datos";
        Font = Theme.Base;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 640);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Font;

        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.TargetBack };
        header.Controls.Add(new Label
        {
            Text = $"Se modificará el DESTINO:  {target.Server}  /  {target.Database}\nDatos tomados de:  {source.Server}  /  {source.Database}",
            ForeColor = Color.White, Font = new Font("Segoe UI", 10f), Dock = DockStyle.Fill, Padding = new Padding(14, 8, 8, 0),
        });

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 10, 16, 0), AutoScroll = true };

        _list.Columns.Add("Tabla", 260);
        _list.Columns.Add("Filtro", 160);
        _list.Columns.Add("Comparación", 230);
        _list.Width = 680;
        foreach (var it in items)
        {
            string cmp = it.Counts switch
            {
                null => it.InTarget ? "sin comparar" : "no existe en destino",
                { TargetMissing: true } c => $"nueva: {c.ToInsert:N0} {(c.ToInsert == 1 ? "fila" : "filas")}",
                { HasDifferences: false } => "idéntica",
                var c => $"+{c.ToInsert:N0}  ~{c.ToUpdate:N0}  −{c.ToDelete:N0}",
            };
            _list.Items.Add(new ListViewItem(new[] { it.Name.ToString(), it.Where, cmp }));
        }

        body.Controls.Add(Section($"Tablas ({items.Count})"));
        body.Controls.Add(_list);
        body.Controls.Add(Section("Modo"));
        body.Controls.Add(_sync);
        body.Controls.Add(_replace);
        body.Controls.Add(_recreate);
        _modeHelp.Width = 680;
        body.Controls.Add(_modeHelp);
        body.Controls.Add(Section("Opciones"));
        foreach (var c in new Control[] { _deleteExtra, _fireTriggers, _checkConstraints, _tableLock, _direct }) body.Controls.Add(c);
        var batchRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        batchRow.Controls.Add(new Label { Text = "Filas por lote de SqlBulkCopy (0 = un solo lote):", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
        batchRow.Controls.Add(_batch);
        body.Controls.Add(batchRow);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 0), BackColor = Theme.SourceBack };
        var ok = Theme.PrimaryButton("Desplegar");
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Height = 34, Width = 100 };
        ok.Click += (_, _) => Accept();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        CancelButton = cancel;

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(header);

        _sync.Checked = settings.Mode == TransferMode.Sync;
        _replace.Checked = settings.Mode == TransferMode.Replace;
        _recreate.Checked = settings.Mode == TransferMode.Recreate;
        _deleteExtra.Checked = settings.DeleteExtraRows;
        _fireTriggers.Checked = settings.FireTriggers;
        _checkConstraints.Checked = settings.CheckConstraints;
        _tableLock.Checked = settings.TableLock;
        _direct.Checked = settings.AllowDirectSameServer;
        _batch.Value = Math.Clamp(settings.BatchSize, 0, 10_000_000);
        foreach (var rb in new[] { _sync, _replace, _recreate }) rb.CheckedChanged += (_, _) => UpdateHelp();
        UpdateHelp();
    }

    private static Label Section(string text) => new()
    {
        Text = text, Font = Theme.Bold, AutoSize = true, Margin = new Padding(0, 12, 0, 4),
    };

    private void UpdateHelp()
    {
        _deleteExtra.Enabled = _sync.Checked;
        _modeHelp.Text = _sync.Checked
            ? "Requiere clave primaria o índice único. Sólo se modifican las filas distintas, dentro de una transacción por tabla. Si ya comparó, se reutilizan los datos transferidos."
            : _replace.Checked
                ? "Mantiene la estructura de destino. Usa TRUNCATE si no hay filtro ni FKs que referencien la tabla; si no, DELETE. Todo dentro de una transacción por tabla."
                : "Borra la tabla de destino y la crea igual que en origen (columnas, PK, índices, CHECK, defaults; no FKs). Falla si otras tablas la referencian con FK.";
    }

    private void Accept()
    {
        var mode = _sync.Checked ? TransferMode.Sync : _replace.Checked ? TransferMode.Replace : TransferMode.Recreate;
        if (mode != TransferMode.Sync)
        {
            string what = mode == TransferMode.Replace ? "VACIAR y recargar" : "ELIMINAR y recrear";
            if (MessageBox.Show(this, $"Se va a {what} {_items.Count} tabla(s) en destino. ¿Continuar?", "Confirmar",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }
        _settings.Mode = mode;
        _settings.DeleteExtraRows = _deleteExtra.Checked;
        _settings.FireTriggers = _fireTriggers.Checked;
        _settings.CheckConstraints = _checkConstraints.Checked;
        _settings.TableLock = _tableLock.Checked;
        _settings.AllowDirectSameServer = _direct.Checked;
        _settings.BatchSize = (int)_batch.Value;
        DialogResult = DialogResult.OK;
        Close();
    }
}
