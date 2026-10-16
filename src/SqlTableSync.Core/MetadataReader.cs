using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlTableSync.Core;

/// <summary>Lee la estructura de tablas desde las vistas de catálogo de SQL Server.</summary>
public static class MetadataReader
{
    public static async Task<List<TableSummary>> ListTablesAsync(SqlConnection conn, CancellationToken ct = default)
    {
        const string sql = @"SELECT s.name, t.name,
    ISNULL((SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1)), 0)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name;";
        var list = new List<TableSummary>();
        using var cmd = Sql.Command(conn, sql);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new TableSummary { Table = new TableName(r.GetString(0), r.GetString(1)), RowCount = r.GetInt64(2) });
        return list;
    }

    public static async Task<int> GetMajorVersionAsync(SqlConnection conn, CancellationToken ct = default) =>
        (int)await Sql.ScalarLongAsync(conn,
            "SELECT CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), 4) AS int);", ct).ConfigureAwait(false);

    /// <summary>Devuelve la definición de la tabla, o null si no existe.</summary>
    public static async Task<TableDefinition?> GetTableAsync(SqlConnection conn, TableName table, CancellationToken ct = default)
    {
        int major = await GetMajorVersionAsync(conn, ct).ConfigureAwait(false);
        bool v2014 = major >= 12, v2016 = major >= 13;

        string objectId = $"OBJECT_ID({Sql.Lit(table.Quoted)}, N'U')";
        var def = new TableDefinition { Table = table };

        // --- Tabla ---
        string tableSql = $@"SELECT t.object_id, {(v2016 ? "t.temporal_type" : "0")}, {(v2014 ? "CAST(t.is_memory_optimized AS int)" : "0")},
    (SELECT COUNT(*) FROM sys.foreign_keys fk WHERE fk.referenced_object_id = t.object_id AND fk.parent_object_id <> t.object_id)
FROM sys.tables t WHERE t.object_id = {objectId};";
        using (var cmd = Sql.Command(conn, tableSql))
        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
            def.IsTemporal = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture) == 2;
            def.IsMemoryOptimized = Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture) == 1;
            def.ReferencedByForeignKeys = r.GetInt32(3);
        }

        // --- Columnas ---
        string colSql = $@"SELECT c.column_id, c.name,
    ty.name, bt.name, ty.is_user_defined, ty.is_assembly_type,
    c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed, c.is_rowguidcol, c.is_sparse,
    c.collation_name,
    CAST(ic.seed_value AS nvarchar(100)), CAST(ic.increment_value AS nvarchar(100)),
    cc.definition, cc.is_persisted,
    dc.name, dc.definition,
    {(v2016 ? "c.generated_always_type" : "CAST(0 AS tinyint)")}
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.types bt ON bt.user_type_id = c.system_type_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = {objectId}
ORDER BY c.column_id;";
        using (var cmd = Sql.Command(conn, colSql))
        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string userType = r.GetString(2);
                string? baseType = r.IsDBNull(3) ? null : r.GetString(3);
                bool isUserDefined = r.GetBoolean(4);
                bool isAssembly = r.GetBoolean(5);
                // Tipos CLR: se usa su nombre. Tipos alias: se resuelven al tipo base.
                string type = isAssembly ? userType : (isUserDefined && baseType != null ? baseType : userType);
                def.Columns.Add(new ColumnDefinition
                {
                    Id = r.GetInt32(0),
                    Name = r.GetString(1),
                    TypeName = type.ToLowerInvariant(),
                    IsClr = isAssembly,
                    MaxLength = r.GetInt16(6),
                    Precision = r.GetByte(7),
                    Scale = r.GetByte(8),
                    IsNullable = r.GetBoolean(9),
                    IsIdentity = r.GetBoolean(10),
                    IsComputed = r.GetBoolean(11),
                    IsRowGuidCol = r.GetBoolean(12),
                    IsSparse = r.GetBoolean(13),
                    Collation = r.IsDBNull(14) ? null : r.GetString(14),
                    IdentitySeed = r.IsDBNull(15) ? null : r.GetString(15),
                    IdentityIncrement = r.IsDBNull(16) ? null : r.GetString(16),
                    ComputedDefinition = r.IsDBNull(17) ? null : r.GetString(17),
                    IsPersisted = !r.IsDBNull(18) && r.GetBoolean(18),
                    DefaultName = r.IsDBNull(19) ? null : r.GetString(19),
                    DefaultDefinition = r.IsDBNull(20) ? null : r.GetString(20),
                    GeneratedAlwaysType = Convert.ToInt32(r.GetValue(21), CultureInfo.InvariantCulture),
                });
            }
        }

        // --- Índices ---
        string ixSql = $@"SELECT i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint,
    i.ignore_dup_key, i.has_filter, i.filter_definition,
    ic.key_ordinal, ic.is_descending_key, ic.is_included_column, c.name
FROM sys.indexes i
LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = {objectId} AND i.index_id > 0 AND i.is_hypothetical = 0
ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;";
        using (var cmd = Sql.Command(conn, ixSql))
        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            var byId = new Dictionary<int, IndexDefinition>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                int id = r.GetInt32(0);
                string name = r.IsDBNull(1) ? $"index_{id}" : r.GetString(1);
                byte type = r.GetByte(2);
                if (type is not (1 or 2))
                {
                    if (!def.UnsupportedIndexes.Contains(name)) def.UnsupportedIndexes.Add(name);
                    continue;
                }
                if (!byId.TryGetValue(id, out var ix))
                {
                    ix = new IndexDefinition
                    {
                        Name = name,
                        IsClustered = type == 1,
                        IsUnique = r.GetBoolean(3),
                        IsPrimaryKey = r.GetBoolean(4),
                        IsUniqueConstraint = r.GetBoolean(5),
                        IgnoreDupKey = r.GetBoolean(6),
                        FilterDefinition = r.GetBoolean(7) && !r.IsDBNull(8) ? r.GetString(8) : null,
                    };
                    byId[id] = ix;
                    def.Indexes.Add(ix);
                }
                if (r.IsDBNull(12)) continue;
                string col = r.GetString(12);
                if (r.GetBoolean(11)) ix.IncludedColumns.Add(col);
                else if (r.GetByte(9) > 0) ix.KeyColumns.Add((col, r.GetBoolean(10)));
            }
        }

        // --- CHECK constraints ---
        using (var cmd = Sql.Command(conn, $"SELECT name, definition, is_disabled FROM sys.check_constraints WHERE parent_object_id = {objectId} ORDER BY name;"))
        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                def.CheckConstraints.Add(new CheckConstraintDefinition { Name = r.GetString(0), Definition = r.GetString(1), IsDisabled = r.GetBoolean(2) });
        }

        // --- Triggers y constraints habilitados (para deshabilitarlos durante la carga) ---
        using (var cmd = Sql.Command(conn, $"SELECT name FROM sys.triggers WHERE parent_id = {objectId} AND is_disabled = 0 ORDER BY name;"))
        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false)) def.EnabledTriggers.Add(r.GetString(0));
        }
        using (var cmd = Sql.Command(conn, $@"SELECT name FROM sys.foreign_keys WHERE parent_object_id = {objectId} AND is_disabled = 0
UNION ALL SELECT name FROM sys.check_constraints WHERE parent_object_id = {objectId} AND is_disabled = 0;"))
        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false)) def.EnabledConstraints.Add(r.GetString(0));
        }

        return def;
    }
}
