using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>Formulario de conexión (servidor, autenticación, base) para origen o destino.</summary>
internal sealed class ConnectionPanel : UserControl
{
    private readonly ComboBox _server = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _remember = new() { Text = "Recordar credenciales", AutoSize = true };
    private readonly CheckBox _encrypt = new() { Text = "Encrypt", AutoSize = true };
    private readonly CheckBox _trust = new() { Text = "Trust server certificate", AutoSize = true, Checked = true };
    private readonly ComboBox _database = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _refresh = new() { Text = "", FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
    private readonly Label _status = new() { AutoSize = false, ForeColor = Theme.Delete };
    private readonly Label _lblAuth = Lbl("Autenticación"), _lblUser = Lbl("Usuario"), _lblPass = Lbl("Contraseña");
    private readonly Label _lblServer = Lbl("Servidor", bold: true), _lblDb = Lbl("Base de datos", bold: true);
    private string _loadedFor = "";

    public ConnectionPanel()
    {
        BackColor = Color.White;
        Font = Theme.Base;
        _auth.Items.AddRange(new object[] { "Autenticación de Windows", "Autenticación de SQL Server" });
        _auth.SelectedIndex = 0;
        _auth.SelectedIndexChanged += (_, _) => UpdateAuthState();
        _refresh.FlatAppearance.BorderSize = 0;
        _refresh.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 243, 252);
        _refresh.Paint += (_, e) => Theme.DrawRefresh(e.Graphics, new Rectangle(5, 5, _refresh.Width - 10, _refresh.Height - 10),
            _refresh.Enabled ? Theme.Accent : Color.Silver);
        _refresh.Click += async (_, _) => await LoadDatabasesAsync(force: true);
        _database.DropDown += async (_, _) => await LoadDatabasesAsync(force: false);
        _server.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _database.TextChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        new ToolTip().SetToolTip(_remember, "Guarda la contraseña cifrada con DPAPI para su usuario de Windows en %APPDATA%\\SqlTableSync.");
        new ToolTip().SetToolTip(_refresh, "Actualizar la lista de bases de datos");

        Controls.AddRange(new Control[] { _lblServer, _server, _lblAuth, _auth, _lblUser, _user, _lblPass, _password,
            _remember, _encrypt, _trust, _lblDb, _database, _refresh, _status });
        UpdateAuthState();
    }

    public event EventHandler? Changed;

    private static Label Lbl(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = false, Font = bold ? Theme.Bold : Theme.Base,
        TextAlign = bold ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight,
    };

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = ClientSize.Width, m = 16, lx = 130, fw = Math.Max(120, w - lx - m);
        _lblServer.SetBounds(m, 14, 200, 20);
        _server.SetBounds(m, 36, w - 2 * m, 24);
        _lblAuth.SetBounds(m, 72, lx - m - 4, 23); _auth.SetBounds(lx, 72, fw, 24);
        _lblUser.SetBounds(m, 106, lx - m - 4, 23); _user.SetBounds(lx, 106, fw, 24);
        _lblPass.SetBounds(m, 140, lx - m - 4, 23); _password.SetBounds(lx, 140, fw, 24);
        _remember.Location = new Point(lx, 174);
        _encrypt.Location = new Point(lx, 210);
        _trust.Location = new Point(lx + 90, 210);
        _lblDb.SetBounds(m, 252, 200, 20);
        _database.SetBounds(m, 274, w - 2 * m - 36, 24);
        _refresh.SetBounds(w - m - 32, 271, 32, 30);
        _status.SetBounds(m, 306, w - 2 * m, 60);
    }

    private void UpdateAuthState()
    {
        bool sql = _auth.SelectedIndex == 1;
        _user.Enabled = _password.Enabled = _remember.Enabled = sql;
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
        _auth.SelectedIndex = c.IntegratedSecurity ? 0 : 1;
        _user.Text = c.User;
        _remember.Checked = c.RememberCredentials;
        _password.Text = c.RememberCredentials ? AppSettings.Unprotect(c.ProtectedPassword) : "";
        _encrypt.Checked = c.Encrypt;
        _trust.Checked = c.TrustServerCertificate;
        _database.Text = c.Database;
    }

    public AppSettings.SavedConnection SaveTo() => new()
    {
        Server = _server.Text.Trim(),
        Database = _database.Text.Trim(),
        IntegratedSecurity = _auth.SelectedIndex == 0,
        User = _user.Text.Trim(),
        RememberCredentials = _remember.Checked,
        ProtectedPassword = _remember.Checked && _auth.SelectedIndex == 1 ? AppSettings.Protect(_password.Text) : null,
        Encrypt = _encrypt.Checked,
        TrustServerCertificate = _trust.Checked,
    };

    public SqlConnectionInfo GetConnection() => new()
    {
        Server = _server.Text.Trim(),
        Database = _database.Text.Trim(),
        IntegratedSecurity = _auth.SelectedIndex == 0,
        User = _user.Text.Trim(),
        Password = _password.Text,
        Encrypt = _encrypt.Checked,
        TrustServerCertificate = _trust.Checked,
    };

    public string? ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_server.Text)) return "Indique el servidor.";
        if (_auth.SelectedIndex == 1 && string.IsNullOrWhiteSpace(_user.Text)) return "Indique el usuario.";
        if (string.IsNullOrWhiteSpace(_database.Text)) return "Indique la base de datos.";
        return null;
    }

    public void ShowStatus(string text, bool error = true)
    {
        _status.ForeColor = error ? Theme.Delete : Theme.Insert;
        _status.Text = text;
    }

    private async Task LoadDatabasesAsync(bool force)
    {
        var conn = GetConnection();
        string key = $"{conn.Server}|{conn.IntegratedSecurity}|{conn.User}";
        if (string.IsNullOrWhiteSpace(conn.Server) || (!force && key == _loadedFor && _database.Items.Count > 0)) return;
        ShowStatus("");
        Cursor = Cursors.WaitCursor;
        _refresh.Enabled = false;
        try
        {
            var dbs = await conn.ListDatabasesAsync();
            string current = _database.Text;
            _database.Items.Clear();
            foreach (var d in dbs) _database.Items.Add(d);
            _database.Text = current;
            _loadedFor = key;
            ShowStatus($"Conectado. {dbs.Count} bases de datos.", error: false);
        }
        catch (Exception ex)
        {
            ShowStatus("No se pudo conectar: " + ex.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
            _refresh.Enabled = true;
        }
    }
}
