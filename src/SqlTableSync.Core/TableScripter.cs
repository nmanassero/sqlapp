using System.Text;

namespace SqlTableSync.Core;

/// <summary>Genera el DDL de una tabla a partir de su definición de origen.</summary>
public static class TableScripter
{
    /// <summary>
    /// CREATE TABLE con columnas, defaults y clave primaria. Los índices restantes y los CHECK
    /// se crean después de cargar los datos (<see cref="PostLoadStatements"/>), que es más rápido.
    /// </summary>
    public static string CreateTable(TableDefinition def, TableName target)
    {
        if (def.IsTemporal)
            throw new NotSupportedException($"La tabla {def.Table} es temporal (SYSTEM_VERSIONING). Cree su estructura con SQL Compare y use el modo Reemplazar o Sincronizar.");
        if (def.IsMemoryOptimized)
            throw new NotSupportedException($"La tabla {def.Table} es memory-optimized. Cree su estructura con SQL Compare y use el modo Reemplazar o Sincronizar.");

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(target.Quoted).AppendLine(" (");
        var lines = new List<string>();
        foreach (var c in def.Columns)
        {
            var l = new StringBuilder("    ").Append(Sql.Q(c.Name)).Append(' ');
            if (c.IsComputed)
            {
                l.Append("AS ").Append(c.ComputedDefinition);
                if (c.IsPersisted) l.Append(" PERSISTED");
            }
            else
            {
                l.Append(c.IsClr ? Sql.Q(c.TypeName) : c.SqlType);
                if (c.IsSparse) l.Append(" SPARSE");
                if (c.IsString && c.Collation != null) l.Append(" COLLATE ").Append(c.Collation);
                if (c.IsIdentity) l.Append($" IDENTITY({c.IdentitySeed ?? "1"},{c.IdentityIncrement ?? "1"})");
                if (c.IsRowGuidCol) l.Append(" ROWGUIDCOL");
                l.Append(c.IsNullable ? " NULL" : " NOT NULL");
                if (c.DefaultDefinition != null)
                    l.Append(" CONSTRAINT ").Append(Sql.Q(c.DefaultName!)).Append(" DEFAULT ").Append(c.DefaultDefinition);
            }
            lines.Add(l.ToString());
        }
        if (def.PrimaryKey is { } pk)
            lines.Add("    CONSTRAINT " + Sql.Q(pk.Name) + " PRIMARY KEY " + (pk.IsClustered ? "CLUSTERED" : "NONCLUSTERED") + " (" + KeyList(pk) + ")");
        sb.AppendLine(string.Join(",\n", lines));
        sb.Append(");");
        return sb.ToString();
    }

    /// <summary>Índices (no PK) y CHECK constraints, para ejecutar después de la carga.</summary>
    public static List<string> PostLoadStatements(TableDefinition def, TableName target)
    {
        var list = new List<string>();
        // Primero el clustered (si no es la PK) para no reconstruir los nonclustered.
        foreach (var ix in def.Indexes.Where(i => !i.IsPrimaryKey).OrderByDescending(i => i.IsClustered))
        {
            if (ix.KeyColumns.Count == 0) continue;
            if (ix.IsUniqueConstraint)
            {
                list.Add($"ALTER TABLE {target.Quoted} ADD CONSTRAINT {Sql.Q(ix.Name)} UNIQUE {(ix.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({KeyList(ix)});");
                continue;
            }
            var sb = new StringBuilder("CREATE ");
            if (ix.IsUnique) sb.Append("UNIQUE ");
            sb.Append(ix.IsClustered ? "CLUSTERED" : "NONCLUSTERED");
            sb.Append(" INDEX ").Append(Sql.Q(ix.Name)).Append(" ON ").Append(target.Quoted).Append(" (").Append(KeyList(ix)).Append(')');
            if (ix.IncludedColumns.Count > 0)
                sb.Append(" INCLUDE (").Append(string.Join(", ", ix.IncludedColumns.Select(Sql.Q))).Append(')');
            if (ix.FilterDefinition != null) sb.Append(" WHERE ").Append(ix.FilterDefinition);
            if (ix.IgnoreDupKey) sb.Append(" WITH (IGNORE_DUP_KEY = ON)");
            sb.Append(';');
            list.Add(sb.ToString());
        }
        foreach (var ck in def.CheckConstraints)
        {
            list.Add($"ALTER TABLE {target.Quoted} WITH {(ck.IsDisabled ? "NOCHECK" : "CHECK")} ADD CONSTRAINT {Sql.Q(ck.Name)} CHECK {ck.Definition};");
            if (ck.IsDisabled) list.Add($"ALTER TABLE {target.Quoted} NOCHECK CONSTRAINT {Sql.Q(ck.Name)};");
        }
        return list;
    }

    public static string CreateSchemaIfMissing(string schema) =>
        $"IF SCHEMA_ID({Sql.Lit(schema)}) IS NULL EXEC(N'CREATE SCHEMA {Sql.Q(schema).Replace("'", "''")}');";

    private static string KeyList(IndexDefinition ix) =>
        string.Join(", ", ix.KeyColumns.Select(k => Sql.Q(k.Column) + (k.Descending ? " DESC" : " ASC")));
}
