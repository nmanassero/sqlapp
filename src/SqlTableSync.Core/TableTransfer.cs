using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlTableSync.Core;

/// <summary>
/// Copia/sincroniza los datos de una tabla de origen a destino.
/// <para>
/// Estrategia: los datos de origen se envían con SqlBulkCopy a una tabla temporal en el servidor de
/// destino (o se leen directamente con nombres de 3 partes si ambas bases están en la misma instancia)
/// y la comparación se hace en el servidor con operaciones por conjuntos (JOIN + EXCEPT),
/// en lugar de comparar fila por fila en el cliente.
/// </para>
/// Mantiene abierta la conexión de destino (y la tabla temporal) entre <see cref="CompareAsync"/> y
/// <see cref="ExecuteAsync"/>. Debe liberarse con <see cref="DisposeAsync"/>.
/// </summary>
public sealed class TableTransfer : IAsyncDisposable
{
    private const string Stage = "#sts_stage";

    private sealed record ColumnPlan(ColumnDefinition Src, ColumnDefinition Tgt)
    {
        public string Name => Tgt.Name;
    }

    private readonly SqlConnectionInfo _source;
    private readonly SqlConnection _target;
    private readonly TransferOptions _opt;
    private readonly List<ColumnPlan> _cols = new();
    private readonly List<ColumnPlan> _key = new();
    private readonly List<string> _warnings = new();
    private string? _directSource;   // [db].[schema].[tabla] cuando se lee origen directamente
    private bool _staged;

    public TableName Table { get; }
    public TableDefinition SourceDefinition { get; }
    public TableDefinition? TargetDefinition { get; private set; }
    public TransferOptions Options => _opt;
    public IReadOnlyList<string> Warnings => _warnings;
    public bool UsesDirectRead => _directSource != null;
    public ComparisonCounts? LastComparison { get; private set; }

    private TableTransfer(SqlConnectionInfo source, SqlConnection target, TransferOptions opt, TableDefinition src, TableDefinition? tgt)
    {
        _source = source;
        _target = target;
        _opt = opt.Clone();
        Table = src.Table;
        SourceDefinition = src;
        TargetDefinition = tgt;
    }

    /// <summary>
    /// Cambia las opciones que sólo afectan a la aplicación de cambios (no a la comparación),
    /// para poder desplegar con otras opciones sin volver a transferir los datos de origen.
    /// </summary>
    public void SetApplyOptions(bool deleteExtraRows, bool fireTriggers, bool checkConstraints, bool tableLock, int batchSize)
    {
        _opt.DeleteExtraRows = deleteExtraRows;
        _opt.FireTriggers = fireTriggers;
        _opt.CheckConstraints = checkConstraints;
        _opt.TableLock = tableLock;
        _opt.BatchSize = batchSize;
    }

    private bool TargetMissing => TargetDefinition is null;
    private bool HasWhere => !string.IsNullOrWhiteSpace(_opt.Where);

    // ------------------------------------------------------------------ preparación

