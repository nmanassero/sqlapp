using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlTableSync.Core;

namespace SqlTableSync.App;

/// <summary>Configuración local en %APPDATA%\SqlTableSync\settings.json.</summary>
internal sealed class AppSettings
{
    public sealed class SavedConnection
    {
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        public bool IntegratedSecurity { get; set; } = true;
        public string User { get; set; } = "";
        /// <summary>Contraseña cifrada con DPAPI (sólo la puede descifrar el mismo usuario de Windows).</summary>
        public string? ProtectedPassword { get; set; }
        public bool RememberCredentials { get; set; }
        public bool Encrypt { get; set; }
        public bool TrustServerCertificate { get; set; } = true;
    }

    public SavedConnection Source { get; set; } = new();
    public SavedConnection Target { get; set; } = new();
    public List<string> RecentServers { get; set; } = new();

    // Opciones de despliegue
    public TransferMode Mode { get; set; } = TransferMode.Sync;
    public bool DeleteExtraRows { get; set; } = true;
    public bool FireTriggers { get; set; }
    public bool CheckConstraints { get; set; } = true;
    public bool TableLock { get; set; } = true;
    public bool AllowDirectSameServer { get; set; } = true;
    public int BatchSize { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlTableSync", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // Configuración dañada: se empieza de cero.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // No es crítico.
        }
    }

    public void AddRecentServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server)) return;
        RecentServers.RemoveAll(s => string.Equals(s, server, StringComparison.OrdinalIgnoreCase));
        RecentServers.Insert(0, server.Trim());
        if (RecentServers.Count > 20) RecentServers.RemoveRange(20, RecentServers.Count - 20);
    }

    public static string? Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }
}
