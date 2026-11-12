using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Application.Operaciones;

/// <summary>Datos que envía el celular al guardar una parada.</summary>
public sealed record DatosEjecucion(
    string? Resultado, string? NumeroMedidor, decimal? Lectura, string? Observacion,
    double? Lat, double? Lon, double? PrecisionMetros, DateTime? FechaDispositivo);

public sealed record ResultadoRegistro(int IdDetalle, string Estado, double DistanciaAlPuntoMetros, bool DentroDelRadio, int Fotos);

/// <summary>
/// Trabajo en campo desde la aplicación móvil: tareas del trabajador, registro de cada parada y posiciones GPS.
/// La ubicación es obligatoria al guardar (evita reportes falsos de «casa cerrada»); el servidor calcula la
/// distancia al código y marca si quedó fuera del radio configurado.
/// </summary>
public sealed class CampoServicio(IOperacionesRepositorio repositorio, IAlmacenEvidencias almacen)
{
    public const int MaximoPosicionesPorEnvio = 500;

    public Task<IReadOnlyList<TareaCampo>> TareasAsync(int idTrabajador, CancellationToken ct) =>
        repositorio.ListarTareasAsync(idTrabajador, ct);

    public Task<ConfiguracionOperativa> ConfiguracionAsync(CancellationToken ct) => repositorio.ObtenerConfiguracionAsync(ct);

    public async Task<AsignacionActiva> RutaAsync(int idAsignacion, int idTrabajador, CancellationToken ct)
    {
        var asignacion = await repositorio.ObtenerAsignacionAsync(idAsignacion, ct);
        if (asignacion is null || asignacion.IdTrabajador != idTrabajador)
            throw new ErrorValidacion("La asignación no existe o no le pertenece.", "asignacion");
        return asignacion;
    }

    public async Task IniciarRecorridoAsync(int idAsignacion, int idTrabajador, CancellationToken ct)
    {
        if (!await repositorio.IniciarRecorridoAsync(idAsignacion, idTrabajador, ct))
            throw new ErrorValidacion("La asignación no existe, no le pertenece o ya terminó.", "asignacion");
    }

