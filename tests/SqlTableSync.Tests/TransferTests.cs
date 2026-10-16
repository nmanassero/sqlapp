using Microsoft.Data.SqlClient;
using SqlTableSync.Core;
using Xunit;
using Xunit.Abstractions;

namespace SqlTableSync.Tests;

/// <summary>
/// Pruebas de integración contra un SQL Server real. Se ejecutan sólo si está definida la variable
/// de entorno SQLSYNC_TEST_SERVER (y SQLSYNC_TEST_USER / SQLSYNC_TEST_PASSWORD para autenticación SQL).
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQLSYNC_TEST_SERVER")))
            Skip = "SQLSYNC_TEST_SERVER no está definida.";
    }
}

public sealed class TransferTests
{
    private const string SrcDb = "SqlSyncTestSrc";
    private const string TgtDb = "SqlSyncTestTgt";
    private readonly ITestOutputHelper _out;

    public TransferTests(ITestOutputHelper output) => _out = output;

    private static SqlConnectionInfo Conn(string db)
    {
        string? user = Environment.GetEnvironmentVariable("SQLSYNC_TEST_USER");
        return new SqlConnectionInfo
        {
            Server = Environment.GetEnvironmentVariable("SQLSYNC_TEST_SERVER")!,
            Database = db,
            IntegratedSecurity = string.IsNullOrEmpty(user),
            User = user ?? "",
            Password = Environment.GetEnvironmentVariable("SQLSYNC_TEST_PASSWORD") ?? "",
        };
    }

