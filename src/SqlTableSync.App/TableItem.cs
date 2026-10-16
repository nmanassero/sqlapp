using SqlTableSync.Core;

namespace SqlTableSync.App;

internal enum TableGroup
{
    Different,
    OnlySource,
    NotCompared,
    Error,
    Identical,
    OnlyTarget,
}

/// <summary>Una tabla en la lista de resultados (emparejada por esquema + nombre).</summary>
internal sealed class TableItem
{
    public required TableName Name { get; init; }
    public long? SourceRows { get; set; }
    public long? TargetRows { get; set; }
    public bool Selected { get; set; }
    public string Where { get; set; } = "";
    public ComparisonCounts? Counts { get; set; }
    public string? Error { get; set; }
    public string Status { get; set; } = "";
    /// <summary>Sesión de comparación abierta (conserva los datos de origen ya transferidos).</summary>
    public TableTransfer? Transfer { get; set; }

    public bool InSource => SourceRows.HasValue;
    public bool InTarget => TargetRows.HasValue;
    public bool Selectable => InSource;

    public TableGroup Group =>
        !InSource ? TableGroup.OnlyTarget :
        Error != null ? TableGroup.Error :
        !InTarget ? TableGroup.OnlySource :
        Counts == null ? TableGroup.NotCompared :
        Counts.HasDifferences ? TableGroup.Different : TableGroup.Identical;

    public async Task DisposeTransferAsync()
    {
        if (Transfer != null)
        {
            var t = Transfer;
            Transfer = null;
            await t.DisposeAsync();
        }
    }
}

internal sealed class GroupHeader
{
    public required TableGroup Group { get; init; }
    public List<TableItem> Items { get; } = new();
    public bool Collapsed { get; set; }

    public string Text
    {
        get
        {
            int n = Items.Count;
            string t = n == 1 ? "1 tabla" : $"{n} tablas";
            return Group switch
            {
                TableGroup.Different => $"{t} con diferencias de datos",
                TableGroup.OnlySource => $"{t} sólo en origen (se crea{(n == 1 ? "rá" : "rán")})",
                TableGroup.NotCompared => $"{t} en ambas bases, sin comparar",
                TableGroup.Error => $"{t} con error",
                TableGroup.Identical => $"{t} con datos idénticos",
                _ => $"{t} sólo en destino",
            };
        }
    }
}