    public static async Task<TableTransfer> PrepareAsync(SqlConnectionInfo source, SqlConnectionInfo target, TableName table,
        TransferOptions options, CancellationToken ct = default)
    {
        TableDefinition src;
        string srcServer;
        DbIdentity srcDb;
        await using (var sconn = await source.OpenAsync(ct).ConfigureAwait(false))
        {
            src = await MetadataReader.GetTableAsync(sconn, table, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"La tabla {table} no existe en origen ({source.Display}).");
            srcServer = await ServerNameAsync(sconn, ct).ConfigureAwait(false);
            srcDb = await DbIdentityAsync(sconn, source.Database, ct).ConfigureAwait(false);
        }

        var tconn = await target.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var tgt = await MetadataReader.GetTableAsync(tconn, table, ct).ConfigureAwait(false);
            var t = new TableTransfer(source, tconn, options, src, tgt);

            // ¿Misma instancia? Se verifica por nombre de servidor y por identidad de la base de origen,
            // para no confundir dos servidores distintos con el mismo nombre.
            string tgtServer = await ServerNameAsync(tconn, ct).ConfigureAwait(false);
            if (string.Equals(srcServer, tgtServer, StringComparison.OrdinalIgnoreCase))
            {
                var seen = await DbIdentityAsync(tconn, source.Database, ct).ConfigureAwait(false);
                if (seen == srcDb && seen.Exists)
                {
                    if (string.Equals(source.Database, target.Database, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Origen y destino son la misma base de datos.");
                    if (options.AllowDirectSameServer &&
                        await Sql.ScalarLongAsync(tconn, $"SELECT HAS_DBACCESS({Sql.Lit(source.Database)});", ct).ConfigureAwait(false) == 1)
                        t._directSource = Sql.Q(source.Database) + "." + table.Quoted;
                }
            }

            t.BuildPlan();
            return t;
        }
        catch
        {
            await tconn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private readonly record struct DbIdentity(bool Exists, int Id, DateTime Created, Guid Broker);

    private static async Task<string> ServerNameAsync(SqlConnection c, CancellationToken ct)
    {
        using var cmd = Sql.Command(c, "SELECT ISNULL(@@SERVERNAME, CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)));");
        return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) as string ?? "";
    }

    private static async Task<DbIdentity> DbIdentityAsync(SqlConnection c, string db, CancellationToken ct)
    {
        using var cmd = Sql.Command(c, $"SELECT database_id, create_date, service_broker_guid FROM sys.databases WHERE name = {Sql.Lit(db)};");
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return default;
        return new DbIdentity(true, r.GetInt32(0), r.GetDateTime(1), r.IsDBNull(2) ? Guid.Empty : r.GetGuid(2));
    }

    /// <summary>Decide qué columnas se copian y con qué clave se comparan las filas.</summary>
    private void BuildPlan()
    {
        var src = SourceDefinition;
        bool useSourceStructure = TargetMissing || _opt.Mode == TransferMode.Recreate;
        var tgt = useSourceStructure ? src : TargetDefinition!;

        if (useSourceStructure)
        {
            // Valida que se pueda generar el CREATE TABLE.
            TableScripter.CreateTable(src, Table);
            if (src.UnsupportedIndexes.Count > 0)
                _warnings.Add("Índices que no se recrean (columnstore/XML/espaciales): " + string.Join(", ", src.UnsupportedIndexes));
        }

        foreach (var tc in tgt.Columns.OrderBy(c => c.Id))
        {
            var sc = src.Column(tc.Name);
            if (sc is null)
            {
                if (tc.IsCopyable && !tc.IsNullable && tc.DefaultDefinition is null && !tc.IsIdentity)
                    _warnings.Add($"La columna de destino {tc.Name} es NOT NULL, sin default, y no existe en origen: la inserción fallará.");
                else if (tc.IsCopyable)
                    _warnings.Add($"La columna de destino {tc.Name} no existe en origen; se deja con su valor por defecto.");
                continue;
            }
            if (!tc.IsCopyable || !sc.IsCopyable) continue;   // calculadas, rowversion, períodos temporales
            if (tc.IsClr != sc.IsClr)
            {
                _warnings.Add($"La columna {tc.Name} tiene tipos incompatibles ({sc.SqlType} → {tc.SqlType}); se omite.");
                continue;
            }
            _cols.Add(new ColumnPlan(sc, tc));
        }
        foreach (var sc in src.Columns.Where(c => c.IsCopyable && tgt.Column(c.Name) is null))
            _warnings.Add($"La columna de origen {sc.Name} no existe en destino; se omite.");

        if (_cols.Count == 0)
            throw new InvalidOperationException("No hay columnas en común para copiar.");

        if (_opt.Mode == TransferMode.Sync && !TargetMissing)
        {
            var key = src.FindComparisonKey()
                      ?? throw new InvalidOperationException("La tabla no tiene clave primaria ni índice único NOT NULL en origen: no se puede comparar fila a fila. Use el modo Reemplazar.");
            foreach (var (col, _) in key.KeyColumns)
            {
                var cp = _cols.FirstOrDefault(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidOperationException($"La columna clave {col} no existe (o no se puede copiar) en destino.");
                _key.Add(cp);
            }
        }
    }

    // ------------------------------------------------------------------ comparación

    /// <summary>
    /// Sincronizar: carga origen en la tabla temporal (si hace falta) y cuenta inserciones,
    /// actualizaciones y eliminaciones. No modifica el destino.
    /// </summary>
    public async Task<ComparisonCounts> CompareAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_opt.Mode != TransferMode.Sync)
            throw new InvalidOperationException("La comparación sólo aplica al modo Sincronizar.");

        if (TargetMissing)
        {
            progress?.Report("La tabla no existe en destino: se creará y se cargarán todas las filas.");
            long n;
            await using (var sconn = await _source.OpenAsync(ct).ConfigureAwait(false))
                n = await Sql.ScalarLongAsync(sconn, $"SELECT COUNT_BIG(*) FROM {Table.Quoted}{WhereClause()};", ct).ConfigureAwait(false);
            return LastComparison = new ComparisonCounts { TargetMissing = true, SourceRows = n, ToInsert = n };
        }

        await EnsureStagedAsync(progress, ct).ConfigureAwait(false);
        progress?.Report("Comparando en el servidor...");

        string S = SourceRowset();
        long srcRows = await Sql.ScalarLongAsync(_target, $"SELECT COUNT_BIG(*) FROM {S} AS s;", ct).ConfigureAwait(false);
        long ins = await Sql.ScalarLongAsync(_target, $"SELECT COUNT_BIG(*) FROM {S} AS s WHERE NOT EXISTS (SELECT 1 FROM {Table.Quoted} AS t WHERE {JoinPredicate()});", ct).ConfigureAwait(false);
        long upd = HasNonKeyColumns
            ? await Sql.ScalarLongAsync(_target, $"SELECT COUNT_BIG(*) FROM {Table.Quoted} AS t INNER JOIN {S} AS s ON {JoinPredicate()} WHERE {DiffPredicate()};", ct).ConfigureAwait(false)
            : 0;
        long del = _opt.DeleteExtraRows
            ? await Sql.ScalarLongAsync(_target, $"SELECT COUNT_BIG(*) FROM {Table.Quoted} AS t WHERE {TargetFilter()}NOT EXISTS (SELECT 1 FROM {S} AS s WHERE {JoinPredicate()});", ct).ConfigureAwait(false)
            : 0;

        return LastComparison = new ComparisonCounts
        {
            SourceRows = srcRows, ToInsert = ins, ToUpdate = upd, ToDelete = del, Identical = srcRows - ins - upd,
        };
    }

    /// <summary>Muestra hasta <paramref name="top"/> filas de un tipo de diferencia (requiere comparar antes).</summary>
    public async Task<DataTable> GetDiffAsync(DiffKind kind, int top, CancellationToken ct = default)
    {
        if (_opt.Mode != TransferMode.Sync || TargetMissing)
            throw new InvalidOperationException("Sólo disponible en modo Sincronizar con la tabla existente en destino.");
        await EnsureStagedAsync(null, ct).ConfigureAwait(false);

        string S = SourceRowset();
        string sql = kind switch
        {
            DiffKind.Insert =>
                $"SELECT TOP ({top}) {string.Join(", ", _cols.Select(c => DisplayExpr("s", c, sourceSide: true) + " AS " + Sql.Q(c.Name)))} " +
                $"FROM {S} AS s WHERE NOT EXISTS (SELECT 1 FROM {Table.Quoted} AS t WHERE {JoinPredicate()});",
            DiffKind.Delete =>
                $"SELECT TOP ({top}) {string.Join(", ", _cols.Select(c => DisplayExpr("t", c, sourceSide: false) + " AS " + Sql.Q(c.Name)))} " +
                $"FROM {Table.Quoted} AS t WHERE {TargetFilter()}NOT EXISTS (SELECT 1 FROM {S} AS s WHERE {JoinPredicate()});",
            _ =>
                $"SELECT TOP ({top}) " +
                string.Join(", ", _key.Select(c => DisplayExpr("t", c, false) + " AS " + Sql.Q(c.Name))
                    .Concat(NonKey.SelectMany(c => new[]
                    {
                        DisplayExpr("s", c, true) + " AS " + Sql.Q(c.Name + " (origen)"),
                        DisplayExpr("t", c, false) + " AS " + Sql.Q(c.Name + " (destino)"),
                    }))) +
                $" FROM {Table.Quoted} AS t INNER JOIN {S} AS s ON {JoinPredicate()} WHERE {DiffPredicate()};",
        };
        if (kind == DiffKind.Update && !HasNonKeyColumns) return new DataTable();

        using var cmd = Sql.Command(_target, sql);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var dt = new DataTable();
        dt.Load(r);
        return dt;
    }

    // ------------------------------------------------------------------ ejecución

    public async Task<TransferResult> ExecuteAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new TransferResult { Table = Table };
        result.Messages.AddRange(_warnings);

        if (_opt.Mode == TransferMode.Sync && !TargetMissing)
            await ApplySyncAsync(result, progress, ct).ConfigureAwait(false);
        else
            await ReloadAsync(result, progress, ct).ConfigureAwait(false);

        if (!_opt.CheckConstraints && TargetDefinition?.EnabledConstraints.Count > 0 && (result.Inserted + result.Updated) > 0)
            result.Messages.Add("Las FKs/CHECK no se verificaron durante la carga y quedaron como 'no confiables' (is_not_trusted). " +
                                "Puede validarlas con ALTER TABLE ... WITH CHECK CHECK CONSTRAINT ALL.");

        // La tabla temporal ya no refleja el estado de destino.
        LastComparison = null;
        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>Sincronizar: DELETE / UPDATE / INSERT por conjuntos dentro de una transacción.</summary>
    private async Task ApplySyncAsync(TransferResult result, IProgress<string>? progress, CancellationToken ct)
    {
        await EnsureStagedAsync(progress, ct).ConfigureAwait(false);
        string S = SourceRowset();
        string T = Table.Quoted;
        var tgt = TargetDefinition!;

        await using var tx = (SqlTransaction)await _target.BeginTransactionAsync(ct).ConfigureAwait(false);
        await DisableAsync(tgt, tx, ct).ConfigureAwait(false);

        if (_opt.DeleteExtraRows)
        {
            progress?.Report("Eliminando filas sobrantes...");
            result.Deleted = await Sql.DmlAsync(_target,
                $"DELETE t FROM {T} AS t WHERE {TargetFilter()}NOT EXISTS (SELECT 1 FROM {S} AS s WHERE {JoinPredicate()})", ct, tx).ConfigureAwait(false);
        }

        if (HasNonKeyColumns)
        {
            progress?.Report("Actualizando filas distintas...");
            var sets = NonKey.Where(c => !c.Tgt.IsIdentity).Select(c => $"t.{Sql.Q(c.Name)} = {ValueExpr(c)}").ToList();
            if (sets.Count > 0)
                result.Updated = await Sql.DmlAsync(_target,
                    $"UPDATE t SET {string.Join(", ", sets)} FROM {T} AS t INNER JOIN {S} AS s ON {JoinPredicate()} WHERE {DiffPredicate()}", ct, tx).ConfigureAwait(false);
            if (NonKey.Any(c => c.Tgt.IsIdentity))
                result.Messages.Add("La columna identity no forma parte de la clave y no se puede actualizar; sólo se copia en filas nuevas.");
        }

        progress?.Report("Insertando filas nuevas...");
        bool identity = _cols.Any(c => c.Tgt.IsIdentity);
        string insert = $"INSERT INTO {T} ({ColumnList()}) SELECT {string.Join(", ", _cols.Select(ValueExpr))} " +
                        $"FROM {S} AS s WHERE NOT EXISTS (SELECT 1 FROM {T} AS t WHERE {JoinPredicate()})";
        if (identity) await Sql.ExecAsync(_target, $"SET IDENTITY_INSERT {T} ON;", ct, tx).ConfigureAwait(false);
        result.Inserted = await Sql.DmlAsync(_target, insert, ct, tx).ConfigureAwait(false);
        if (identity) await Sql.ExecAsync(_target, $"SET IDENTITY_INSERT {T} OFF;", ct, tx).ConfigureAwait(false);

        await EnableAsync(tgt, tx, ct).ConfigureAwait(false);
        progress?.Report("Confirmando transacción...");
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reemplazar / Recrear / tabla nueva: vacía (o crea) la tabla y la carga completa.</summary>
    private async Task ReloadAsync(TransferResult result, IProgress<string>? progress, CancellationToken ct)
    {
        string T = Table.Quoted;
        bool create = TargetMissing || _opt.Mode == TransferMode.Recreate;

        await using var tx = (SqlTransaction)await _target.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (create)
        {
            if (!TargetMissing)
            {
                if (TargetDefinition!.ReferencedByForeignKeys > 0)
                    throw new InvalidOperationException($"No se puede recrear {Table}: otras tablas la referencian con FOREIGN KEY. Use el modo Reemplazar.");
                progress?.Report("Eliminando tabla de destino...");
                await Sql.ExecAsync(_target, $"DROP TABLE {T};", ct, tx).ConfigureAwait(false);
            }
            progress?.Report("Creando tabla de destino...");
            await Sql.ExecAsync(_target, TableScripter.CreateSchemaIfMissing(Table.Schema), ct, tx).ConfigureAwait(false);
            await Sql.ExecAsync(_target, TableScripter.CreateTable(SourceDefinition, Table), ct, tx).ConfigureAwait(false);
            result.Messages.Add(TargetMissing ? "Tabla creada en destino." : "Tabla recreada en destino.");
        }
        else
        {
            var tgt = TargetDefinition!;
            bool truncate = !HasWhere && tgt.ReferencedByForeignKeys == 0 && !tgt.IsTemporal && !tgt.IsMemoryOptimized;
            progress?.Report(truncate ? "Vaciando tabla (TRUNCATE)..." : "Eliminando filas de destino...");
            if (truncate)
                await Sql.ExecAsync(_target, $"TRUNCATE TABLE {T};", ct, tx).ConfigureAwait(false);
            else
                result.Deleted = await Sql.DmlAsync(_target, $"DELETE FROM {T}{WhereClause()}", ct, tx).ConfigureAwait(false);
        }

        // Una tabla recién creada no tiene triggers ni FKs que deshabilitar.
        var current = create ? null : TargetDefinition;

        if (_directSource != null)
        {
            progress?.Report("Copiando datos (misma instancia, INSERT ... SELECT)...");
            if (current != null) await DisableAsync(current, tx, ct).ConfigureAwait(false);
            bool identity = _cols.Any(c => c.Tgt.IsIdentity);
            if (identity) await Sql.ExecAsync(_target, $"SET IDENTITY_INSERT {T} ON;", ct, tx).ConfigureAwait(false);
            result.Inserted = await Sql.DmlAsync(_target,
                $"INSERT INTO {T}{(_opt.TableLock ? " WITH (TABLOCK)" : "")} ({ColumnList()}) SELECT {string.Join(", ", _cols.Select(c => Sql.Q(c.Src.Name)))} FROM {_directSource}{WhereClause()}", ct, tx).ConfigureAwait(false);
            if (identity) await Sql.ExecAsync(_target, $"SET IDENTITY_INSERT {T} OFF;", ct, tx).ConfigureAwait(false);
            if (current != null) await EnableAsync(current, tx, ct).ConfigureAwait(false);
        }
        else if (_cols.Any(c => c.Src.IsClr))
        {
            // SqlBulkCopy no puede escribir tipos CLR sin sus ensamblados: se cargan como varbinary
            // en la tabla temporal y se convierten con CAST en el servidor.
            await StageAsync(progress, ct, tx).ConfigureAwait(false);
            if (current != null) await DisableAsync(current, tx, ct).ConfigureAwait(false);
            bool identity = _cols.Any(c => c.Tgt.IsIdentity);
            if (identity) await Sql.ExecAsync(_target, $"SET IDENTITY_INSERT {T} ON;", ct, tx).ConfigureAwait(false);
            progress?.Report("Insertando desde tabla temporal...");
            result.Inserted = await Sql.DmlAsync(_target,
                $"INSERT INTO {T}{(_opt.TableLock ? " WITH (TABLOCK)" : "")} ({ColumnList()}) SELECT {string.Join(", ", _cols.Select(ValueExpr))} FROM {Stage} AS s", ct, tx).ConfigureAwait(false);
            if (identity) await Sql.ExecAsync(_target, $"SET IDENTITY_INSERT {T} OFF;", ct, tx).ConfigureAwait(false);
            if (current != null) await EnableAsync(current, tx, ct).ConfigureAwait(false);
        }
        else
        {
            var options = SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls;
            if (_opt.TableLock) options |= SqlBulkCopyOptions.TableLock;
            if (_opt.CheckConstraints) options |= SqlBulkCopyOptions.CheckConstraints;
            if (_opt.FireTriggers) options |= SqlBulkCopyOptions.FireTriggers;
            result.Inserted = await BulkLoadAsync(T, options, tx, progress, ct).ConfigureAwait(false);
        }

        if (create)
        {
            var post = TableScripter.PostLoadStatements(SourceDefinition, Table);
            if (post.Count > 0) progress?.Report("Creando índices y constraints...");
            foreach (var stmt in post)
                await Sql.ExecAsync(_target, stmt, ct, tx).ConfigureAwait(false);
        }

        progress?.Report("Confirmando transacción...");
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ carga a la tabla temporal

    private async Task EnsureStagedAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_directSource != null || _staged) return;
        await StageAsync(progress, ct, null).ConfigureAwait(false);
    }

    private async Task StageAsync(IProgress<string>? progress, CancellationToken ct, SqlTransaction? tx)
    {
        await Sql.ExecAsync(_target, $"IF OBJECT_ID('tempdb..{Stage}') IS NOT NULL DROP TABLE {Stage};", ct, tx).ConfigureAwait(false);
        var ddl = new StringBuilder($"CREATE TABLE {Stage} (");
        ddl.Append(string.Join(", ", _cols.Select(c =>
        {
            string def = Sql.Q(c.Name) + " " + StageType(c.Src);
            string? coll = c.Src.IsString ? (c.Tgt.IsString && c.Tgt.Collation != null ? c.Tgt.Collation : c.Src.Collation) : null;
            if (coll != null) def += " COLLATE " + coll;
            return def + " NULL";
        })));
        ddl.Append(");");
        await Sql.ExecAsync(_target, ddl.ToString(), ct, tx).ConfigureAwait(false);

        long rows = await BulkLoadAsync(Stage, SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock, tx, progress, ct).ConfigureAwait(false);

        if (_key.Count > 0)
        {
            progress?.Report("Indexando tabla temporal...");
            await Sql.ExecAsync(_target,
                $"CREATE CLUSTERED INDEX [IX_sts_stage] ON {Stage} ({string.Join(", ", _key.Select(k => Sql.Q(k.Name)))});", ct, tx).ConfigureAwait(false);
        }
        _staged = tx == null;   // dentro de una transacción la temporal se descarta con ella si falla
        progress?.Report($"{rows:N0} filas de origen transferidas.");
    }

    private async Task<long> BulkLoadAsync(string destination, SqlBulkCopyOptions options, SqlTransaction? tx,
        IProgress<string>? progress, CancellationToken ct)
    {
        // Las columnas se leen en el mismo orden que el destino para poder usar SequentialAccess (streaming).
        string select = "SELECT " + string.Join(", ", _cols.Select(c =>
                            (c.Src.IsClr ? $"CAST({Sql.Q(c.Src.Name)} AS varbinary(max))" : Sql.Q(c.Src.Name)) + " AS " + Sql.Q(c.Name)))
                        + $" FROM {Table.Quoted}{WhereClause()};";

        progress?.Report("Leyendo origen y enviando datos...");
        await using var sconn = await _source.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = Sql.Command(sconn, select);
        using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

        using var bulk = new SqlBulkCopy(_target, options, tx)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = 0,
            BatchSize = Math.Max(0, _opt.BatchSize),
            EnableStreaming = true,
            NotifyAfter = 50_000,
        };
        foreach (var c in _cols) bulk.ColumnMappings.Add(c.Name, c.Name);
        var sw = Stopwatch.StartNew();
        bulk.SqlRowsCopied += (_, e) =>
        {
            double secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            progress?.Report($"{e.RowsCopied:N0} filas enviadas ({e.RowsCopied / secs:N0} filas/s)...");
        };
        await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        return bulk.RowsCopied64;
    }

