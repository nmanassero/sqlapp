namespace SqlTableSync.Core;

public enum TransferMode
{
    /// <summary>Compara por clave e inserta/actualiza/elimina sólo lo distinto (como Data Compare).</summary>
    Sync,
    /// <summary>Vacía la tabla de destino (o las filas del filtro) y carga todo desde origen.</summary>
    Replace,
    /// <summary>DROP + CREATE de la tabla con la estructura de origen y carga de datos.</summary>
    Recreate,
}

public sealed class TransferOptions
{
    public TransferMode Mode { get; set; } = TransferMode.Sync;

    /// <summary>
    /// Filtro opcional (sin la palabra WHERE). Se aplica al origen y, en Sincronizar/Reemplazar, también
    /// a las filas de destino que se pueden eliminar, de modo que sólo se toca ese subconjunto.
    /// </summary>
    public string? Where { get; set; }

    /// <summary>Sincronizar: eliminar en destino las filas que no existen en origen.</summary>
    public bool DeleteExtraRows { get; set; } = true;

    /// <summary>Ejecutar los triggers de la tabla de destino durante la carga.</summary>
    public bool FireTriggers { get; set; }

    /// <summary>Verificar FKs y CHECK constraints de destino durante la carga.</summary>
    public bool CheckConstraints { get; set; } = true;

    /// <summary>Bloqueo de tabla durante la carga masiva (más rápido, bloquea lectores).</summary>
    public bool TableLock { get; set; } = true;

    /// <summary>Filas por lote de SqlBulkCopy (0 = un solo lote).</summary>
    public int BatchSize { get; set; } = 0;

    /// <summary>
    /// Si origen y destino están en la misma instancia, leer origen con nombres de 3 partes en lugar
    /// de transferir los datos por la red.
    /// </summary>
    public bool AllowDirectSameServer { get; set; } = true;

    public TransferOptions Clone() => (TransferOptions)MemberwiseClone();
}

public sealed class ComparisonCounts
{
    public bool TargetMissing { get; init; }
    public long SourceRows { get; init; }
    public long ToInsert { get; init; }
    public long ToUpdate { get; init; }
    public long ToDelete { get; init; }
    public long Identical { get; init; }
    public bool HasDifferences => TargetMissing || ToInsert + ToUpdate + ToDelete > 0;
}

public sealed class TransferResult
{
    public required TableName Table { get; init; }
    public long Inserted { get; set; }
    public long Updated { get; set; }
    public long Deleted { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<string> Messages { get; } = new();
}

public enum DiffKind { Insert, Update, Delete }