    private static async Task ExecAsync(string db, string sql)
    {
        await using var c = await Conn(db).OpenAsync(default, db);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string db, string sql)
    {
        await using var c = await Conn(db).OpenAsync(default, db);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static readonly SemaphoreSlim DbLock = new(1, 1);

    private static async Task ResetDatabasesAsync(string tableDdl, string fill)
    {
        foreach (var db in new[] { SrcDb, TgtDb })
        {
            await ExecAsync("master", $@"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];");
        }
        foreach (var db in new[] { SrcDb, TgtDb })
            await ExecAsync(db, @"DECLARE @s nvarchar(max) = N'';
SELECT @s += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';' FROM sys.foreign_keys;
SELECT @s += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' FROM sys.tables;
EXEC(@s);
IF SCHEMA_ID('ventas') IS NOT NULL DROP SCHEMA ventas;");
        // Destino con collation distinta a origen para probar conflictos de collation.
        if (await ScalarAsync("master", $"SELECT CASE WHEN DATABASEPROPERTYEX('{TgtDb}', 'Collation') = 'Latin1_General_CS_AS' THEN 1 ELSE 0 END") == 0)
            await ExecAsync("master", $"ALTER DATABASE [{TgtDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ALTER DATABASE [{TgtDb}] COLLATE Latin1_General_CS_AS; ALTER DATABASE [{TgtDb}] SET MULTI_USER;");
        await ExecAsync(SrcDb, "CREATE SCHEMA ventas;");
        await ExecAsync(SrcDb, tableDdl);
        await ExecAsync(SrcDb, fill);
    }

    private const string ComplexTable = @"CREATE TABLE ventas.Cliente (
    Id int IDENTITY(10,5) NOT NULL CONSTRAINT PK_Cliente PRIMARY KEY,
    Codigo varchar(20) COLLATE Modern_Spanish_CI_AS NOT NULL,
    Nombre nvarchar(100) NULL,
    Saldo decimal(18,4) NOT NULL CONSTRAINT DF_Cliente_Saldo DEFAULT (0),
    Alta datetime2(3) NOT NULL,
    Notas text NULL,
    Datos xml NULL,
    Foto varbinary(max) NULL,
    Ubicacion geography NULL,
    Nodo hierarchyid NULL,
    Variante sql_variant NULL,
    Guid uniqueidentifier NOT NULL CONSTRAINT DF_Cliente_Guid DEFAULT NEWID(),
    Doble AS (Saldo * 2) PERSISTED,
    Ver rowversion,
    CONSTRAINT CK_Cliente_Saldo CHECK (Saldo >= -1000000)
);
CREATE UNIQUE INDEX UX_Cliente_Codigo ON ventas.Cliente (Codigo) INCLUDE (Nombre);
CREATE INDEX IX_Cliente_Alta ON ventas.Cliente (Alta DESC) WHERE Nombre IS NOT NULL;";

    private const string ComplexFill = @"SET NOCOUNT ON;
INSERT ventas.Cliente (Codigo, Nombre, Saldo, Alta, Notas, Datos, Foto, Ubicacion, Nodo, Variante)
SELECT TOP (5000)
    'C' + RIGHT('00000' + CAST(n AS varchar(10)), 6),
    CASE WHEN n % 7 = 0 THEN NULL ELSE N'Cliente ñandú ' + CAST(n AS nvarchar(10)) END,
    n * 1.25,
    DATEADD(minute, n, '2020-01-01'),
    CASE WHEN n % 3 = 0 THEN REPLICATE('x', n % 100) END,
    CASE WHEN n % 2 = 0 THEN CAST('<a n=""' + CAST(n AS varchar(10)) + '""/>' AS xml) END,
    CASE WHEN n % 5 = 0 THEN CAST(REPLICATE(CAST(0x0102 AS varbinary(max)), n % 50) AS varbinary(max)) END,
    CASE WHEN n % 4 = 0 THEN geography::Point(-34.6 + n / 10000.0, -58.4, 4326) END,
    CASE WHEN n % 6 = 0 THEN hierarchyid::Parse('/' + CAST(n % 9 AS varchar(5)) + '/') END,
    CASE n % 3 WHEN 0 THEN CAST(n AS sql_variant) WHEN 1 THEN CAST(N'v' AS sql_variant) END
FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;";

    /// <summary>Filas distintas entre origen y destino (en ambos sentidos), comparando de forma exacta.</summary>
    private static Task<long> DifferencesAsync(string cols, string table = "ventas.Cliente") => ScalarAsync(SrcDb, $@"
SELECT COUNT_BIG(*) FROM (
  (SELECT {cols} FROM [{SrcDb}].{table} EXCEPT SELECT {cols} FROM [{TgtDb}].{table})
  UNION ALL
  (SELECT {cols} FROM [{TgtDb}].{table} EXCEPT SELECT {cols} FROM [{SrcDb}].{table})
) d;");

    private const string ClienteCols = @"Id, Codigo COLLATE Latin1_General_BIN2 AS c2, Nombre COLLATE Latin1_General_BIN2 AS c3, Saldo, Alta,
CAST(Notas AS varchar(max)) COLLATE Latin1_General_BIN2 AS c6, CAST(Datos AS nvarchar(max)) COLLATE Latin1_General_BIN2 AS c7, Foto,
CAST(Ubicacion AS varbinary(max)) AS c9, CAST(Nodo AS varbinary(max)) AS c10,
CAST(Variante AS nvarchar(4000)) COLLATE Latin1_General_BIN2 AS c11, CAST(SQL_VARIANT_PROPERTY(Variante, 'BaseType') AS nvarchar(128)) AS c11b, Guid, Doble";

    private async Task<TransferResult> RunAsync(TransferOptions opt, string table = "ventas.Cliente", bool compare = false, Action<ComparisonCounts>? check = null)
    {
        var parts = table.Split('.');
        await using var t = await TableTransfer.PrepareAsync(Conn(SrcDb), Conn(TgtDb), new TableName(parts[0], parts[1]), opt);
        foreach (var w in t.Warnings) _out.WriteLine("AVISO: " + w);
        _out.WriteLine($"Lectura directa: {t.UsesDirectRead}");
        if (compare)
        {
            var c = await t.CompareAsync();
            _out.WriteLine($"Comparación: +{c.ToInsert} ~{c.ToUpdate} -{c.ToDelete} ={c.Identical}");
            check?.Invoke(c);
            if (c.ToUpdate > 0)
            {
                var diff = await t.GetDiffAsync(DiffKind.Update, 5);
                Assert.True(diff.Rows.Count > 0);
            }
        }
        var r = await t.ExecuteAsync();
        _out.WriteLine($"Resultado: +{r.Inserted} ~{r.Updated} -{r.Deleted} en {r.Elapsed.TotalMilliseconds:N0} ms");
        foreach (var m in r.Messages) _out.WriteLine("  " + m);
        return r;
    }

    [SqlServerFact]
    public async Task Recrear_Y_Sincronizar_TablaCompleja()
    {
        await DbLock.WaitAsync();
        try
        {
            foreach (bool direct in new[] { false, true })
            {
                _out.WriteLine($"===== AllowDirectSameServer = {direct}");
                await ResetDatabasesAsync(ComplexTable, ComplexFill);

                // 1) La tabla no existe en destino → se crea y carga.
                var r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync, AllowDirectSameServer = direct }, compare: true,
                    check: c => Assert.True(c.TargetMissing));
                Assert.Equal(5000, r.Inserted);
                Assert.Equal(0, await DifferencesAsync(ClienteCols));
                // Índices, CHECK y defaults recreados.
                Assert.Equal(3, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('ventas.Cliente') AND index_id > 0"));
                Assert.Equal(1, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_Cliente_Saldo' AND is_not_trusted = 0"));

                // 2) Se modifica destino: borrar, cambiar, agregar sobrantes, cambio sólo de mayúsculas.
                await ExecAsync(TgtDb, @"
DELETE FROM ventas.Cliente WHERE Id % 100 = 0;
UPDATE ventas.Cliente SET Saldo = Saldo + 1 WHERE Id % 97 = 0;
UPDATE ventas.Cliente SET Nombre = UPPER(Nombre) WHERE Id = 15;
UPDATE ventas.Cliente SET Ubicacion = NULL WHERE Id = 25;
UPDATE ventas.Cliente SET Datos = '<b/>' WHERE Id = 20;
SET IDENTITY_INSERT ventas.Cliente ON;
INSERT ventas.Cliente (Id, Codigo, Nombre, Saldo, Alta) VALUES (999999, 'ZZZ1', N'sobra', 1, SYSDATETIME()), (999998, 'ZZZ2', N'sobra', 1, SYSDATETIME());
SET IDENTITY_INSERT ventas.Cliente OFF;");
                long removed = await ScalarAsync(SrcDb, "SELECT COUNT(*) FROM ventas.Cliente WHERE Id % 100 = 0");
                long changed = await ScalarAsync(SrcDb, "SELECT COUNT(*) FROM ventas.Cliente WHERE Id % 97 = 0 AND Id % 100 <> 0");
                Assert.True(await DifferencesAsync(ClienteCols) > 0);

                r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync, AllowDirectSameServer = direct }, compare: true, check: c =>
                {
                    Assert.Equal(removed, c.ToInsert);
                    Assert.Equal(changed + 3, c.ToUpdate);
                    Assert.Equal(2, c.ToDelete);
                });
                Assert.Equal(removed, r.Inserted);
                Assert.Equal(changed + 3, r.Updated);
                Assert.Equal(2, r.Deleted);
                Assert.Equal(0, await DifferencesAsync(ClienteCols));

                // 3) Sin diferencias.
                r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync, AllowDirectSameServer = direct }, compare: true,
                    check: c => Assert.False(c.HasDifferences));
                Assert.Equal(0, r.Inserted + r.Updated + r.Deleted);

                // 4) Reemplazar sólo un subconjunto con filtro.
                await ExecAsync(TgtDb, "UPDATE ventas.Cliente SET Saldo = -5 WHERE Id BETWEEN 100 AND 200 OR Id = 4000;");
                r = await RunAsync(new TransferOptions { Mode = TransferMode.Replace, Where = "Id BETWEEN 100 AND 200", AllowDirectSameServer = direct });
                Assert.Equal(1, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM ventas.Cliente WHERE Saldo = -5"));
                Assert.Equal(2, await DifferencesAsync(ClienteCols));   // Id 4000, contada en ambos sentidos

                // 5) Reemplazar completo (TRUNCATE + carga).
                r = await RunAsync(new TransferOptions { Mode = TransferMode.Replace, AllowDirectSameServer = direct });
                Assert.Equal(5000, r.Inserted);
                Assert.Equal(0, await DifferencesAsync(ClienteCols));

                // 6) Recrear.
                r = await RunAsync(new TransferOptions { Mode = TransferMode.Recreate, AllowDirectSameServer = direct });
                Assert.Equal(5000, r.Inserted);
                Assert.Equal(0, await DifferencesAsync(ClienteCols));
                // La identidad sigue desde el máximo copiado.
                Assert.Equal(await ScalarAsync(SrcDb, "SELECT IDENT_CURRENT('ventas.Cliente')"), await ScalarAsync(TgtDb, "SELECT IDENT_CURRENT('ventas.Cliente')"));
            }
        }
        finally { DbLock.Release(); }
    }