    // ------------------------------------------------------------------ triggers / constraints

    private async Task DisableAsync(TableDefinition tgt, SqlTransaction tx, CancellationToken ct)
    {
        foreach (var sql in ToggleStatements(tgt, enable: false))
            await Sql.ExecAsync(_target, sql, ct, tx).ConfigureAwait(false);
    }

    private async Task EnableAsync(TableDefinition tgt, SqlTransaction tx, CancellationToken ct)
    {
        foreach (var sql in ToggleStatements(tgt, enable: true))
            await Sql.ExecAsync(_target, sql, ct, tx).ConfigureAwait(false);
    }

    private IEnumerable<string> ToggleStatements(TableDefinition tgt, bool enable)
    {
        string T = Table.Quoted;
        if (!_opt.FireTriggers)
            foreach (var tr in tgt.EnabledTriggers)
                yield return $"{(enable ? "ENABLE" : "DISABLE")} TRIGGER {Sql.Q(Table.Schema)}.{Sql.Q(tr)} ON {T};";
        if (!_opt.CheckConstraints)
            foreach (var c in tgt.EnabledConstraints)
                yield return $"ALTER TABLE {T} {(enable ? "CHECK" : "NOCHECK")} CONSTRAINT {Sql.Q(c)};";
    }

    // ------------------------------------------------------------------ generación de SQL

