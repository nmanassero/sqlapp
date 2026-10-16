using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>
/// Conexión paso a paso: primero servidor (IP), usuario y contraseña de SQL Server y "Conectar";
/// recién entonces se habilita la lista de bases de datos.
/// </summary>
internal sealed class ConnectionPanel : UserControl
{
    private readonly ComboBox _server = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _remember = new() { Text = "Recordar credenciales", AutoSize = true };
    private readonly CheckBox _encrypt = new() { Text = "Encrypt", AutoSize = true };
    private readonly CheckBox _trust = new() { Text = "Trust server certificate", AutoSize = true, Checked = true };
    private readonly Button _connect = Theme.PrimaryButton("Conectar");
    private readonly ComboBox _database = new() { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
    private readonly Button _refresh = new() { Text = "", FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Enabled = false };
    private readonly Label _status = new() { AutoSize = false, ForeColor = Theme.Delete };
    private readonly Label _lblServer = Lbl("Servidor (IP o nombre)", bold: true);
    private readonly Label _lblUser = Lbl("Usuario"), _lblPass = Lbl("Contraseña");
    private readonly Label _step1 = Step("1"), _step2 = Step("2");
    private readonly Label _lblDb = Lbl("Base de datos", bold: true);
    private string? _pendingDatabase;
    private bool _connected;

    public ConnectionPanel()
    {
        BackColor = Color.White;
        Font = Theme.Base;
        _connect.Width = 110;
        _connect.Height = 30;
        _connect.Click += async (_, _) => await ConnectAsync();
        _refresh.FlatAppearance.BorderSize = 0;
        _refresh.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 243, 252);
        _refresh.Paint += (_, e) => Theme.DrawRefresh(e.Graphics, new Rectangle(5, 5, _refresh.Width - 10, _refresh.Height - 10),
            _refresh.Enabled ? Theme.Accent : Color.Silver);
        _refresh.Click += async (_, _) => await ConnectAsync();

        // Cambiar servidor o credenciales obliga a volver a conectar.
        foreach (var c in new Control[] { _server, _user, _password })
            c.TextChanged += (_, _) => Disconnect();
        _encrypt.CheckedChanged += (_, _) => Disconnect();
        _trust.CheckedChanged += (_, _) => Disconnect();
        _database.SelectedIndexChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _password.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ConnectAsync(); } };

        new ToolTip().SetToolTip(_remember, "Guarda la contraseña cifrada (DPAPI) para su usuario de Windows en %APPDATA%\\SqlTableSync.");
        new ToolTip().SetToolTip(_refresh, "Actualizar la lista de bases de datos");

        Controls.AddRange(new Control[] { _step1, _lblServer, _server, _lblUser, _user, _lblPass, _password,
            _remember, _encrypt, _trust, _connect, _step2, _lblDb, _database, _refresh, _status });
        UpdateState();
    }

    public event EventHandler? Changed;

    private static Label Lbl(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = false, Font = bold ? Theme.Bold : Theme.Base,
        TextAlign = bold ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight,
    };

    private static Label Step(string n) => new()
    {
        Text = n, AutoSize = false, Size = new Size(22, 22), TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Bold,
    };

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = ClientSize.Width, m = 16, lx = 110, fw = Math.Max(120, w - lx - m);
        _step1.Location = new Point(m, 14);
        _lblServer.SetBounds(m + 30, 15, 260, 20);
        _server.SetBounds(m, 44, w - 2 * m, 24);
        _lblUser.SetBounds(m, 80, lx - m - 4, 23); _user.SetBounds(lx, 80, fw, 24);
        _lblPass.SetBounds(m, 114, lx - m - 4, 23); _password.SetBounds(lx, 114, fw, 24);
        _remember.Location = new Point(lx, 146);
        _encrypt.Location = new Point(lx, 172);
        _trust.Location = new Point(lx + 90, 172);
        _connect.Location = new Point(w - m - _connect.Width, 160);
        _step2.Location = new Point(m, 222);
        _lblDb.SetBounds(m + 30, 223, 200, 20);
        _database.SetBounds(m, 252, w - 2 * m - 36, 24);
        _refresh.SetBounds(w - m - 32, 249, 32, 30);
        _status.SetBounds(m, 286, w - 2 * m, 60);
    }

    private void UpdateState()
    {
        _database.Enabled = _refresh.Enabled = _connected;
        _step2.BackColor = _connected ? Theme.Accent : Color.Silver;
        _lblDb.ForeColor = _connected ? Color.Black : Color.Gray;
        _refresh.Invalidate();
    }

    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        _pendingDatabase = _database.SelectedItem as string ?? _pendingDatabase;
        _database.Items.Clear();
        ShowStatus("");
        UpdateState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRecentServers(IEnumerable<string> servers)
    {
        string current = _server.Text;
        _server.Items.Clear();
        foreach (var s in servers) _server.Items.Add(s);
        _server.Text = current;
    }

    public void LoadFrom(AppSettings.SavedConnection c)
    {
        _server.Text = c.Server;
        _user.Text = c.User;
        _remember.Checked = c.RememberCredentials;
        _password.Text = c.RememberCredentials ? AppSettings.Unprotect(c.ProtectedPassword) : "";
        _encrypt.Checked = c.Encrypt;
        _trust.Checked = c.TrustServerCertificate;
        _connected = true;   // fuerza el reseteo
        Disconnect();
        _pendingDatabase = c.Database;
    }

    public AppSettings.SavedConnection SaveTo() => new()
    {
        Server = _server.Text.Trim(),
        Database = Database,
        IntegratedSecurity = false,
        User = _user.Text.Trim(),
        RememberCredentials = _remember.Checked,
        ProtectedPassword = _remember.Checked ? AppSettings.Protect(_password.Text) : null,
        Encrypt = _encrypt.Checked,
        TrustServerCertificate = _trust.Checked,
    };

    private string Database => _database.SelectedItem as string ?? _pendingDatabase ?? "";

    public string Password => _password.Text;
    public void SetPassword(string password) => _password.Text = password;

    public SqlConnectionInfo GetConnection() => new()
    {
        Server = _server.Text.Trim(),
        Database = Database,
        IntegratedSecurity = false,
        User = _user.Text.Trim(),
        Password = _password.Text,
        Encrypt = _encrypt.Checked,
        TrustServerCertificate = _trust.Checked,
    };

    public bool IsConnected => _connected;

    public string? ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_server.Text)) return "Indique el servidor.";
        if (string.IsNullOrWhiteSpace(_user.Text)) return "Indique el usuario.";
        if (!_connected) return "Pulse \"Conectar\" y elija la base de datos.";
        if (_database.SelectedItem is null) return "Elija la base de datos.";
        return null;
    }

    public void ShowStatus(string text, bool error = true)
    {
        _status.ForeColor = error ? Theme.Delete : Theme.Insert;
        _status.Text = text;
    }

    /// <summary>Conecta y carga las bases. Devuelve true si se conectó.</summary>
    public async Task<bool> ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_server.Text)) { ShowStatus("Indique el servidor."); return false; }
        if (string.IsNullOrWhiteSpace(_user.Text)) { ShowStatus("Indique el usuario."); return false; }
        var conn = GetConnection();
        string wanted = _database.SelectedItem as string ?? _pendingDatabase ?? "";
        ShowStatus("Conectando...", error: false);
        Cursor = Cursors.WaitCursor;
        _connect.Enabled = false;
        try
        {
            var dbs = await conn.ListDatabasesAsync();
            _database.Items.Clear();
            foreach (var d in dbs) _database.Items.Add(d);
            _connected = true;
            int idx = dbs.FindIndex(d => string.Equals(d, wanted, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _database.SelectedIndex = idx;
            _pendingDatabase = null;
            UpdateState();
            ShowStatus($"Conectado. {dbs.Count} bases de datos disponibles.", error: false);
            if (idx < 0) _database.Focus();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _connected = false;
            UpdateState();
            ShowStatus("No se pudo conectar: " + ex.Message);
            return false;
        }
        finally
        {
            Cursor = Cursors.Default;
            _connect.Enabled = true;
        }
    }
}
