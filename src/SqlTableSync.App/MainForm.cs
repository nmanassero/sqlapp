namespace SqlTableSync.App;

/// <summary>
/// Ventana principal: pantalla de conexiones (Origen / Destino) y pantalla de resultados.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HeaderBar _header = new();
    private readonly Panel _connectionPage = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly ConnectionPanel _source = new();
    private readonly ConnectionPanel _target = new();
    private readonly ResultsPage _results;

    public MainForm()
    {
        Text = "SqlTableSync — Copia de datos de tablas SQL Server";
        Font = Theme.Base;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(980, 620);
        Size = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* sin ícono */ }

        _results = new ResultsPage(_settings) { Dock = DockStyle.Fill, Visible = false };
        _results.BackRequested += (_, _) => ShowConnections();
        _header.SetCenterContent(_results.CenterContent);

        BuildConnectionPage();
        Controls.Add(_results);
        Controls.Add(_connectionPage);
        Controls.Add(_header);

        _source.LoadFrom(_settings.Source);
        _target.LoadFrom(_settings.Target);
        _source.SetRecentServers(_settings.RecentServers);
        _target.SetRecentServers(_settings.RecentServers);
        _source.Changed += (_, _) => UpdateHeader();
        _target.Changed += (_, _) => UpdateHeader();
        UpdateHeader();
    }

    private void BuildConnectionPage()
    {
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.White };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _source.Dock = _target.Dock = DockStyle.Fill;
        split.Controls.Add(_source, 0, 0);
        split.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Separator, Margin = new Padding(0, 10, 0, 10) }, 1, 0);
        split.Controls.Add(_target, 2, 0);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.SourceBack };
        var compare = Theme.PrimaryButton("Comparar ahora");
        compare.Width = 160;
        compare.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        var swap = new Button { Text = "Intercambiar origen y destino", AutoSize = true, Height = 34, Location = new Point(16, 13) };
        var note = new Label
        {
            Text = "Sólo lectura en origen. Los cambios en destino se hacen al desplegar, con una transacción por tabla.",
            AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90), Location = new Point(250, 21),
        };
        bottom.Controls.Add(compare);
        bottom.Controls.Add(swap);
        bottom.Controls.Add(note);
        bottom.Resize += (_, _) => compare.Location = new Point(bottom.Width - compare.Width - 16, 13);
        compare.Click += async (_, _) => await CompareAsync();
        swap.Click += (_, _) =>
        {
            var s = _source.SaveTo(); var sp = _source.Password;
            var t = _target.SaveTo(); var tp = _target.Password;
            _source.LoadFrom(t); _target.LoadFrom(s);
            // La contraseña se mueve aunque no esté guardada.
            _source.SetPassword(tp); _target.SetPassword(sp);
            UpdateHeader();
        };

        _connectionPage.Controls.Add(split);
        _connectionPage.Controls.Add(bottom);
    }

    private void UpdateHeader()
    {
        var s = _source.GetConnection();
        var t = _target.GetConnection();
        _header.SetEndpoints(s.Server, s.Database, t.Server, t.Database);
    }

    private async Task CompareAsync()
    {
        // Si falta conectar alguno de los dos, se conecta ahora.
        if (!_source.IsConnected && !await _source.ConnectAsync()) return;
        if (!_target.IsConnected && !await _target.ConnectAsync()) return;
        string? err = _source.ValidateInput();
        if (err != null) { _source.ShowStatus(err); return; }
        err = _target.ValidateInput();
        if (err != null) { _target.ShowStatus(err); return; }

        var src = _source.GetConnection();
        var tgt = _target.GetConnection();
        if (string.Equals(src.Server, tgt.Server, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(src.Database, tgt.Database, StringComparison.OrdinalIgnoreCase))
        {
            _target.ShowStatus("Origen y destino son la misma base de datos.");
            return;
        }

        _settings.Source = _source.SaveTo();
        _settings.Target = _target.SaveTo();
        _settings.AddRecentServer(src.Server);
        _settings.AddRecentServer(tgt.Server);
        _settings.Save();
        _source.SetRecentServers(_settings.RecentServers);
        _target.SetRecentServers(_settings.RecentServers);

        ShowResults();
        await _results.LoadAsync(src, tgt);
    }

    private void ShowResults()
    {
        _connectionPage.Visible = false;
        _results.Visible = true;
        _header.Compact = true;
        UpdateHeader();
    }

    private void ShowConnections()
    {
        _results.Visible = false;
        _connectionPage.Visible = true;
        _header.Compact = false;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _settings.Source = _source.SaveTo();
        _settings.Target = _target.SaveTo();
        _settings.Save();
        try { await _results.DisposeTransfersAsync(); } catch { /* se cierran con el proceso */ }
    }
}