    private IEnumerable<ColumnPlan> NonKey => _cols.Where(c => !_key.Contains(c));
    private bool HasNonKeyColumns => NonKey.Any();

    private string ColumnList() => string.Join(", ", _cols.Select(c => Sql.Q(c.Name)));

    private string WhereClause() => HasWhere ? $" WHERE ({_opt.Where})" : "";

    /// <summary>Filtro para las filas de destino (prefijo de un WHERE que continúa).</summary>
    private string TargetFilter() => HasWhere ? $"({_opt.Where}) AND " : "";

    /// <summary>Origen como conjunto de filas con alias de columnas iguales a destino.</summary>
    private string SourceRowset()
    {
        if (_directSource == null) return Stage;
        string cols = string.Join(", ", _cols.Select(c => Sql.Q(c.Src.Name) + " AS " + Sql.Q(c.Name)));
        return $"(SELECT {cols} FROM {_directSource}{WhereClause()})";
    }

    private string JoinPredicate() => string.Join(" AND ", _key.Select(k =>
    {
        string s = "s." + Sql.Q(k.Name);
        if (_directSource != null && k.Src.IsString && k.Tgt.IsString && k.Tgt.Collation != null)
            s += " COLLATE " + k.Tgt.Collation;
        return $"t.{Sql.Q(k.Name)} = {s}";
    }));

