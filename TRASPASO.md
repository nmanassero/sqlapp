# SqlTableSync — Resumen de traspaso

Al 2026-10-23. Aplicación de escritorio para Windows (.NET 8, WinForms) que copia y sincroniza los
datos de tablas entre bases SQL Server sin restaurar la base completa. Versión 1.1.0 publicada, con
pruebas automáticas en verde; **falta probarla en un Windows real** contra los servidores del usuario.

Para el uso y la compilación en detalle, ver `README.md`.

## Pedido original del usuario

- App instalable en Windows, local, **sin IA**, para SQL Server.
- Una sola función: como Redgate Data Compare, pasar **tablas específicas con sus datos** (no sólo
  estructura; para eso ya usan SQL Compare) sin restaurar la base completa, y **rápido**.
- Interfaz basada en capturas de SQL Compare: origen gris a la izquierda, destino azul a la derecha.
- Conexión: primero servidor (IP), usuario y contraseña de SQL; después se elige la base.
- Resultados: buscar tablas (a mano o con cuadro de búsqueda), ver nombre y cantidad de filas, y al
  hacer clic ver la estructura en ambos lados.
- Servidores reales de referencia (de las capturas): origen `172.20.1.151`, destino `172.25.50.4`,
  base `B_Aplicativos`.

## Estado actual

| Función | Estado | Cómo se verificó |
| --- | --- | --- |
| Conexión en dos pasos (servidor + usuario/contraseña SQL → Conectar → base) | Hecho | Ejecutada con Wine contra SQL Server 2022 |
| Lista de tablas emparejadas, con filas de cada lado y buscador | Hecho | Ejecutada con Wine |
| Clic en tabla: estructura origen/destino lado a lado con diferencias | Hecho | Ejecutada con Wine |
| Comparar datos (+insertar ~actualizar −eliminar) y ver filas distintas | Hecho | Pruebas automáticas + Wine |
| Desplegar: Sincronizar / Reemplazar / Recrear, filtro WHERE por tabla | Hecho | Pruebas automáticas + Wine |
| Instalador Windows (Inno Setup) y Release en GitHub | Hecho | Workflow de GitHub en verde; instalado con Wine |
| Prueba en Windows real contra servidores del usuario | Pendiente | El usuario está instalando SQL Server + AdventureWorks |

Rendimiento medido en un contenedor de prueba (aproximado): copia de 1.000.000 de filas en ~8 s entre
servidores distintos; sincronización de 4.000 cambios sobre esa tabla en ~3 s.

## Dónde está cada cosa

| Qué | Dónde |
| --- | --- |
| Repositorio (público) | https://github.com/nmanassero/sqlapp |
| Rama de trabajo | `claude/sql-table-comparator-brr4qb` (sin Pull Request; no está unida a la principal) |
| Instalador | https://github.com/nmanassero/sqlapp/releases/latest (`SqlTableSync-Setup-1.1.0.exe`) |
| CI | https://github.com/nmanassero/sqlapp/actions |
| Instrucciones en Drive | Carpeta "SqlTableSync" en la cuenta corchetedev@gmail.com (sólo links e instrucciones) |

## Arquitectura

| Archivo | Responsabilidad |
| --- | --- |
| `src/SqlTableSync.Core/TableTransfer.cs` | Motor: plan de columnas y clave, comparar, aplicar (Sincronizar/Reemplazar/Recrear), diferencias |
| `src/SqlTableSync.Core/MetadataReader.cs` | Lee tablas, columnas, índices, CHECK, triggers y FKs desde `sys.*` |
| `src/SqlTableSync.Core/TableScripter.cs` | Genera `CREATE TABLE`, índices y CHECK |
| `src/SqlTableSync.Core/SqlConnectionInfo.cs` | Cadena de conexión y lista de bases |
| `src/SqlTableSync.App/MainForm.cs` | Ventana principal: conexiones y resultados |
| `src/SqlTableSync.App/ConnectionPanel.cs` | Conexión en dos pasos |
| `src/SqlTableSync.App/ResultsPage.cs` | Grilla agrupada, buscador, comparar y desplegar |
| `src/SqlTableSync.App/StructurePanel.cs` | Estructura origen/destino lado a lado |
| `src/SqlTableSync.App/DeployForm.cs`, `DiffForm.cs` | Diálogo de despliegue y vista de filas distintas |
| `src/SqlTableSync.App/AppSettings.cs` | Configuración en `%APPDATA%\SqlTableSync\settings.json` (contraseña con DPAPI si se pide) |
| `tests/SqlTableSync.Tests/TransferTests.cs` | 4 pruebas de integración contra SQL Server real |
| `installer/SqlTableSync.iss`, `build.ps1`, `.github/workflows/build.yml` | Instalador, compilación y CI |

