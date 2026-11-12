using System.Diagnostics;

namespace VisorDatosSIG.Application.Migracion;

public sealed record CapaAMigrar(DefinicionCapaMigracion Definicion, ArchivosShapefile Archivos, bool ConfirmadoSinPrj = false);

public sealed record SolicitudMigracion(
    string Usuario,
    string Equipo,
    ModoCarga Modo,
    IReadOnlyList<CapaAMigrar> Capas,
    int TamanoLote = 1000,
    bool DetenerSiHayFilasConError = false);

public sealed record ProgresoMigracion(
    string Capa, string Fase, int Procesados, int Total, int Exitosos, int Omitidos, int Fallidos, string? Mensaje = null);

public sealed record ResultadoCapaMigrada(
    DefinicionCapaMigracion Definicion, string Archivo, int IdMigracion, ConteosCarga Conteos, RegistroIncidencias Incidencias);

public sealed record ResumenMigracion(
    string Estado,
    string Mensaje,
    TimeSpan Duracion,
    IReadOnlyList<ResultadoCapaMigrada> Capas,
    ResultadoVinculos? Vinculos,
    int GeometriasReparadasEnSql,
    IReadOnlyDictionary<string, int>? ConteosFinales)
{
    public bool Exitosa => Estado == EstadosMigracion.Completada;
}

public static class EstadosMigracion
{
    public const string EnCurso = "EnCurso";
    public const string Completada = "Completada";
    public const string Revertida = "Revertida";
    public const string Cancelada = "Cancelada";
    public const string Fallida = "Fallida";
    public const string Interrumpida = "Interrumpida";
}