    /// <summary>Verdadero si alguna columna no clave difiere (EXCEPT trata NULL = NULL).</summary>
    private string DiffPredicate()
    {
        var nk = NonKey.ToList();
        return $"EXISTS (SELECT {string.Join(", ", nk.Select(c => CompareExpr("s", c, true)))} " +
               $"EXCEPT SELECT {string.Join(", ", nk.Select(c => CompareExpr("t", c, false)))})";
    }

    /// <summary>Tipo de una columna de origen en la tabla temporal.</summary>
    private static string StageType(ColumnDefinition c) =>
        c.IsClr ? "varbinary(max)" : c.TypeName switch
        {
            "text" => "varchar(max)",
            "ntext" => "nvarchar(max)",
            "image" => "varbinary(max)",
            _ => c.SqlType,
        };

    /// <summary>Tipo de la columna tal como se ve en el lado indicado (origen leído de la temporal o directo).</summary>
    private (string Type, bool Clr, short MaxLength) SideType(ColumnPlan c, bool sourceSide)
    {
        if (!sourceSide) return (c.Tgt.TypeName, c.Tgt.IsClr, c.Tgt.MaxLength);
        if (_directSource != null) return (c.Src.TypeName, c.Src.IsClr, c.Src.MaxLength);
        string st = StageType(c.Src);
        int p = st.IndexOf('(');
        return (p < 0 ? st : st[..p], false, st.EndsWith("(max)") ? (short)-1 : c.Src.MaxLength);
    }