    [SqlServerFact]
    public async Task Sincronizar_ConFiltro_NoTocaFilasFueraDelFiltro()
    {
        await DbLock.WaitAsync();
        try
        {
            await ResetDatabasesAsync("CREATE TABLE dbo.T (A int NOT NULL, B nvarchar(10) NOT NULL, V int NULL, CONSTRAINT PK_T PRIMARY KEY (A, B));",
                "INSERT dbo.T VALUES (1,N'x',1),(2,N'x',2),(3,N'x',3),(4,N'y',4);");
            await RunAsync(new TransferOptions { Mode = TransferMode.Recreate, AllowDirectSameServer = false }, "dbo.T");
            await ExecAsync(TgtDb, "UPDATE dbo.T SET V = 99; INSERT dbo.T VALUES (5,N'x',5),(6,N'y',6);");

            var r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync, Where = "B = N'x'", AllowDirectSameServer = false }, "dbo.T", compare: true);
            Assert.Equal(3, r.Updated);
            Assert.Equal(1, r.Deleted);    // (5,x) se elimina; (6,y) queda fuera del filtro
            Assert.Equal(2, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM dbo.T WHERE B = N'y'"));
            Assert.Equal(99, await ScalarAsync(TgtDb, "SELECT V FROM dbo.T WHERE A = 4"));
        }
        finally { DbLock.Release(); }
    }

    [SqlServerFact]
    public async Task Reemplazar_ConFkEntrante_Y_Trigger()
    {
        await DbLock.WaitAsync();
        try
        {
            await ResetDatabasesAsync("CREATE TABLE dbo.Padre (Id int NOT NULL PRIMARY KEY, N int NOT NULL);",
                "INSERT dbo.Padre VALUES (1,1),(2,2),(3,3);");
            await RunAsync(new TransferOptions { Mode = TransferMode.Recreate }, "dbo.Padre");
            await ExecAsync(TgtDb, @"CREATE TABLE dbo.Log (X int);
CREATE TABLE dbo.Hijo (Id int PRIMARY KEY, PadreId int NOT NULL REFERENCES dbo.Padre(Id));
INSERT dbo.Hijo VALUES (1, 1);");
            await ExecAsync(TgtDb, "CREATE TRIGGER dbo.TR_Padre ON dbo.Padre AFTER INSERT, UPDATE, DELETE AS INSERT dbo.Log VALUES (1);");
            await ExecAsync(SrcDb, "UPDATE dbo.Padre SET N = N * 10;");

            // FK entrante: no se puede TRUNCATE, se usa DELETE; el trigger no se ejecuta.
            var r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync }, "dbo.Padre", compare: true);
            Assert.Equal(3, r.Updated);
            Assert.Equal(0, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM dbo.Log"));
            Assert.Equal(1, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_Padre' AND is_disabled = 0"));

            // Reemplazar con la fila padre referenciada por Hijo: falla y no deja cambios.
            await ExecAsync(SrcDb, "DELETE dbo.Padre WHERE Id = 1;");
            await Assert.ThrowsAsync<SqlException>(() => RunAsync(new TransferOptions { Mode = TransferMode.Replace }, "dbo.Padre"));
            Assert.Equal(3, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM dbo.Padre"));

            // Recrear no está permitido con FKs entrantes.
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new TransferOptions { Mode = TransferMode.Recreate }, "dbo.Padre"));

            // Con triggers habilitados, se ejecutan.
            await ExecAsync(SrcDb, "INSERT dbo.Padre VALUES (1, 77);");
            r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync, FireTriggers = true }, "dbo.Padre", compare: true);
            Assert.Equal(1, r.Updated);
            // Triggers de sentencia: se disparan con DELETE, UPDATE e INSERT (aunque afecten 0 filas).
            Assert.Equal(3, await ScalarAsync(TgtDb, "SELECT COUNT(*) FROM dbo.Log"));
        }
        finally { DbLock.Release(); }
    }

    [SqlServerFact]
    public async Task Rendimiento_UnMillonDeFilas()
    {
        await DbLock.WaitAsync();
        try
        {
            await ResetDatabasesAsync(@"CREATE TABLE dbo.Grande (Id bigint NOT NULL PRIMARY KEY, Cliente int NOT NULL, Importe decimal(18,2) NOT NULL,
Fecha datetime2(0) NOT NULL, Texto nvarchar(200) NULL);",
                @"INSERT dbo.Grande WITH (TABLOCK)
SELECT TOP (1000000) n, n % 5000, n * 0.01, DATEADD(second, n, '2024-01-01'), N'Descripción de la fila ' + CAST(n AS nvarchar(20))
FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c) x;");

            foreach (bool direct in new[] { false, true })
            {
                _out.WriteLine($"===== AllowDirectSameServer = {direct}");
                var r = await RunAsync(new TransferOptions { Mode = TransferMode.Recreate, AllowDirectSameServer = direct }, "dbo.Grande");
                Assert.Equal(1_000_000, r.Inserted);
                await ExecAsync(TgtDb, "DELETE FROM dbo.Grande WHERE Id % 1000 = 0; UPDATE dbo.Grande SET Importe = 0 WHERE Id % 333 = 0;");
                r = await RunAsync(new TransferOptions { Mode = TransferMode.Sync, AllowDirectSameServer = direct }, "dbo.Grande", compare: true);
                Assert.Equal(1000, r.Inserted);
                Assert.Equal(0, await DifferencesAsync("Id, Cliente, Importe, Fecha, Texto", "dbo.Grande"));
            }
        }
        finally { DbLock.Release(); }
    }
}
