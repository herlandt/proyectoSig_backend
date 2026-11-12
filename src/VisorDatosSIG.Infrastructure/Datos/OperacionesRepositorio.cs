using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Infrastructure.Datos;

/// <summary>Asignaciones, ejecución en campo y monitoreo sobre las tablas auxiliares de 07_Operaciones.sql (SQL parametrizado).</summary>
public sealed class OperacionesRepositorio(FabricaConexion fabrica) : IOperacionesRepositorio
{
    private const int TiempoComando = 60;
    private const string Activas = "N'Pendiente', N'EnCurso'";
    private static readonly WKBWriter Wkb = new(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false);
    private static readonly GeometryFactory Fabrica = new(new PrecisionModel(), 4326);

    // ---------------------------------------------------------------- configuración

    public async Task<ConfiguracionOperativa> ObtenerConfiguracionAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaConfiguracion>(new CommandDefinition("""
            SELECT TOP (1) VelocidadKmH, MinutosPorParada, IntervaloGpsSegundos, RadioValidacionMetros,
                   CAST(MaximoFotos AS INT) AS MaximoFotos, MaximoParadas
              FROM dbo.ConfiguracionOperativa ORDER BY IdConfiguracion
            """, cancellationToken: ct, commandTimeout: TiempoComando));
        return fila is null
            ? ConfiguracionOperativa.PorDefecto
            : new(fila.VelocidadKmH, fila.MinutosPorParada, fila.IntervaloGpsSegundos, fila.RadioValidacionMetros, fila.MaximoFotos, fila.MaximoParadas);
    }

    public async Task GuardarConfiguracionAsync(ConfiguracionOperativa c, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM dbo.ConfiguracionOperativa) INSERT dbo.ConfiguracionOperativa(IdConfiguracion) VALUES (1);
            UPDATE dbo.ConfiguracionOperativa
               SET VelocidadKmH = @VelocidadKmH, MinutosPorParada = @MinutosPorParada, IntervaloGpsSegundos = @IntervaloGpsSegundos,
                   RadioValidacionMetros = @RadioValidacionMetros, MaximoFotos = @MaximoFotos, MaximoParadas = @MaximoParadas,
                   FechaModificacion = SYSDATETIME()
             WHERE IdConfiguracion = 1;
            """, c, cancellationToken: ct, commandTimeout: TiempoComando));
    }

    // ---------------------------------------------------------------- asignaciones

    public async Task<IReadOnlyList<Trabajador>> ListarTrabajadoresAsync(TipoAsignacion tipo, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaTrabajador>(new CommandDefinition($"""
            SELECT u.IdUsuario, u.Login, u.Nombre,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle d JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
                     WHERE a.IdTrabajador = u.IdUsuario AND a.Tipo = @tipo AND a.Estado IN ({Activas})
                       AND d.Estado = N'Pendiente') AS Pendientes
              FROM dbo.Usuarios u
             WHERE u.Activo = 1
               AND EXISTS (SELECT 1 FROM dbo.UsuariosRoles ur JOIN dbo.Roles r ON r.IdRol = ur.IdRol
                            WHERE ur.IdUsuario = u.IdUsuario AND r.NombreRol = @rol)
             ORDER BY u.Nombre
            """, new { tipo = tipo.Codigo(), rol = new DbString { Value = tipo.Rol(), IsAnsi = true, Length = 50 } },
            cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => new Trabajador(f.IdUsuario, f.Login, f.Nombre, f.Pendientes))];
    }

    public async Task<bool> EsTrabajadorAsync(int idUsuario, TipoAsignacion tipo, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.Usuarios u
             WHERE u.IdUsuario = @id AND u.Activo = 1
               AND EXISTS (SELECT 1 FROM dbo.UsuariosRoles ur JOIN dbo.Roles r ON r.IdRol = ur.IdRol
                            WHERE ur.IdUsuario = u.IdUsuario AND r.NombreRol = @rol)
            """, new { id = idUsuario, rol = new DbString { Value = tipo.Rol(), IsAnsi = true, Length = 50 } },
            cancellationToken: ct, commandTimeout: TiempoComando)) > 0;
    }

    public async Task<IReadOnlyList<CodigoOperativo>> ListarCandidatosAsync(TipoAsignacion tipo, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaCodigo>(new CommandDefinition("""
            SELECT c.IdCodigo, c.CodFijo, c.Nombre, c.Estado AS EstadoCodigo, c.Geom.STX AS Lon, c.Geom.STY AS Lat
              FROM dbo.CodigosFijos c
             WHERE c.Estado = @estado AND c.Geom IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM dbo.AsignacionDetalle d WHERE d.IdCodigo = c.IdCodigo AND d.Estado = N'Pendiente')
            """, new { estado = (byte)tipo.EstadoCandidato() }, cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => f.Codigo())];
    }

    public Task<AsignacionActiva?> ObtenerActivaAsync(int idTrabajador, TipoAsignacion tipo, CancellationToken ct) =>
        CargarAsignacionAsync($"""
            SELECT TOP (1) {ColumnasCabecera} FROM dbo.Asignacion a
             WHERE a.IdTrabajador = @t AND a.Tipo = @tipo AND a.Estado IN ({Activas})
             ORDER BY a.IdAsignacion DESC
            """, new { t = idTrabajador, tipo = tipo.Codigo() }, ct);

    public Task<AsignacionActiva?> ObtenerAsignacionAsync(int idAsignacion, CancellationToken ct) =>
        CargarAsignacionAsync($"SELECT {ColumnasCabecera} FROM dbo.Asignacion a WHERE a.IdAsignacion = @id",
            new { id = idAsignacion }, ct);

    private const string ColumnasCabecera = """
        a.IdAsignacion, a.Tipo, a.IdTrabajador, a.Estado, a.FechaAsignacion,
        a.PartidaGeom.STX AS PartidaLon, a.PartidaGeom.STY AS PartidaLat, a.DistanciaMetros, a.DuracionEstimadaMin
        """;

    private async Task<AsignacionActiva?> CargarAsignacionAsync(string sqlCabecera, object parametros, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var c = await cn.QuerySingleOrDefaultAsync<FilaCabecera>(new CommandDefinition(sqlCabecera, parametros, cancellationToken: ct, commandTimeout: TiempoComando));
        if (c is null) return null;
        var detalle = await cn.QueryAsync<FilaParada>(new CommandDefinition("""
            SELECT d.IdDetalle, d.Orden, d.Estado, d.DistanciaTramoMetros, d.DistanciaAccesoMetros, d.TramoGeom.STAsBinary() AS Tramo,
                   c.IdCodigo, c.CodFijo, c.Nombre, c.Estado AS EstadoCodigo, c.Geom.STX AS Lon, c.Geom.STY AS Lat
              FROM dbo.AsignacionDetalle d JOIN dbo.CodigosFijos c ON c.IdCodigo = d.IdCodigo
             WHERE d.IdAsignacion = @id AND d.Estado <> N'Anulada'
             ORDER BY d.Orden
            """, new { id = c.IdAsignacion }, cancellationToken: ct, commandTimeout: TiempoComando));
        return new AsignacionActiva(
            c.IdAsignacion, TipoDesde(c.Tipo), c.IdTrabajador, c.Estado, c.FechaAsignacion,
            new Coordenada(c.PartidaLon, c.PartidaLat), c.DistanciaMetros, c.DuracionEstimadaMin,
            [.. detalle.Select(d => new ParadaAsignada(d.IdDetalle, d.Orden, d.Estado, d.Codigo(),
                d.DistanciaTramoMetros, d.DistanciaAccesoMetros, Coordenadas(d.Tramo)))]);
    }

    public async Task<int> GuardarPlanAsync(PlanAGuardar plan, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var cabecera = new
            {
                tipo = plan.Tipo.Codigo(), t = plan.IdTrabajador, por = plan.IdAsignadoPor,
                lon = plan.Partida.Lon, lat = plan.Partida.Lat, dist = plan.DistanciaMetros, dur = plan.DuracionMin,
                ruta = Linea(plan.Ruta),
            };
            // UPDLOCK + HOLDLOCK: dos administradores no pueden recalcular a la vez la ruta del mismo trabajador.
            int? id = await cn.ExecuteScalarAsync<int?>(new CommandDefinition($"""
                SELECT TOP (1) IdAsignacion FROM dbo.Asignacion WITH (UPDLOCK, HOLDLOCK)
                 WHERE IdTrabajador = @t AND Tipo = @tipo AND Estado IN ({Activas}) ORDER BY IdAsignacion DESC
                """, cabecera, tx, cancellationToken: ct));
            if (id is null)
                id = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    INSERT dbo.Asignacion(Tipo, IdTrabajador, IdAsignadoPor, PartidaGeom, DistanciaMetros, DuracionEstimadaMin, RutaGeom)
                    VALUES (@tipo, @t, @por, geometry::Point(@lon, @lat, 4326), @dist, @dur,
                            CASE WHEN @ruta IS NULL THEN NULL ELSE geometry::STGeomFromWKB(@ruta, 4326) END);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """, cabecera, tx, cancellationToken: ct));
            else
                await cn.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.Asignacion
                       SET PartidaGeom = geometry::Point(@lon, @lat, 4326), DistanciaMetros = @dist, DuracionEstimadaMin = @dur,
                           RutaGeom = CASE WHEN @ruta IS NULL THEN NULL ELSE geometry::STGeomFromWKB(@ruta, 4326) END,
                           FechaActualizacion = SYSDATETIME()
                     WHERE IdAsignacion = @id
                    """, new { cabecera.lon, cabecera.lat, cabecera.dist, cabecera.dur, cabecera.ruta, id }, tx, cancellationToken: ct));

            var existentes = (await cn.QueryAsync<(int IdDetalle, int IdCodigo)>(new CommandDefinition(
                    "SELECT IdDetalle, IdCodigo FROM dbo.AsignacionDetalle WHERE IdAsignacion = @id AND Estado = N'Pendiente'",
                    new { id }, tx, cancellationToken: ct)))
                .ToDictionary(x => x.IdCodigo, x => x.IdDetalle);

            foreach (var p in plan.Paradas)
            {
                var datos = new { id, codigo = p.IdCodigo, orden = p.Orden, dt = p.DistanciaTramo, da = p.DistanciaAcceso, tramo = Linea(p.Tramo),
                                  estado = (byte)plan.Tipo.EstadoCandidato() };
                if (existentes.TryGetValue(p.IdCodigo, out int idDetalle))
                {
                    await cn.ExecuteAsync(new CommandDefinition("""
                        UPDATE dbo.AsignacionDetalle
                           SET Orden = @orden, DistanciaTramoMetros = @dt, DistanciaAccesoMetros = @da,
                               TramoGeom = CASE WHEN @tramo IS NULL THEN NULL ELSE geometry::STGeomFromWKB(@tramo, 4326) END
                         WHERE IdDetalle = @idDetalle
                        """, new { datos.orden, datos.dt, datos.da, datos.tramo, idDetalle }, tx, cancellationToken: ct));
                    continue;
                }
                int insertadas = await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT dbo.AsignacionDetalle(IdAsignacion, IdCodigo, Orden, EstadoCodigoAlAsignar, DistanciaTramoMetros, DistanciaAccesoMetros, TramoGeom)
                    SELECT @id, c.IdCodigo, @orden, c.Estado, @dt, @da,
                           CASE WHEN @tramo IS NULL THEN NULL ELSE geometry::STGeomFromWKB(@tramo, 4326) END
                      FROM dbo.CodigosFijos c
                     WHERE c.IdCodigo = @codigo AND c.Estado = @estado
                    """, datos, tx, cancellationToken: ct));
                if (insertadas == 0)
                    throw new ErrorValidacion($"El código {p.IdCodigo} cambió de estado mientras se calculaba la ruta. Vuelva a calcularla.", "ruta");
            }

            // Pendientes que no entraron al plan (casa lejos de toda vía): quedan al final, sin perderse.
            var enPlan = plan.Paradas.Select(p => p.IdCodigo).ToHashSet();
            int siguiente = plan.Paradas.Select(p => p.Orden).DefaultIfEmpty(0).Max();
            foreach (var (idCodigo, idDetalle) in existentes.Where(e => !enPlan.Contains(e.Key)))
                await cn.ExecuteAsync(new CommandDefinition("UPDATE dbo.AsignacionDetalle SET Orden = @orden WHERE IdDetalle = @idDetalle",
                    new { orden = ++siguiente, idDetalle }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return id.Value;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw new ErrorValidacion("Algún código ya fue asignado en otra ruta mientras se calculaba esta. Vuelva a calcularla.", "ruta");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> AnularAsync(int idAsignacion, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        int filas = await cn.ExecuteScalarAsync<int>(new CommandDefinition($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE dbo.Asignacion SET Estado = N'Anulada', FechaFin = SYSDATETIME(), FechaActualizacion = SYSDATETIME()
             WHERE IdAsignacion = @id AND Estado IN ({Activas});
            DECLARE @filas INT = @@ROWCOUNT;
            UPDATE dbo.AsignacionDetalle SET Estado = N'Anulada' WHERE IdAsignacion = @id AND Estado = N'Pendiente' AND @filas > 0;
            COMMIT;
            SELECT @filas;
            """, new { id = idAsignacion }, cancellationToken: ct, commandTimeout: TiempoComando));
        return filas > 0;
    }

    // ---------------------------------------------------------------- trabajo en campo (móvil)

    private const string ColumnasTarea = """
        d.IdDetalle, d.IdAsignacion, a.Tipo, a.Estado AS EstadoAsignacion, d.Orden, d.DistanciaTramoMetros, d.DistanciaAccesoMetros,
        c.IdCodigo, c.CodFijo, c.Nombre, c.Estado AS EstadoCodigo, c.Geom.STX AS Lon, c.Geom.STY AS Lat
        """;

    public async Task<IReadOnlyList<TareaCampo>> ListarTareasAsync(int idTrabajador, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaTarea>(new CommandDefinition($"""
            SELECT {ColumnasTarea}
              FROM dbo.AsignacionDetalle d
              JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
              JOIN dbo.CodigosFijos c ON c.IdCodigo = d.IdCodigo
             WHERE a.IdTrabajador = @t AND a.Estado IN ({Activas}) AND d.Estado = N'Pendiente'
             ORDER BY a.Tipo, d.Orden
            """, new { t = idTrabajador }, cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => f.Tarea())];
    }

    public async Task<TareaCampo?> ObtenerTareaAsync(int idDetalle, int idTrabajador, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaTarea>(new CommandDefinition($"""
            SELECT {ColumnasTarea}
              FROM dbo.AsignacionDetalle d
              JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
              JOIN dbo.CodigosFijos c ON c.IdCodigo = d.IdCodigo
             WHERE d.IdDetalle = @d AND a.IdTrabajador = @t AND a.Estado IN ({Activas}) AND d.Estado = N'Pendiente'
            """, new { d = idDetalle, t = idTrabajador }, cancellationToken: ct, commandTimeout: TiempoComando));
        return fila?.Tarea();
    }

    public async Task<bool> IniciarRecorridoAsync(int idAsignacion, int idTrabajador, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteAsync(new CommandDefinition($"""
            UPDATE dbo.Asignacion
               SET Estado = N'EnCurso', FechaInicio = COALESCE(FechaInicio, SYSDATETIME()), FechaActualizacion = SYSDATETIME()
             WHERE IdAsignacion = @id AND IdTrabajador = @t AND Estado IN ({Activas})
            """, new { id = idAsignacion, t = idTrabajador }, cancellationToken: ct, commandTimeout: TiempoComando)) > 0;
    }

    public async Task RegistrarEjecucionAsync(EjecucionARegistrar e, IReadOnlyList<FotoGuardada> fotos, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            int filas = await cn.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.AsignacionDetalle
                   SET Estado = @EstadoParada, Resultado = @Resultado, NumeroMedidor = @NumeroMedidor, Lectura = @Lectura,
                       Observacion = @Observacion, FechaEjecucion = SYSDATETIME(), FechaDispositivo = @FechaDispositivo,
                       UbicacionEjecucion = geometry::Point(@lon, @lat, 4326), PrecisionMetros = @PrecisionMetros,
                       DistanciaAlPuntoMetros = @DistanciaAlPuntoMetros, DentroDelRadio = @DentroDelRadio
                 WHERE IdDetalle = @IdDetalle AND Estado = N'Pendiente'
                """, new
                {
                    e.EstadoParada, e.Resultado, e.NumeroMedidor, e.Lectura, e.Observacion, e.FechaDispositivo,
                    lon = e.Ubicacion.Lon, lat = e.Ubicacion.Lat, e.PrecisionMetros, e.DistanciaAlPuntoMetros, e.DentroDelRadio, e.IdDetalle,
                }, tx, cancellationToken: ct));
            if (filas == 0) throw new ErrorValidacion("La tarea ya fue registrada.", "tarea");

            // El trigger TR_CodigosFijos_FechaCambioEstado actualiza FechaCambioEstado.
            if (e.NuevoEstadoCodigo is { } nuevo)
                await cn.ExecuteAsync(new CommandDefinition("UPDATE dbo.CodigosFijos SET Estado = @estado WHERE IdCodigo = @id",
                    new { estado = (byte)nuevo, id = e.IdCodigo }, tx, cancellationToken: ct));

            foreach (var f in fotos)
                await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT dbo.EvidenciaFoto(IdDetalle, NombreArchivo, TipoContenido, TamanoBytes, HashSha256)
                    VALUES (@IdDetalle, @NombreArchivo, @TipoContenido, @TamanoBytes, @HashSha256)
                    """, new { e.IdDetalle, f.NombreArchivo, f.TipoContenido, f.TamanoBytes, f.HashSha256 }, tx, cancellationToken: ct));

            await cn.ExecuteAsync(new CommandDefinition("""
                DECLARE @quedan INT = (SELECT COUNT(*) FROM dbo.AsignacionDetalle WHERE IdAsignacion = @id AND Estado = N'Pendiente');
                UPDATE dbo.Asignacion
                   SET Estado = CASE WHEN @quedan = 0 THEN N'Completada' ELSE N'EnCurso' END,
                       FechaInicio = COALESCE(FechaInicio, SYSDATETIME()),
                       FechaFin = CASE WHEN @quedan = 0 THEN SYSDATETIME() ELSE NULL END,
                       FechaActualizacion = SYSDATETIME()
                 WHERE IdAsignacion = @id
                """, new { id = e.IdAsignacion }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> AsignacionPerteneceAsync(int idAsignacion, int idTrabajador, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.Asignacion WHERE IdAsignacion = @id AND IdTrabajador = @t",
            new { id = idAsignacion, t = idTrabajador }, cancellationToken: ct, commandTimeout: TiempoComando)) > 0;
    }

    public async Task<int> RegistrarPosicionesAsync(int idTrabajador, int? idAsignacion, IReadOnlyList<PosicionGps> posiciones, CancellationToken ct)
    {
        // Un solo INSERT para todo el lote (OPENJSON): el celular envía hasta 500 posiciones juntas.
        string json = JsonSerializer.Serialize(posiciones.Select(p => new
        {
            f = p.FechaHora.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
            lat = p.Lat, lon = p.Lon, p = p.PrecisionMetros, v = p.VelocidadMs,
        }));
        await using var cn = fabrica.Crear();
        return await cn.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.PosicionTrabajador(IdTrabajador, IdAsignacion, FechaHora, Latitud, Longitud, PrecisionMetros, VelocidadMs)
            SELECT @t, @a, j.f, j.lat, j.lon, j.p, j.v
              FROM OPENJSON(@json) WITH (f DATETIME2 '$.f', lat FLOAT '$.lat', lon FLOAT '$.lon', p FLOAT '$.p', v FLOAT '$.v') j
            """, new { t = idTrabajador, a = idAsignacion, json }, cancellationToken: ct, commandTimeout: TiempoComando));
    }

    // ---------------------------------------------------------------- monitoreo

    public async Task<IReadOnlyList<UltimaPosicion>> ListarUltimasPosicionesAsync(DateTime desde, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaUltimaPosicion>(new CommandDefinition($"""
            WITH ult AS (
                SELECT p.IdTrabajador, p.IdAsignacion, p.FechaHora, p.Latitud, p.Longitud, p.PrecisionMetros,
                       ROW_NUMBER() OVER (PARTITION BY p.IdTrabajador ORDER BY p.FechaHora DESC) AS rn
                  FROM dbo.PosicionTrabajador p WHERE p.FechaHora >= @desde)
            SELECT u.IdUsuario AS IdTrabajador, u.Login, u.Nombre,
                   COALESCE((SELECT TOP (1) r.NombreRol FROM dbo.UsuariosRoles ur JOIN dbo.Roles r ON r.IdRol = ur.IdRol
                              WHERE ur.IdUsuario = u.IdUsuario AND r.NombreRol IN ('Cortador', 'Reconexion')), '') AS Rol,
                   ult.FechaHora, ult.Latitud AS Lat, ult.Longitud AS Lon, ult.PrecisionMetros, ult.IdAsignacion, a.Tipo,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle d JOIN dbo.Asignacion x ON x.IdAsignacion = d.IdAsignacion
                     WHERE x.IdTrabajador = u.IdUsuario AND x.Estado IN ({Activas}) AND d.Estado = N'Pendiente') AS Pendientes,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle d JOIN dbo.Asignacion x ON x.IdAsignacion = d.IdAsignacion
                     WHERE x.IdTrabajador = u.IdUsuario AND d.Estado IN (N'Ejecutada', N'Observada') AND d.FechaEjecucion >= @desde) AS Realizadas
              FROM ult
              JOIN dbo.Usuarios u ON u.IdUsuario = ult.IdTrabajador
              LEFT JOIN dbo.Asignacion a ON a.IdAsignacion = ult.IdAsignacion
             WHERE ult.rn = 1
             ORDER BY u.Nombre
            """, new { desde }, cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => new UltimaPosicion(f.IdTrabajador, f.Login, f.Nombre, f.Rol, f.FechaHora, f.Lat, f.Lon,
            f.PrecisionMetros, f.IdAsignacion, f.Tipo, f.Pendientes, f.Realizadas))];
    }

    public async Task<IReadOnlyList<(int IdTrabajador, string Nombre, PosicionGps Posicion)>> ListarPosicionesAsync(
        DateOnly fecha, int? idTrabajador, TipoAsignacion? tipo, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaPosicion>(new CommandDefinition("""
            SELECT p.IdTrabajador, u.Nombre, p.FechaHora, p.Latitud AS Lat, p.Longitud AS Lon, p.PrecisionMetros, p.VelocidadMs
              FROM dbo.PosicionTrabajador p
              JOIN dbo.Usuarios u ON u.IdUsuario = p.IdTrabajador
              LEFT JOIN dbo.Asignacion a ON a.IdAsignacion = p.IdAsignacion
             WHERE p.FechaHora >= @desde AND p.FechaHora < @hasta
               AND (@t IS NULL OR p.IdTrabajador = @t)
               AND (@tipo IS NULL OR a.Tipo = @tipo)
             ORDER BY p.IdTrabajador, p.FechaHora
            """, new { desde = Dia(fecha), hasta = Dia(fecha).AddDays(1), t = idTrabajador, tipo = tipo?.Codigo() },
            cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => (f.IdTrabajador, f.Nombre, new PosicionGps(f.FechaHora, f.Lat, f.Lon, f.PrecisionMetros, f.VelocidadMs)))];
    }

    public async Task<IReadOnlyList<(int IdTrabajador, ParadaRealizada Parada)>> ListarRealizadasAsync(
        DateOnly fecha, int? idTrabajador, TipoAsignacion? tipo, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaRealizada>(new CommandDefinition("""
            SELECT a.IdTrabajador, d.IdDetalle, d.IdAsignacion, a.Tipo, d.Orden, c.CodFijo, c.Nombre AS Titular, d.Resultado,
                   COALESCE(d.FechaDispositivo, d.FechaEjecucion) AS FechaEjecucion,
                   c.Geom.STY AS Lat, c.Geom.STX AS Lon, d.UbicacionEjecucion.STY AS LatEjecucion,
                   d.UbicacionEjecucion.STX AS LonEjecucion, d.DentroDelRadio
              FROM dbo.AsignacionDetalle d
              JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
              JOIN dbo.CodigosFijos c ON c.IdCodigo = d.IdCodigo
             WHERE d.Estado IN (N'Ejecutada', N'Observada')
               AND COALESCE(d.FechaDispositivo, d.FechaEjecucion) >= @desde AND COALESCE(d.FechaDispositivo, d.FechaEjecucion) < @hasta
               AND (@t IS NULL OR a.IdTrabajador = @t)
               AND (@tipo IS NULL OR a.Tipo = @tipo)
             ORDER BY a.IdTrabajador, FechaEjecucion
            """, new { desde = Dia(fecha), hasta = Dia(fecha).AddDays(1), t = idTrabajador, tipo = tipo?.Codigo() },
            cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => (f.IdTrabajador, new ParadaRealizada(f.IdDetalle, f.IdAsignacion, TipoDesde(f.Tipo), f.Orden,
            f.CodFijo, f.Titular, f.Resultado, f.FechaEjecucion, f.Lat, f.Lon, f.LatEjecucion, f.LonEjecucion, f.DentroDelRadio)))];
    }

    // ---------------------------------------------------------------- revisión de ejecuciones

    public async Task<Pagina<EjecucionRegistrada>> ListarEjecucionesAsync(FiltroEjecuciones f, CancellationToken ct)
    {
        const string desdeDonde = """
              FROM dbo.AsignacionDetalle d
              JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
              JOIN dbo.Usuarios u ON u.IdUsuario = a.IdTrabajador
              JOIN dbo.CodigosFijos c ON c.IdCodigo = d.IdCodigo
             WHERE d.Estado IN (N'Ejecutada', N'Observada')
               AND (@tipo IS NULL OR a.Tipo = @tipo)
               AND (@desde IS NULL OR d.FechaEjecucion >= @desde)
               AND (@hasta IS NULL OR d.FechaEjecucion < @hasta)
               AND (@t IS NULL OR a.IdTrabajador = @t)
               AND (@alertas = 0 OR d.DentroDelRadio = 0)
            """;
        var p = new
        {
            tipo = f.Tipo?.Codigo(), desde = f.Desde is { } d1 ? Dia(d1) : (DateTime?)null,
            hasta = f.Hasta is { } d2 ? Dia(d2).AddDays(1) : (DateTime?)null, t = f.IdTrabajador,
            alertas = f.SoloAlertas, off = (f.Pagina - 1) * f.Tamano, tam = f.Tamano,
        };
        await using var cn = fabrica.Crear();
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) {desdeDonde};
            SELECT d.IdDetalle, d.IdAsignacion, a.Tipo, d.Orden, u.Nombre AS Trabajador, c.IdCodigo, c.CodFijo, c.Nombre AS Titular,
                   d.Resultado, d.NumeroMedidor, d.Lectura, d.Observacion, d.FechaEjecucion, d.FechaDispositivo,
                   c.Geom.STY AS LatCodigo, c.Geom.STX AS LonCodigo, d.UbicacionEjecucion.STY AS LatEjecucion,
                   d.UbicacionEjecucion.STX AS LonEjecucion, d.PrecisionMetros, d.DistanciaAlPuntoMetros, d.DentroDelRadio,
                   (SELECT COUNT(*) FROM dbo.EvidenciaFoto x WHERE x.IdDetalle = d.IdDetalle) AS Fotos
              {desdeDonde}
             ORDER BY d.FechaEjecucion DESC
             OFFSET @off ROWS FETCH NEXT @tam ROWS ONLY;
            """, p, cancellationToken: ct, commandTimeout: TiempoComando));
        int total = await multi.ReadSingleAsync<int>();
        var filas = await multi.ReadAsync<FilaEjecucion>();
        return new Pagina<EjecucionRegistrada>(total, f.Pagina, f.Tamano, [.. filas.Select(x => new EjecucionRegistrada(
            x.IdDetalle, x.IdAsignacion, TipoDesde(x.Tipo), x.Orden, x.Trabajador, x.IdCodigo, x.CodFijo, x.Titular, x.Resultado,
            x.NumeroMedidor, x.Lectura, x.Observacion, x.FechaEjecucion, x.FechaDispositivo, x.LatCodigo, x.LonCodigo,
            x.LatEjecucion, x.LonEjecucion, x.PrecisionMetros, x.DistanciaAlPuntoMetros, x.DentroDelRadio, x.Fotos))]);
    }

    public async Task<IReadOnlyList<FotoEvidencia>> ListarFotosAsync(int idDetalle, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<FilaFoto>(new CommandDefinition("""
            SELECT IdFoto, IdDetalle, NombreArchivo, TipoContenido, TamanoBytes FROM dbo.EvidenciaFoto WHERE IdDetalle = @id ORDER BY IdFoto
            """, new { id = idDetalle }, cancellationToken: ct, commandTimeout: TiempoComando));
        return [.. filas.Select(f => f.Foto())];
    }

    public async Task<FotoEvidencia?> ObtenerFotoAsync(int idFoto, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaFoto>(new CommandDefinition("""
            SELECT IdFoto, IdDetalle, NombreArchivo, TipoContenido, TamanoBytes FROM dbo.EvidenciaFoto WHERE IdFoto = @id
            """, new { id = idFoto }, cancellationToken: ct, commandTimeout: TiempoComando));
        return fila?.Foto();
    }

    public async Task<ResumenOperativo> ObtenerResumenAsync(DateOnly dia, CancellationToken ct)
    {
        var p = new { desde = Dia(dia), hasta = Dia(dia).AddDays(1) };
        await using var cn = fabrica.Crear();
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition($"""
            SELECT (SELECT COUNT(*) FROM dbo.Asignacion WHERE Estado IN ({Activas})) AS AsignacionesActivas,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle d JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
                     WHERE d.Estado = N'Pendiente' AND a.Estado IN ({Activas})) AS Pendientes,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle WHERE Estado = N'Ejecutada' AND FechaEjecucion >= @desde AND FechaEjecucion < @hasta) AS RealizadasHoy,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle WHERE Estado = N'Observada' AND FechaEjecucion >= @desde AND FechaEjecucion < @hasta) AS ObservadasHoy,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle WHERE DentroDelRadio = 0 AND FechaEjecucion >= @desde AND FechaEjecucion < @hasta) AS FueraDeRadioHoy,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle d JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
                     WHERE d.Estado = N'Pendiente' AND a.Estado IN ({Activas}) AND a.Tipo = N'Corte') AS CortesPendientes,
                   (SELECT COUNT(*) FROM dbo.AsignacionDetalle d JOIN dbo.Asignacion a ON a.IdAsignacion = d.IdAsignacion
                     WHERE d.Estado = N'Pendiente' AND a.Estado IN ({Activas}) AND a.Tipo = N'Reconexion') AS ReconexionesPendientes;
            SELECT u.IdUsuario AS IdTrabajador, u.Nombre, r.NombreRol AS Rol,
                   SUM(CASE WHEN d.Estado = N'Pendiente' AND a.Estado IN ({Activas}) THEN 1 ELSE 0 END) AS Pendientes,
                   SUM(CASE WHEN d.Estado = N'Ejecutada' AND d.FechaEjecucion >= @desde AND d.FechaEjecucion < @hasta THEN 1 ELSE 0 END) AS Realizadas,
                   SUM(CASE WHEN d.Estado = N'Observada' AND d.FechaEjecucion >= @desde AND d.FechaEjecucion < @hasta THEN 1 ELSE 0 END) AS Observadas,
                   SUM(CASE WHEN d.DentroDelRadio = 0 AND d.FechaEjecucion >= @desde AND d.FechaEjecucion < @hasta THEN 1 ELSE 0 END) AS FueraDeRadio,
                   MAX(d.FechaEjecucion) AS UltimaEjecucion
              FROM dbo.Usuarios u
              JOIN dbo.UsuariosRoles ur ON ur.IdUsuario = u.IdUsuario
              JOIN dbo.Roles r ON r.IdRol = ur.IdRol AND r.NombreRol IN ('Cortador', 'Reconexion')
              LEFT JOIN dbo.Asignacion a ON a.IdTrabajador = u.IdUsuario
              LEFT JOIN dbo.AsignacionDetalle d ON d.IdAsignacion = a.IdAsignacion
             WHERE u.Activo = 1
             GROUP BY u.IdUsuario, u.Nombre, r.NombreRol
             ORDER BY u.Nombre;
            """, p, cancellationToken: ct, commandTimeout: TiempoComando));
        var r = await multi.ReadSingleAsync<FilaResumen>();
        var trabajadores = await multi.ReadAsync<FilaAvance>();
        return new ResumenOperativo(r.AsignacionesActivas, r.Pendientes, r.RealizadasHoy, r.ObservadasHoy, r.FueraDeRadioHoy,
            r.CortesPendientes, r.ReconexionesPendientes,
            [.. trabajadores.Select(t => new AvanceTrabajador(t.IdTrabajador, t.Nombre, t.Rol, t.Pendientes, t.Realizadas,
                t.Observadas, t.FueraDeRadio, t.UltimaEjecucion))]);
    }

    // ---------------------------------------------------------------- utilidades

    private static DateTime Dia(DateOnly fecha) => fecha.ToDateTime(TimeOnly.MinValue);

    private static TipoAsignacion TipoDesde(string tipo) =>
        TiposAsignacion.Parsear(tipo) ?? throw new InvalidOperationException($"Tipo de asignación desconocido en la base: {tipo}");

    /// <summary>WKB 2D de una línea (sin puntos repetidos consecutivos); null si no llega a dos puntos distintos.</summary>
    internal static byte[]? Linea(IReadOnlyList<Coordenada> puntos)
    {
        var coordenadas = new List<Coordinate>();
        foreach (var p in puntos)
            if (coordenadas.Count == 0 || coordenadas[^1].X != p.Lon || coordenadas[^1].Y != p.Lat)
                coordenadas.Add(new Coordinate(p.Lon, p.Lat));
        return coordenadas.Count < 2 ? null : Wkb.Write(Fabrica.CreateLineString([.. coordenadas]));
    }

    internal static IReadOnlyList<Coordenada> Coordenadas(byte[]? wkb)
    {
        if (wkb is null) return [];
        return [.. new WKBReader().Read(wkb).Coordinates.Select(c => new Coordenada(c.X, c.Y))];
    }

    private sealed class FilaConfiguracion
    {
        public decimal VelocidadKmH { get; set; }
        public decimal MinutosPorParada { get; set; }
        public int IntervaloGpsSegundos { get; set; }
        public int RadioValidacionMetros { get; set; }
        public int MaximoFotos { get; set; }
        public int MaximoParadas { get; set; }
    }

    private sealed class FilaTrabajador
    {
        public int IdUsuario { get; set; }
        public string Login { get; set; } = "";
        public string Nombre { get; set; } = "";
        public int Pendientes { get; set; }
    }

    private class FilaCodigo
    {
        public int IdCodigo { get; set; }
        public int? CodFijo { get; set; }
        public string? Nombre { get; set; }
        public byte EstadoCodigo { get; set; }
        public double Lon { get; set; }
        public double Lat { get; set; }
        public CodigoOperativo Codigo() => new(IdCodigo, CodFijo, Nombre, EstadoCodigo, Lon, Lat);
    }

    private sealed class FilaParada : FilaCodigo
    {
        public int IdDetalle { get; set; }
        public int Orden { get; set; }
        public string Estado { get; set; } = "";
        public double DistanciaTramoMetros { get; set; }
        public double DistanciaAccesoMetros { get; set; }
        public byte[]? Tramo { get; set; }
    }

    private sealed class FilaTarea : FilaCodigo
    {
        public int IdDetalle { get; set; }
        public int IdAsignacion { get; set; }
        public string Tipo { get; set; } = "";
        public string EstadoAsignacion { get; set; } = "";
        public int Orden { get; set; }
        public double DistanciaTramoMetros { get; set; }
        public double DistanciaAccesoMetros { get; set; }
        public TareaCampo Tarea() => new(IdDetalle, IdAsignacion, TipoDesde(Tipo), EstadoAsignacion, Orden, Codigo(),
            DistanciaTramoMetros, DistanciaAccesoMetros);
    }

    private sealed class FilaCabecera
    {
        public int IdAsignacion { get; set; }
        public string Tipo { get; set; } = "";
        public int IdTrabajador { get; set; }
        public string Estado { get; set; } = "";
        public DateTime FechaAsignacion { get; set; }
        public double PartidaLon { get; set; }
        public double PartidaLat { get; set; }
        public double DistanciaMetros { get; set; }
        public double DuracionEstimadaMin { get; set; }
    }

    private sealed class FilaUltimaPosicion
    {
        public int IdTrabajador { get; set; }
        public string Login { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string Rol { get; set; } = "";
        public DateTime FechaHora { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double? PrecisionMetros { get; set; }
        public int? IdAsignacion { get; set; }
        public string? Tipo { get; set; }
        public int Pendientes { get; set; }
        public int Realizadas { get; set; }
    }

    private sealed class FilaPosicion
    {
        public int IdTrabajador { get; set; }
        public string Nombre { get; set; } = "";
        public DateTime FechaHora { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double? PrecisionMetros { get; set; }
        public double? VelocidadMs { get; set; }
    }

    private sealed class FilaRealizada
    {
        public int IdTrabajador { get; set; }
        public int IdDetalle { get; set; }
        public int IdAsignacion { get; set; }
        public string Tipo { get; set; } = "";
        public int Orden { get; set; }
        public int? CodFijo { get; set; }
        public string? Titular { get; set; }
        public string Resultado { get; set; } = "";
        public DateTime FechaEjecucion { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double? LatEjecucion { get; set; }
        public double? LonEjecucion { get; set; }
        public bool? DentroDelRadio { get; set; }
    }

    private sealed class FilaEjecucion
    {
        public int IdDetalle { get; set; }
        public int IdAsignacion { get; set; }
        public string Tipo { get; set; } = "";
        public int Orden { get; set; }
        public string Trabajador { get; set; } = "";
        public int IdCodigo { get; set; }
        public int? CodFijo { get; set; }
        public string? Titular { get; set; }
        public string Resultado { get; set; } = "";
        public string? NumeroMedidor { get; set; }
        public decimal? Lectura { get; set; }
        public string? Observacion { get; set; }
        public DateTime FechaEjecucion { get; set; }
        public DateTime? FechaDispositivo { get; set; }
        public double LatCodigo { get; set; }
        public double LonCodigo { get; set; }
        public double? LatEjecucion { get; set; }
        public double? LonEjecucion { get; set; }
        public double? PrecisionMetros { get; set; }
        public double? DistanciaAlPuntoMetros { get; set; }
        public bool? DentroDelRadio { get; set; }
        public int Fotos { get; set; }
    }

    private sealed class FilaFoto
    {
        public int IdFoto { get; set; }
        public int IdDetalle { get; set; }
        public string NombreArchivo { get; set; } = "";
        public string TipoContenido { get; set; } = "";
        public int TamanoBytes { get; set; }
        public FotoEvidencia Foto() => new(IdFoto, IdDetalle, NombreArchivo, TipoContenido, TamanoBytes);
    }

    private sealed class FilaResumen
    {
        public int AsignacionesActivas { get; set; }
        public int Pendientes { get; set; }
        public int RealizadasHoy { get; set; }
        public int ObservadasHoy { get; set; }
        public int FueraDeRadioHoy { get; set; }
        public int CortesPendientes { get; set; }
        public int ReconexionesPendientes { get; set; }
    }

    private sealed class FilaAvance
    {
        public int IdTrabajador { get; set; }
        public string Nombre { get; set; } = "";
        public string Rol { get; set; } = "";
        public int Pendientes { get; set; }
        public int Realizadas { get; set; }
        public int Observadas { get; set; }
        public int FueraDeRadio { get; set; }
        public DateTime? UltimaEjecucion { get; set; }
    }
}