    /// <summary>
    /// Expresión comparable de forma exacta: textos en collation binaria (distingue mayúsculas/acentos),
    /// tipos no comparables (xml, text, image, CLR) convertidos a tipos comparables.
    /// </summary>
    private string CompareExpr(string alias, ColumnPlan c, bool sourceSide)
    {
        var (type, clr, maxLen) = SideType(c, sourceSide);
        string x = alias + "." + Sql.Q(c.Name);
        if (clr) return $"CAST({x} AS varbinary(max))";
        const string bin = " COLLATE Latin1_General_BIN2";
        return type switch
        {
            "nchar" or "nvarchar" => x + bin,
            "char" or "varchar" => $"CAST({x} AS nvarchar({(maxLen == -1 || maxLen > 4000 ? "max" : maxLen.ToString())})){bin}",
            "text" or "ntext" or "xml" => $"CAST({x} AS nvarchar(max)){bin}",
            "image" => $"CAST({x} AS varbinary(max))",
            _ => x,
        };
    }

    /// <summary>Valor a insertar/actualizar en destino tomado de la fila de origen <c>s</c>.</summary>
    private string ValueExpr(ColumnPlan c)
    {
        string x = "s." + Sql.Q(c.Name);
        return _directSource == null && c.Src.IsClr ? $"CAST({x} AS {c.Tgt.TypeName})" : x;
    }

    /// <summary>Expresión para mostrar en la grilla (los tipos binarios y CLR se convierten a texto).</summary>
    private string DisplayExpr(string alias, ColumnPlan c, bool sourceSide)
    {
        string x = alias + "." + Sql.Q(c.Name);
        if (c.Tgt.IsClr)
        {
            if (sourceSide && _directSource == null) x = $"CAST({x} AS {c.Tgt.TypeName})";
            return $"{x}.ToString()";
        }
        var (type, _, _) = SideType(c, sourceSide);
        return type switch
        {
            "binary" or "varbinary" or "image" => $"CONVERT(varchar(max), CAST({x} AS varbinary(max)), 1)",
            "sql_variant" => $"CAST({x} AS nvarchar(4000))",
            "xml" => $"CAST({x} AS nvarchar(max))",
            _ => x,
        };
    }

    // ------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_staged && _target.State == ConnectionState.Open)
                await Sql.ExecAsync(_target, $"IF OBJECT_ID('tempdb..{Stage}') IS NOT NULL DROP TABLE {Stage};", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // La temporal se elimina igualmente al cerrar la conexión.
        }
        await _target.DisposeAsync().ConfigureAwait(false);
    }
}
