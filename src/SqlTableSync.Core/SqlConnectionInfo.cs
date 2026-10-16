using Microsoft.Data.SqlClient;

namespace SqlTableSync.Core;

/// <summary>Datos de conexión a una instancia de SQL Server.</summary>
public sealed class SqlConnectionInfo
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool IntegratedSecurity { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Encrypt { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 15;

    public string BuildConnectionString(string? databaseOverride = null)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseOverride ?? Database,
            IntegratedSecurity = IntegratedSecurity,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SqlTableSync",
            // Las operaciones de copia pueden ser largas; se evita que el pool reutilice
            // conexiones con tablas temporales de sesiones anteriores.
            Pooling = false,
        };
        if (!IntegratedSecurity)
        {
            b.UserID = User;
            b.Password = Password;
        }
        return b.ConnectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default, string? databaseOverride = null)
    {
        var conn = new SqlConnection(BuildConnectionString(databaseOverride));
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET XACT_ABORT ON; SET ARITHABORT ON; SET ANSI_WARNINGS ON; SET QUOTED_IDENTIFIER ON;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Lista las bases de datos en línea a las que el usuario tiene acceso.</summary>
    public async Task<List<string>> ListDatabasesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct, "master").ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT name FROM sys.databases
WHERE state = 0 AND HAS_DBACCESS(name) = 1 AND database_id > 4
ORDER BY name;";
        var list = new List<string>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
        return list;
    }

    public string Display => $"{Server} / {Database}";

    public SqlConnectionInfo Clone() => (SqlConnectionInfo)MemberwiseClone();
}
