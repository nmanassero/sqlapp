using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlTableSync.Core;

internal static class Sql
{
    /// <summary>Delimita un identificador: <c>a]b</c> → <c>[a]]b]</c>.</summary>
    public static string Q(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Literal Unicode: <c>N'...'</c>.</summary>
    public static string Lit(string s) => "N'" + s.Replace("'", "''") + "'";

    public static SqlCommand Command(SqlConnection conn, string text, SqlTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = 0;
        cmd.Transaction = tx;
        return cmd;
    }

    public static async Task ExecAsync(SqlConnection conn, string text, CancellationToken ct, SqlTransaction? tx = null)
    {
        using var cmd = Command(conn, text, tx);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<long> ScalarLongAsync(SqlConnection conn, string text, CancellationToken ct, SqlTransaction? tx = null)
    {
        using var cmd = Command(conn, text, tx);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Ejecuta una sentencia DML y devuelve las filas afectadas por ella (no por triggers).</summary>
    public static Task<long> DmlAsync(SqlConnection conn, string dml, CancellationToken ct, SqlTransaction? tx = null) =>
        ScalarLongAsync(conn, "SET NOCOUNT ON;\n" + dml + ";\nSELECT CAST(@@ROWCOUNT AS bigint);", ct, tx);
}
