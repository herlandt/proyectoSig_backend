using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using NetTopologySuite.IO;
using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Migracion;

namespace VisorDatosSIG.Infrastructure.Datos;

/// <summary>Persistencia de la migración: bitácora (conexión propia) y carga transaccional por lotes.</summary>
public sealed class MigracionRepositorio(FabricaConexion fabrica) : IMigracionRepositorio
{
    public async Task<string?> ProbarConexionAsync(CancellationToken ct)
    {
        try
        {
            await using var cn = fabrica.Crear();
            await cn.OpenAsync(ct);
            var faltantes = await cn.QueryAsync<string>(new CommandDefinition("""
                SELECT t.Nombre FROM (VALUES (N'Manzanas'),(N'Lotes'),(N'CodigosFijos'),(N'Vias'),(N'Usuarios'),(N'BitacoraMigracion')) t(Nombre)
                 WHERE OBJECT_ID(N'dbo.' + t.Nombre, N'U') IS NULL
                """, cancellationToken: ct));
            var lista = faltantes.ToList();
            return lista.Count == 0 ? null
                : $"Conexión correcta, pero faltan tablas del diseño ({string.Join(", ", lista)}). Ejecute los scripts 01–03.";
        }
        catch (SqlException ex) { return $"No se pudo conectar a SQL Server: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Cadena de conexión inválida: {ex.Message}"; }
    }

    public async Task<int> MarcarInterrumpidasAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.BitacoraMigracion
               SET Estado = N'Interrumpida', FechaFin = SYSDATETIME(),
                   Mensaje = N'Registro sin cierre durante más de dos horas; marcado al iniciar otra migración.'
             WHERE Estado = N'EnCurso' AND FechaInicio < DATEADD(HOUR, -2, SYSDATETIME())
            """, cancellationToken: ct));
    }

    public async Task<int> IniciarBitacoraAsync(EntradaBitacoraMigracion e, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT dbo.BitacoraMigracion(Usuario, Equipo, Capa, Archivo, TablaDestino, Modo, Estado)
            OUTPUT INSERTED.IdMigracion
            VALUES(@Usuario, @Equipo, @Capa, @Archivo, @TablaDestino, @Modo, N'EnCurso')
            """, new
        {
            Usuario = Truncar(e.Usuario, 50), Equipo = Truncar(e.Equipo, 100), e.Capa,
            Archivo = Truncar(e.Archivo, 260), e.TablaDestino, Modo = e.Modo.ToString(),
        }, cancellationToken: ct));
    }

    public async Task FinalizarBitacoraAsync(int idMigracion, string estado, ConteosCarga c, int duracionMs, string? mensaje, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.BitacoraMigracion
               SET FechaFin = SYSDATETIME(), Estado = @estado, RegistrosLeidos = @Leidos, Insertados = @Insertados,
                   Omitidos = @Omitidos, Fallidos = @Fallidos, Reparados = @Reparados, Advertencias = @Advertencias,
                   DuracionMs = @duracionMs, Mensaje = @mensaje
             WHERE IdMigracion = @idMigracion
            """, new
        {
            idMigracion, estado, c.Leidos, c.Insertados, c.Omitidos, c.Fallidos, c.Reparados, c.Advertencias,
            duracionMs, mensaje = Truncar(mensaje, 1000),
        }, cancellationToken: ct));
    }

    public async Task RegistrarIncidenciasAsync(int idMigracion, IEnumerable<Incidencia> incidencias, CancellationToken ct)
    {
        var tabla = new DataTable();
        tabla.Columns.Add("IdMigracion", typeof(int));
        tabla.Columns.Add("Nivel", typeof(string));
        tabla.Columns.Add("Fila", typeof(int));
        tabla.Columns.Add("Campo", typeof(string));
        tabla.Columns.Add("Valor", typeof(string));
        tabla.Columns.Add("Mensaje", typeof(string));
        foreach (var i in incidencias.Take(5000))
            tabla.Rows.Add(idMigracion, i.Nivel.ToString(), (object?)i.Fila ?? DBNull.Value,
                (object?)Truncar(i.Campo, 50) ?? DBNull.Value, (object?)Truncar(i.Valor, 200) ?? DBNull.Value,
                Truncar($"[{i.Codigo}] {i.Mensaje}", 500));
        if (tabla.Rows.Count == 0) return;

        await using var cn = fabrica.Crear();
        await cn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.FireTriggers, null)
        {
            DestinationTableName = "dbo.BitacoraMigracionDetalle",
        };
        foreach (DataColumn col in tabla.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(tabla, ct);
    }

    public async Task<IMigracionTransaccion> IniciarTransaccionAsync(CancellationToken ct)
    {
        var cn = fabrica.Crear();
        try
        {
            await cn.OpenAsync(ct);
            var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            return new MigracionTransaccionSql(cn, tx);
        }
        catch
        {
            await cn.DisposeAsync();
            throw;
        }
    }

    public async Task<string> ReconstruirIndicesEspacialesAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteScalarAsync<string>(new CommandDefinition("dbo.sp_ReconstruirIndicesEspaciales",
            commandType: CommandType.StoredProcedure, commandTimeout: 600, cancellationToken: ct)) ?? "";
    }

    public async Task<Pagina<MigracionHistorial>> ListarHistorialAsync(int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 5, 100);
        await using var cn = fabrica.Crear();
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM dbo.BitacoraMigracion;
            SELECT {ColumnasHistorial} FROM dbo.BitacoraMigracion
             ORDER BY IdMigracion DESC OFFSET @off ROWS FETCH NEXT @tam ROWS ONLY;
            """, new { off = (pagina - 1) * tamano, tam = tamano }, cancellationToken: ct));
        int total = await multi.ReadSingleAsync<int>();
        return new Pagina<MigracionHistorial>(total, pagina, tamano, (await multi.ReadAsync<MigracionHistorial>()).ToList());
    }

    public async Task<(MigracionHistorial? Cabecera, IReadOnlyList<DetalleMigracion> Detalle)> ObtenerAsync(int idMigracion, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition($"""
            SELECT {ColumnasHistorial} FROM dbo.BitacoraMigracion WHERE IdMigracion = @id;
            SELECT TOP (500) IdDetalle, Nivel, Fila, Campo, Valor, Mensaje FROM dbo.BitacoraMigracionDetalle
             WHERE IdMigracion = @id ORDER BY IdDetalle;
            """, new { id = idMigracion }, cancellationToken: ct));
        var cabecera = await multi.ReadSingleOrDefaultAsync<MigracionHistorial>();
        var detalle = (await multi.ReadAsync<DetalleMigracion>()).ToList();
        return (cabecera, detalle);
    }

    public async Task<DateTime?> UltimaActualizacionAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(FechaFin) FROM dbo.BitacoraMigracion WHERE Estado = N'Completada'", cancellationToken: ct));
    }

    private const string ColumnasHistorial = """
        IdMigracion, FechaInicio, FechaFin, Usuario, Equipo, Capa, Archivo, TablaDestino, Modo, Estado,
        RegistrosLeidos, Insertados, Omitidos, Fallidos, Reparados, Advertencias, DuracionMs, Mensaje
        """;

    private static string? Truncar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>
