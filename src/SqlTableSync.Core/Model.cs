namespace SqlTableSync.Core;

public sealed record TableName(string Schema, string Name)
{
    public string Quoted => Sql.Q(Schema) + "." + Sql.Q(Name);
    public override string ToString() => Schema + "." + Name;
}

/// <summary>Resumen de una tabla para el listado (nombre y filas aproximadas).</summary>
public sealed class TableSummary
{
    public required TableName Table { get; init; }
    public long RowCount { get; init; }
}

public sealed class ColumnDefinition
{
    public int Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Tipo de sistema efectivo (los tipos alias se resuelven a su tipo base), en minúsculas.</summary>
    public required string TypeName { get; init; }
    /// <summary>Tipos CLR (geography, geometry, hierarchyid, tipos de usuario CLR).</summary>
    public bool IsClr { get; init; }
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIdentity { get; init; }
    public string? IdentitySeed { get; init; }
    public string? IdentityIncrement { get; init; }
    public bool IsComputed { get; init; }
    public string? ComputedDefinition { get; init; }
    public bool IsPersisted { get; init; }
    public bool IsRowGuidCol { get; init; }
    public bool IsSparse { get; init; }
    public string? Collation { get; init; }
    public string? DefaultName { get; init; }
    public string? DefaultDefinition { get; init; }
    /// <summary>Columnas de período de tablas temporales (GENERATED ALWAYS).</summary>
    public int GeneratedAlwaysType { get; init; }

    public bool IsRowVersion => TypeName == "timestamp";

    /// <summary>Se puede insertar un valor en la columna.</summary>
    public bool IsCopyable => !IsComputed && !IsRowVersion && GeneratedAlwaysType == 0;

    public bool IsString => TypeName is "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext";

    /// <summary>Declaración del tipo, p.ej. <c>nvarchar(50)</c>.</summary>
    public string SqlType => FormatType(TypeName, MaxLength, Precision, Scale);

    public static string FormatType(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString())})",
        "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "datetime2" or "time" or "datetimeoffset" => $"{type}({scale})",
        "float" => $"float({precision})",
        _ => type,
    };
}

public sealed class IndexDefinition
{
    public required string Name { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsUniqueConstraint { get; init; }
    public bool IsUnique { get; init; }
    public bool IsClustered { get; init; }
    public bool IgnoreDupKey { get; init; }
    public string? FilterDefinition { get; init; }
    public List<(string Column, bool Descending)> KeyColumns { get; } = new();
    public List<string> IncludedColumns { get; } = new();
}

public sealed class CheckConstraintDefinition
{
    public required string Name { get; init; }
    public required string Definition { get; init; }
    public bool IsDisabled { get; init; }
}

public sealed class TableDefinition
{
    public required TableName Table { get; init; }
    public List<ColumnDefinition> Columns { get; } = new();
    public List<IndexDefinition> Indexes { get; } = new();
    public List<CheckConstraintDefinition> CheckConstraints { get; } = new();
    /// <summary>Índices que no se pueden recrear automáticamente (columnstore, XML, espaciales...).</summary>
    public List<string> UnsupportedIndexes { get; } = new();
    public List<string> EnabledTriggers { get; } = new();
    /// <summary>FKs y CHECKs habilitados de la propia tabla.</summary>
    public List<string> EnabledConstraints { get; } = new();
    public int ReferencedByForeignKeys { get; set; }
    public bool IsTemporal { get; set; }
    public bool IsMemoryOptimized { get; set; }

    public IndexDefinition? PrimaryKey => Indexes.FirstOrDefault(i => i.IsPrimaryKey);

    public ColumnDefinition? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Clave para comparar filas: la PK, o el primer índice único sin filtro cuyas columnas sean NOT NULL.
    /// </summary>
    public IndexDefinition? FindComparisonKey()
    {
        if (PrimaryKey is { } pk) return pk;
        return Indexes
            .Where(i => i.IsUnique && i.FilterDefinition is null && i.KeyColumns.Count > 0)
            .Where(i => i.KeyColumns.All(k => Column(k.Column) is { IsNullable: false }))
            .OrderByDescending(i => i.IsClustered)
            .ThenBy(i => i.KeyColumns.Count)
            .FirstOrDefault();
    }
}