Sincronización de una tabla:

1. Lee la estructura en ambos lados; columnas en común y clave (PK o índice único NOT NULL).
2. `SqlBulkCopy` de origen a `#sts_stage` en el servidor destino, indexada por la clave (si es la misma
   instancia, lee origen directo con nombres de 3 partes).
3. Compara en el servidor destino por conjuntos (`JOIN` por clave + `EXCEPT`, textos en collation binaria).
4. En una transacción: `DELETE` sobrantes, `UPDATE` distintas, `INSERT` nuevas.
5. Si algo falla, esa tabla queda como estaba.

## Compilar, probar y publicar

- Requisitos: .NET 8 SDK; Inno Setup 6 o 7 para el instalador.
- Instalador: `./build.ps1 -Version 1.1.0` → `dist\`.
- Pruebas: variables `SQLSYNC_TEST_SERVER`, `SQLSYNC_TEST_USER`, `SQLSYNC_TEST_PASSWORD` y
  `dotnet test tests/SqlTableSync.Tests` (crean las bases `SqlSyncTestSrc` y `SqlSyncTestTgt`).
- CI: cada push a la rama de trabajo corre las pruebas (SQL Server 2022 en contenedor), genera el
  instalador en Windows y publica el Release `v<versión>`.
- Nueva versión: cambiar `<Version>` en `src/SqlTableSync.App/SqlTableSync.App.csproj` y `$v` en el
  workflow.

## Decisiones tomadas

| Decisión | Motivo |
| --- | --- |
| C#/.NET 8 + WinForms, instalador autocontenido | Nativo de Windows; no requiere .NET ni permisos de administrador |
| Comparación en el servidor, no fila por fila en el cliente | Velocidad (el problema de Redgate Data Compare) |
| Lectura directa si es la misma instancia | Evita transferir datos por la red; se valida por nombre de servidor + id, fecha y GUID de la base |
| Comparación exacta (collation binaria) | Detecta cambios de mayúsculas y acentos |
| Una transacción por tabla | Si falla, esa tabla queda intacta |
| Triggers deshabilitados y FKs/CHECK verificadas por defecto | Sin efectos colaterales ni constraints no confiables |
| Sólo autenticación SQL en la interfaz | Pedido del usuario (el motor soporta Windows) |
| Recrear no copia FKs | Las tablas referenciadas pueden no existir en destino |

## Limitaciones conocidas

- Varias tablas con FK entre sí se procesan en el orden de la lista, no por dependencias.
- Sin PK ni índice único NOT NULL: sólo Reemplazar o Recrear.
- Tablas temporales (system-versioned) y memory-optimized no se pueden recrear.
- Textos dentro de `sql_variant` toman la collation de destino entre servidores distintos.
- Sincronizar usa `tempdb` del servidor destino.
- Cantidad de filas de la lista desde `sys.partitions`, no `COUNT(*)`.
- Instalador sin firma digital (aviso de SmartScreen).

## Próximos pasos

- [ ] Probar en Windows real con AdventureWorks (origen + copia como destino).
- [ ] Probar contra los servidores reales, empezando por un destino de prueba y una tabla chica.
- [ ] Decidir si unir la rama a la principal y si el repositorio queda público o privado.
- [ ] Verificar que un push con la misma versión actualiza los archivos del Release.
- [ ] Opcional: ordenar tablas por dependencias de FK; firma digital del instalador.