/// Una única transacción para todas las capas de una ejecución. Cada lote se copia con
/// SqlBulkCopy a una tabla temporal (WKB 2D en VARBINARY) y se inserta en la tabla
/// oficial construyendo la geometría con geometry::STGeomFromWKB(@wkb, 4326).
/// </summary>
internal sealed class MigracionTransaccionSql(SqlConnection cn, SqlTransaction tx) : IMigracionTransaccion
{
    private const int TiempoLargo = 900;
    private static readonly WKBWriter Wkb = new(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
    private bool _terminada;

    public async Task PrepararReemplazoAsync(IReadOnlyCollection<string> capas, CancellationToken ct)
    {
        bool R(string id) => capas.Contains(id, StringComparer.OrdinalIgnoreCase);
        var sql = new List<string>();
        // Eliminar indices espaciales temporalmente para permitir insercion ultra rapida sin bloqueo de memoria
        if (R(CatalogoCapas.CodigosFijos)) { sql.Add("DROP INDEX IF EXISTS SIX_CodigosFijos_Geom ON dbo.CodigosFijos;"); sql.Add("DELETE FROM dbo.CodigosFijos;"); }
        if (R(CatalogoCapas.Lotes)) { sql.Add("DROP INDEX IF EXISTS SIX_Lotes_Geom ON dbo.Lotes;"); sql.Add("UPDATE dbo.CodigosFijos SET IdLote = NULL WHERE IdLote IS NOT NULL; DELETE FROM dbo.Lotes;"); }
        if (R(CatalogoCapas.Manzanas)) { sql.Add("DROP INDEX IF EXISTS SIX_Manzanas_Geom ON dbo.Manzanas;"); sql.Add("UPDATE dbo.Lotes SET IdManzana = NULL WHERE IdManzana IS NOT NULL; DELETE FROM dbo.Manzanas;"); }
        if (R(CatalogoCapas.Vias)) { sql.Add("DROP INDEX IF EXISTS SIX_Vias_Geom ON dbo.Vias;"); sql.Add("DELETE FROM dbo.Vias;"); }
        if (sql.Count > 0)
            await cn.ExecuteAsync(new CommandDefinition(string.Join("\n", sql), transaction: tx, commandTimeout: TiempoLargo, cancellationToken: ct));
    }

    public async Task<HashSet<string>> ObtenerClavesExistentesAsync(string capa, CancellationToken ct)
    {
        var def = DefinicionesMigracion.Buscar(capa) ?? throw new ErrorValidacion($"Capa desconocida: {capa}");
        var columnas = DefinicionesMigracion.ColumnasClave(def);
        var filas = await cn.QueryAsync(new CommandDefinition(
            $"SELECT {string.Join(", ", columnas)} FROM {def.Tabla}", transaction: tx, commandTimeout: TiempoLargo, cancellationToken: ct));
        var claves = new HashSet<string>(StringComparer.Ordinal);
        foreach (IDictionary<string, object> f in filas)
        {
            var valores = columnas.ToDictionary(c => c, c => f[c] is DBNull ? null : f[c], StringComparer.OrdinalIgnoreCase);
            if (valores.Values.Any(v => v is null) && def.ClavePersonalizada is not null) continue;
            claves.Add(DefinicionesMigracion.ClaveNatural(def, valores));
        }
        return claves;
    }

    public async Task InsertarAsync(DefinicionCapaMigracion def, IReadOnlyList<FilaDestino> filas, CancellationToken ct)
    {
        if (filas.Count == 0) return;
        var tabla = new DataTable();
        foreach (var m in def.Mapeo)
            tabla.Columns.Add(m.ColumnaDestino, m.Tipo switch
            {
                TipoDestino.Entero => typeof(int),
                TipoDestino.TinyInt => typeof(byte),
                TipoDestino.Flotante => typeof(double),
                _ => typeof(string),
            });
        tabla.Columns.Add("__Wkb", typeof(byte[]));
        foreach (var f in filas)
        {
            var fila = tabla.NewRow();
            foreach (var m in def.Mapeo) fila[m.ColumnaDestino] = f.Valores[m.ColumnaDestino] ?? DBNull.Value;
            fila["__Wkb"] = Wkb.Write(f.Geometria);
            tabla.Rows.Add(fila);
        }

        string definicionTemporal = string.Join(", ", def.Mapeo.Select(m => $"[{m.ColumnaDestino}] {m.DescripcionTipo} NULL"))
                                    + ", [__Wkb] VARBINARY(MAX) NOT NULL";
        await cn.ExecuteAsync(new CommandDefinition(
            $"IF OBJECT_ID('tempdb..#carga') IS NOT NULL DROP TABLE #carga; CREATE TABLE #carga ({definicionTemporal});",
            transaction: tx, cancellationToken: ct));

        using (var bulk = new SqlBulkCopy(cn, SqlBulkCopyOptions.Default, tx)
               { DestinationTableName = "#carga", BatchSize = filas.Count, BulkCopyTimeout = TiempoLargo })
        {
            foreach (DataColumn col in tabla.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(tabla, ct);
        }

        string columnas = string.Join(", ", def.Mapeo.Select(m => $"[{m.ColumnaDestino}]"));
        await cn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO {def.Tabla} ({columnas}, Geom)
            SELECT {columnas}, geometry::STGeomFromWKB([__Wkb], 4326) FROM #carga;
            DROP TABLE #carga;
            """, transaction: tx, commandTimeout: TiempoLargo, cancellationToken: ct));
    }

    public async Task<int> RepararGeometriasInvalidasAsync(CancellationToken ct) =>
        await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            DECLARE @n INT = 0;
            UPDATE dbo.Manzanas SET Geom = Geom.MakeValid() WHERE Geom IS NOT NULL AND Geom.STIsValid() = 0; SET @n += @@ROWCOUNT;
            UPDATE dbo.Lotes SET Geom = Geom.MakeValid() WHERE Geom IS NOT NULL AND Geom.STIsValid() = 0; SET @n += @@ROWCOUNT;
            UPDATE dbo.CodigosFijos SET Geom = Geom.MakeValid() WHERE Geom IS NOT NULL AND Geom.STIsValid() = 0; SET @n += @@ROWCOUNT;
            UPDATE dbo.Vias SET Geom = Geom.MakeValid() WHERE Geom IS NOT NULL AND Geom.STIsValid() = 0; SET @n += @@ROWCOUNT;
            SELECT @n;
            """, transaction: tx, commandTimeout: TiempoLargo, cancellationToken: ct));

    public async Task<ResultadoVinculos> ActualizarVinculosAsync(CancellationToken ct)
    {
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition("dbo.sp_ActualizarVinculosEspaciales",
            transaction: tx, commandType: CommandType.StoredProcedure, commandTimeout: TiempoLargo, cancellationToken: ct));
        var codigos = await multi.ReadSingleAsync();
        var lotes = await multi.ReadSingleAsync();
        return new ResultadoVinculos(
            (int)codigos.TotalCodigos, (int)codigos.AsociadosALote,
            (int)lotes.TotalLotes, (int)lotes.AsociadosAManzana);
    }

    public async Task<IReadOnlyDictionary<string, int>> ContarAsync(CancellationToken ct)
    {
        var filas = await cn.QueryAsync<(string Capa, int Total)>(new CommandDefinition("""
            SELECT 'manzanas', COUNT(*) FROM dbo.Manzanas UNION ALL SELECT 'lotes', COUNT(*) FROM dbo.Lotes
            UNION ALL SELECT 'codigosfijos', COUNT(*) FROM dbo.CodigosFijos UNION ALL SELECT 'vias', COUNT(*) FROM dbo.Vias
            """, transaction: tx, cancellationToken: ct));
        return filas.ToDictionary(f => f.Capa, f => f.Total);
    }

    public async Task ConfirmarAsync(CancellationToken ct)
    {
        await tx.CommitAsync(ct);
        _terminada = true;
    }

    public async Task RevertirAsync()
    {
        if (_terminada) return;
        try { await tx.RollbackAsync(); }
        catch (InvalidOperationException) { /* la conexión ya revirtió (p. ej. se perdió) */ }
        catch (SqlException) { /* idem */ }
        _terminada = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_terminada) await RevertirAsync();
        await tx.DisposeAsync();
        await cn.DisposeAsync();
    }
}