/// <summary>
/// Orquesta la carga SHP → SQL Server: valida antes de tocar la base, carga por lotes
/// dentro de UNA transacción (todas las capas + vínculos espaciales), y registra la
/// bitácora fuera de la transacción para que sobreviva a un rollback (RF-MIG-07..12).
/// </summary>
public sealed class MigracionServicio(ILectorShapefile lector, IMigracionRepositorio repositorio)
{
    public async Task<ResumenMigracion> EjecutarAsync(
        SolicitudMigracion solicitud, IProgress<ProgresoMigracion>? progreso, CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        if (solicitud.Capas.Count == 0)
            return new(EstadosMigracion.Fallida, "No se seleccionó ninguna capa.", reloj.Elapsed, [], null, 0, null);

        await repositorio.MarcarInterrumpidasAsync(ct);

        var capas = solicitud.Capas.OrderBy(c => c.Definicion.Orden).ToList();
        var validador = new ValidadorShapefile(lector);

        // 1) Validación estructural de TODAS las capas antes de abrir la transacción (CA-02).
        var bloqueos = new List<(CapaAMigrar Capa, RegistroIncidencias Incidencias)>();
        foreach (var capa in capas)
        {
            progreso?.Report(new(capa.Definicion.Nombre, "Validando estructura", 0, 0, 0, 0, 0));
            var (_, inc) = validador.ValidarEstructura(capa.Definicion, capa.Archivos, capa.ConfirmadoSinPrj);
            if (inc.HayBloqueantes) bloqueos.Add((capa, inc));
        }
        if (bloqueos.Count > 0)
        {
            var resultados = new List<ResultadoCapaMigrada>();
            foreach (var (capa, inc) in bloqueos)
            {
                int id = await repositorio.IniciarBitacoraAsync(Entrada(solicitud, capa), ct);
                await repositorio.RegistrarIncidenciasAsync(id, inc.ParaBitacora(), ct);
                await repositorio.FinalizarBitacoraAsync(id, EstadosMigracion.Fallida, new(0, 0, 0, 0, 0, 0), 0,
                    "Bloqueada por validación: la base no se modificó.", ct);
                resultados.Add(new(capa.Definicion, capa.Archivos.Nombre, id, new(0, 0, 0, 0, 0, 0), inc));
            }
            return new(EstadosMigracion.Fallida,
                "La validación bloqueó la carga (archivos, PRJ o esquema). No se modificó la base de datos.",
                reloj.Elapsed, resultados, null, 0, null);
        }

        // 2) Bitácora EnCurso por capa (fuera de la transacción).
        var trabajos = new List<Trabajo>();
        foreach (var capa in capas)
            trabajos.Add(new Trabajo(capa, await repositorio.IniciarBitacoraAsync(Entrada(solicitud, capa), ct)));

        string estado = EstadosMigracion.Completada, mensaje = "Migración completada.";
        ResultadoVinculos? vinculos = null;
        IReadOnlyDictionary<string, int>? conteosFinales = null;
        int reparadosSql = 0;

        IMigracionTransaccion? transaccion = null;
        try
        {
            transaccion = await repositorio.IniciarTransaccionAsync(ct);
        }
        catch (Exception ex)
        {
            estado = EstadosMigracion.Fallida;
            mensaje = $"No se pudo iniciar la transacción de carga: {ex.Message}";
        }

        if (transaccion is not null)
        {
            await using var tx = transaccion;
            try
            {
                if (solicitud.Modo == ModoCarga.Reemplazar)
                {
                    progreso?.Report(new("Todas", "Preparando reemplazo seguro", 0, 0, 0, 0, 0));
                    await tx.PrepararReemplazoAsync(capas.Select(c => c.Definicion.Id).ToList(), ct);
                }

                foreach (var t in trabajos)
                    await CargarCapaAsync(tx, t, solicitud, progreso, ct);

                progreso?.Report(new("Todas", "Validando geometrías en SQL Server (STIsValid)", 0, 0, 0, 0, 0));
                reparadosSql = await tx.RepararGeometriasInvalidasAsync(ct);

                progreso?.Report(new("Todas", "Vinculando lotes ↔ manzanas y códigos ↔ lotes", 0, 0, 0, 0, 0));
                vinculos = await tx.ActualizarVinculosAsync(ct);
                conteosFinales = await tx.ContarAsync(ct);

                ct.ThrowIfCancellationRequested();
                progreso?.Report(new("Todas", "Confirmando transacción", 0, 0, 0, 0, 0));
                await tx.ConfirmarAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await tx.RevertirAsync();
                estado = EstadosMigracion.Cancelada;
                mensaje = "Operación cancelada por el usuario: la transacción se revirtió y la base quedó sin cambios.";
            }
            catch (Exception ex)
            {
                await tx.RevertirAsync();
                estado = EstadosMigracion.Revertida;
                mensaje = $"Error crítico durante la carga; la transacción se revirtió: {ex.Message}";
            }
        }

        // 3) Cierre sin el token cancelado. Un error de auditoría no cambia el resultado de los datos.
        bool falloCierre = false;
        foreach (var t in trabajos)
        {
            var c = t.Conteos();
            var conteos = estado == EstadosMigracion.Completada ? c : c with { Insertados = 0 };
            try
            {
                await repositorio.RegistrarIncidenciasAsync(t.IdMigracion, t.Incidencias.ParaBitacora(), CancellationToken.None);
            }
            catch (Exception) { falloCierre = true; }
            try
            {
                await repositorio.FinalizarBitacoraAsync(t.IdMigracion, estado, conteos, (int)t.Reloj.ElapsedMilliseconds,
                    estado == EstadosMigracion.Completada ? Detalle(t, vinculos) : mensaje, CancellationToken.None);
            }
            catch (Exception) { falloCierre = true; }
        }
        if (falloCierre)
            mensaje = estado == EstadosMigracion.Completada
                ? "Datos cargados; no se pudo cerrar el registro de auditoría"
                : mensaje + " No se pudo cerrar el registro de auditoría.";

        return new(estado, mensaje, reloj.Elapsed,
            trabajos.Select(t => new ResultadoCapaMigrada(t.Capa.Definicion, t.Capa.Archivos.Nombre, t.IdMigracion,
                estado == EstadosMigracion.Completada ? t.Conteos() : t.Conteos() with { Insertados = 0 }, t.Incidencias)).ToList(),
            vinculos, reparadosSql, conteosFinales);
    }

