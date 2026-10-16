# SqlTableSync

Aplicación de escritorio para Windows que copia y sincroniza **los datos** de tablas entre dos
bases SQL Server, sin necesidad de restaurar la base completa. La idea es similar a Redgate SQL Data
Compare, pero centrada en una sola tarea: pasar tablas concretas (estructura + datos) de origen a
destino, rápido.

- 100 % local: no usa IA ni servicios externos. Sólo se conecta a los SQL Server que usted indique.
- No modifica el origen: sólo lee.
- Cada tabla se despliega dentro de una transacción: si algo falla, esa tabla queda como estaba.

## Instalación

Descargue `SqlTableSync-Setup-x.y.z.exe` (artefacto *SqlTableSync-windows* del workflow **build**
en GitHub Actions, o el *release* si se publicó un tag `v*`) y ejecútelo. No requiere permisos de
administrador ni tener .NET instalado (la app incluye su runtime).

También se genera un `.zip` portable: se descomprime y se ejecuta `SqlTableSync.exe`.

## Uso

1. **Conexiones**: complete Origen (izquierda) y Destino (derecha): servidor, autenticación de
   Windows o de SQL Server, *Encrypt* / *Trust server certificate* y la base de datos (el botón de
   actualizar lista las bases). Pulse **Comparar ahora**.
2. **Tablas**: se listan emparejadas por esquema y nombre, agrupadas en: *sólo en origen*
   (se crearán), *en ambas bases*, *sólo en destino*, y tras comparar: *con diferencias*,
   *idénticas* o *con error*. Marque las tablas que quiere pasar.
   - **Filtro WHERE** (opcional, por tabla): p. ej. `Fecha >= '2024-01-01'` para pasar sólo ese
     subconjunto. Se aplica en origen y a las filas de destino que se reemplazan o eliminan; las
     filas de destino fuera del filtro no se tocan.
3. **Comparar datos** (opcional): cuenta cuántas filas se insertarán, actualizarán y eliminarán.
   Doble clic en una tabla (o **Ver diferencias**) muestra hasta 1.000 filas de cada tipo, con los
   valores distintos resaltados.
4. **Desplegar...**: elija el modo y confirme. El destino se muestra en grande para evitar errores.

### Modos

| Modo | Qué hace | Requiere |
|---|---|---|
| **Sincronizar** | `DELETE` / `UPDATE` / `INSERT` sólo de las filas distintas. Si ya comparó, reutiliza los datos ya transferidos. | PK o índice único NOT NULL |
| **Reemplazar datos** | Vacía la tabla (`TRUNCATE`, o `DELETE` si hay filtro o FKs que la referencian) y carga todo. Mantiene la estructura de destino. | — |
| **Recrear tabla** | `DROP` + `CREATE` con la estructura de origen (columnas, identity, defaults, PK, índices, CHECK; **no** FKs) y carga. | Que ninguna FK de otra tabla la referencie |

Si la tabla no existe en destino, en cualquier modo se crea con la estructura de origen.

### Opciones

- **Eliminar filas sobrantes** (Sincronizar): borra en destino lo que no existe en origen.
- **Ejecutar triggers de destino**: por defecto los triggers se deshabilitan durante la carga y se
  vuelven a habilitar al terminar (dentro de la misma transacción).
- **Verificar FKs y CHECK**: si se desactiva, la carga es más rápida pero las constraints quedan
  *no confiables* (`is_not_trusted`); la app lo avisa.
- **TABLOCK**: bloqueo de tabla en la carga masiva (más rápido; bloquea a otros usuarios mientras dura).
- **Misma instancia**: si origen y destino están en el mismo SQL Server, los datos se copian con
  `INSERT ... SELECT` entre bases, sin pasar por la red.

## Por qué es rápido

- Los datos de origen se envían con `SqlBulkCopy` (protocolo de carga masiva, en streaming) a una
  tabla temporal en el servidor de destino.
- La comparación se hace **en el servidor de destino**, por conjuntos (`JOIN` por clave + `EXCEPT`),
  no fila por fila en el cliente. La tabla temporal se indexa por la clave antes de comparar.
- Si ambas bases están en la misma instancia, no se transfiere nada: se leen con nombres de 3 partes.

Mediciones en un SQL Server 2022 de prueba (contenedor Docker, misma máquina; en su red los
tiempos dependerán del ancho de banda y del servidor):

| Operación (1.000.000 filas, 5 columnas) | Servidores distintos | Misma instancia |
|---|---|---|
| Copia completa a tabla nueva | ~8 s | ~1,6 s |
| Sincronizar (comparar + aplicar 4.000 cambios) | ~2,7 s | ~2,9 s |

## Detalles y limitaciones conocidas

- Comparación exacta: los textos se comparan en collation binaria (distingue mayúsculas y acentos;
  como en SQL Server, los espacios finales no cuentan). `xml`, `text`, `ntext`, `image` y los tipos
  CLR (`geography`, `geometry`, `hierarchyid`) se convierten a tipos comparables.
- Se omiten columnas calculadas, `rowversion`/`timestamp` y columnas de período de tablas temporales.
- Si la estructura difiere, se copian las columnas con el mismo nombre y se avisa de las demás.
- Los tipos alias se crean con su tipo base al recrear una tabla.
- Los textos dentro de `sql_variant` toman la collation de la base de destino al pasar por
  `SqlBulkCopy` (en la misma instancia se conservan).
- En Sincronizar, los datos de origen se guardan en `tempdb` del servidor de destino mientras dura
  la operación.
- Varias tablas relacionadas por FK se procesan en el orden de la lista (no se ordenan por dependencias).
- Las tablas temporales (system-versioned) y memory-optimized no se pueden recrear: use Reemplazar
  o Sincronizar sobre una estructura existente.

La configuración (servidores recientes, opciones) se guarda en `%APPDATA%\SqlTableSync\settings.json`.
La contraseña sólo se guarda si marca *Recordar credenciales*, cifrada con DPAPI para su usuario de Windows.

## Compilar

Requisitos: .NET 8 SDK y, para el instalador, [Inno Setup](https://jrsoftware.org/isinfo.php).

```powershell
./build.ps1 -Version 1.0.0     # genera dist\SqlTableSync-Setup-1.0.0.exe y el zip portable
```

Pruebas de integración (necesitan un SQL Server; crean las bases `SqlSyncTestSrc` y `SqlSyncTestTgt`):

```powershell
$env:SQLSYNC_TEST_SERVER = "localhost"
$env:SQLSYNC_TEST_USER = "sa"            # omitir para autenticación de Windows
$env:SQLSYNC_TEST_PASSWORD = "..."
dotnet test tests/SqlTableSync.Tests
```

Estructura:

- `src/SqlTableSync.Core`: lectura de metadatos, generación de DDL y motor de copia/comparación.
- `src/SqlTableSync.App`: interfaz WinForms.
- `tests/SqlTableSync.Tests`: pruebas de integración contra SQL Server real.
- `installer/SqlTableSync.iss`: script de Inno Setup.