    public async Task<ResultadoRegistro> RegistrarAsync(
        int idTrabajador, int idDetalle, DatosEjecucion datos, IReadOnlyList<Stream> fotos, CancellationToken ct)
    {
        var tarea = await repositorio.ObtenerTareaAsync(idDetalle, idTrabajador, ct)
            ?? throw new ErrorValidacion("La tarea no existe, ya fue registrada o no está asignada a usted.", "tarea");

        if (datos.Lat is not double lat || datos.Lon is not double lon || !double.IsFinite(lat) || !double.IsFinite(lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180 || (lat == 0 && lon == 0))
            throw new ErrorValidacion("La ubicación GPS es obligatoria para registrar el trabajo. Active el GPS e intente de nuevo.", "gps");
        if (datos.PrecisionMetros is < 0 or > 5000)
            throw new ErrorValidacion("La precisión del GPS no es válida.", "gps");

        var configuracion = await repositorio.ObtenerConfiguracionAsync(ct);
        string resultado = datos.Resultado?.Trim() ?? "";
        bool realizado = string.Equals(resultado, tarea.Tipo.ResultadoRealizado(), StringComparison.OrdinalIgnoreCase);
        bool observado = string.Equals(resultado, ResultadosEjecucion.Observado, StringComparison.OrdinalIgnoreCase);
        if (!realizado && !observado)
            throw new ErrorValidacion($"Resultado no válido: use «{tarea.Tipo.ResultadoRealizado()}» u «Observado».", "resultado");

        string? observacion = Recortar(datos.Observacion, 300, "La observación", "observacion");
        string? medidor = Recortar(datos.NumeroMedidor, 30, "El número de medidor", "medidor");
        if (observado && observacion is null)
            throw new ErrorValidacion("Describa por qué no se pudo realizar (por ejemplo, «casa cerrada»).", "observacion");
        if (realizado && datos.Lectura is null)
            throw new ErrorValidacion("Registre la lectura actual del medidor.", "lectura");
        if (datos.Lectura is < 0 or > 9_999_999_999m)
            throw new ErrorValidacion("La lectura del medidor no es válida.", "lectura");
        if (realizado && fotos.Count == 0)
            throw new ErrorValidacion("Tome al menos una fotografía del medidor.", "fotos");
        if (fotos.Count > configuracion.MaximoFotos)
            throw new ErrorValidacion($"Se admiten hasta {configuracion.MaximoFotos} fotografías.", "fotos");

        var ubicacion = new Coordenada(lon, lat);
        double distancia = RedVial.Distancia(ubicacion, tarea.Codigo.Ubicacion);
        bool dentro = distancia <= configuracion.RadioValidacionMetros;
        var ejecucion = new EjecucionARegistrar(
            idDetalle, tarea.IdAsignacion, tarea.Codigo.IdCodigo,
            realizado ? tarea.Tipo.ResultadoRealizado() : ResultadosEjecucion.Observado,
            realizado ? EstadosParada.Ejecutada : EstadosParada.Observada,
            medidor, datos.Lectura, observacion, datos.FechaDispositivo, ubicacion, datos.PrecisionMetros,
            Math.Round(distancia, 1), dentro, realizado ? tarea.Tipo.EstadoTrasEjecutar() : null);

        // Primero los archivos; si el registro falla se borran para no dejar fotos huérfanas.
        var guardadas = new List<FotoGuardada>();
        try
        {
            foreach (var foto in fotos) guardadas.Add(await almacen.GuardarAsync(foto, ct));
            await repositorio.RegistrarEjecucionAsync(ejecucion, guardadas, ct);
        }
        catch
        {
            foreach (var g in guardadas) almacen.Eliminar(g.NombreArchivo);
            throw;
        }
        return new ResultadoRegistro(idDetalle, ejecucion.EstadoParada, ejecucion.DistanciaAlPuntoMetros, dentro, guardadas.Count);
    }

    /// <summary>Guarda las posiciones válidas de un envío y descarta las que no lo son (coordenadas o fechas imposibles).</summary>
    public async Task<int> RegistrarPosicionesAsync(int idTrabajador, int? idAsignacion, IReadOnlyList<PosicionGps> posiciones, CancellationToken ct)
    {
        if (posiciones.Count > MaximoPosicionesPorEnvio)
            throw new ErrorValidacion($"Se admiten hasta {MaximoPosicionesPorEnvio} posiciones por envío.", "posiciones");
        if (posiciones.Count == 0) return 0;
        if (idAsignacion is int id && !await repositorio.AsignacionPerteneceAsync(id, idTrabajador, ct))
            throw new ErrorValidacion("La asignación no pertenece al trabajador.", "asignacion");

        var ahora = DateTime.Now;
        var validas = posiciones
            .Where(p => double.IsFinite(p.Lat) && double.IsFinite(p.Lon) && p.Lat is >= -90 and <= 90 && p.Lon is >= -180 and <= 180
                        && !(p.Lat == 0 && p.Lon == 0) && p.FechaHora <= ahora.AddMinutes(10) && p.FechaHora >= ahora.AddDays(-7)
                        && p.PrecisionMetros is null or (>= 0 and <= 5000))
            .OrderBy(p => p.FechaHora)
            .ToList();
        return validas.Count == 0 ? 0 : await repositorio.RegistrarPosicionesAsync(idTrabajador, idAsignacion, validas, ct);
    }

    private static string? Recortar(string? texto, int maximo, string nombre, string campo)
    {
        var t = texto?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > maximo) throw new ErrorValidacion($"{nombre} admite hasta {maximo} caracteres.", campo);
        return t;
    }
}