    private async Task CargarCapaAsync(IMigracionTransaccion tx, Trabajo t, SolicitudMigracion s,
        IProgress<ProgresoMigracion>? progreso, CancellationToken ct)
    {
        var def = t.Capa.Definicion;
        t.Reloj.Restart();
        var existentes = s.Modo == ModoCarga.Anexar
            ? await tx.ObtenerClavesExistentesAsync(def.Id, ct)
            : new HashSet<string>();
        int total = lector.Inspeccionar(t.Capa.Archivos).Registros;
        var transformador = new TransformadorCapa(def, t.Incidencias);
        var lote = new List<FilaDestino>(s.TamanoLote);

        void Reportar(string fase) => progreso?.Report(new(def.Nombre, fase, t.Leidos, total, t.Insertados, t.Omitidos, t.Fallidos));
        Reportar("Cargando");

        foreach (var registro in lector.Leer(t.Capa.Archivos, ct))
        {
            ct.ThrowIfCancellationRequested();
            t.Leidos++;
            var r = transformador.Transformar(registro);
            if (r.Reparada) t.Reparados++;
            switch (r.Estado)
            {
                case EstadoFila.Fallida: t.Fallidos++; continue;
                case EstadoFila.Duplicada: t.Omitidos++; continue;
            }
            if (existentes.Contains(r.Fila!.ClaveNatural))
            {
                t.Omitidos++;
                t.Incidencias.Agregar(NivelIncidencia.Info, "YA_EXISTE",
                    "El registro ya existe en la base (modo Anexar); se omite.", r.Fila.Fid, "Clave", r.Fila.ClaveNatural);
                continue;
            }
            lote.Add(r.Fila);
            if (lote.Count >= s.TamanoLote)
            {
                await tx.InsertarAsync(def, lote, ct);
                t.Insertados += lote.Count;
                lote.Clear();
                Reportar("Cargando");
            }
        }
        if (lote.Count > 0)
        {
            await tx.InsertarAsync(def, lote, ct);
            t.Insertados += lote.Count;
        }
        Reportar("Capa cargada");

        if (s.DetenerSiHayFilasConError && t.Fallidos > 0)
            throw new InvalidOperationException($"{def.Nombre}: {t.Fallidos} registro(s) con error y la opción 'detener ante errores' está activa.");
    }

    private static EntradaBitacoraMigracion Entrada(SolicitudMigracion s, CapaAMigrar c) =>
        new(s.Usuario, s.Equipo, c.Definicion.Id, c.Archivos.Shp ?? Path.Combine(c.Archivos.Carpeta, c.Archivos.Nombre + ".shp"),
            c.Definicion.Tabla, s.Modo);

    private static string Detalle(Trabajo t, ResultadoVinculos? v)
    {
        var texto = $"Leídos {t.Leidos}, insertados {t.Insertados}, omitidos {t.Omitidos}, fallidos {t.Fallidos}, reparados {t.Reparados}.";
        if (v is null) return texto;
        return t.Capa.Definicion.Id switch
        {
            "codigosfijos" => texto + $" Códigos con lote: {v.CodigosConLote}/{v.TotalCodigos}.",
            "lotes" => texto + $" Lotes con manzana: {v.LotesConManzana}/{v.TotalLotes}.",
            _ => texto
        };
    }

    private sealed class Trabajo(CapaAMigrar capa, int idMigracion)
    {
        public CapaAMigrar Capa { get; } = capa;
        public int IdMigracion { get; } = idMigracion;
        public RegistroIncidencias Incidencias { get; } = new();
        public Stopwatch Reloj { get; } = new();
        public int Leidos, Insertados, Omitidos, Fallidos, Reparados;
        public ConteosCarga Conteos() => new(Leidos, Insertados, Omitidos, Fallidos, Reparados, Incidencias.Advertencias);
    }
}
