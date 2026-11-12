using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Application.Operaciones;

/// <summary>Pedido de ruta desde la pantalla de asignación: trabajador, cantidad de códigos nuevos y punto de partida.</summary>
public sealed record SolicitudRuta(string? Tipo, int IdTrabajador, int Cantidad, double? Lat, double? Lon);

/// <summary>Parada propuesta, con el orden ya desplazado tras las paradas que el trabajador realizó antes.</summary>
public sealed record ParadaPropuesta(
    int Orden, CodigoOperativo Codigo, bool EraPendiente, double DistanciaTramoMetros,
    double DistanciaAcumuladaMetros, double DistanciaAccesoMetros, Coordenada EnRed, IReadOnlyList<Coordenada> Tramo);

/// <summary>Resultado de «Calcular ruta óptima»: se muestra para revisar el orden antes de guardar.</summary>
public sealed record PropuestaRuta(
    TipoAsignacion Tipo, int IdTrabajador, int? IdAsignacionActiva, int PendientesPrevios, int Nuevas,
    Coordenada Partida, Coordenada PartidaEnRed, IReadOnlyList<ParadaPropuesta> Paradas,
    double DistanciaMetros, double DuracionMin, IReadOnlyList<ParadaExcluida> Excluidas)
{
    public IReadOnlyList<Coordenada> Trazado =>
        [.. Paradas.SelectMany((p, i) => i == 0 ? p.Tramo : p.Tramo.Skip(1))];
}

/// <summary>
/// Asignación de cortes y reconexiones (RF de la parte 2). La ruta se calcula con Dijkstra sobre dbo.Vias
/// e incluye siempre las paradas pendientes del trabajador (ruta acumulada). Al guardar se vuelve a calcular
/// en el servidor: el orden nunca se toma del navegador.
/// </summary>
public sealed class AsignacionServicio(IOperacionesRepositorio repositorio, IRedVialProveedor redes)
{
    public async Task<PropuestaRuta> CalcularAsync(SolicitudRuta solicitud, CancellationToken ct) =>
        (await PrepararAsync(solicitud, ct)).Propuesta;

    public async Task<(int IdAsignacion, PropuestaRuta Propuesta)> GuardarAsync(SolicitudRuta solicitud, int idAsignadoPor, CancellationToken ct)
    {
        var (propuesta, _) = await PrepararAsync(solicitud, ct);
        if (propuesta.Paradas.Count == 0)
            throw new ErrorValidacion("No hay códigos para asignar con esos datos.", "cantidad");
        var plan = new PlanAGuardar(
            propuesta.Tipo, propuesta.IdTrabajador, idAsignadoPor, propuesta.Partida,
            propuesta.DistanciaMetros, propuesta.DuracionMin, propuesta.Trazado,
            [.. propuesta.Paradas.Select(p => new ParadaAGuardar(p.Codigo.IdCodigo, p.Orden, p.DistanciaTramoMetros, p.DistanciaAccesoMetros, p.Tramo))]);
        int id = await repositorio.GuardarPlanAsync(plan, ct);
        return (id, propuesta);
    }

    public Task<IReadOnlyList<Trabajador>> TrabajadoresAsync(string? tipo, CancellationToken ct) =>
        repositorio.ListarTrabajadoresAsync(Tipo(tipo), ct);

    public Task<IReadOnlyList<CodigoOperativo>> CandidatosAsync(string? tipo, CancellationToken ct) =>
        repositorio.ListarCandidatosAsync(Tipo(tipo), ct);

    public Task<AsignacionActiva?> ActivaAsync(string? tipo, int idTrabajador, CancellationToken ct) =>
        repositorio.ObtenerActivaAsync(idTrabajador, Tipo(tipo), ct);

    public async Task AnularAsync(int idAsignacion, CancellationToken ct)
    {
        if (!await repositorio.AnularAsync(idAsignacion, ct))
            throw new ErrorValidacion("La asignación no existe o ya no está activa.", "asignacion");
    }

    private async Task<(PropuestaRuta Propuesta, ConfiguracionOperativa Configuracion)> PrepararAsync(SolicitudRuta s, CancellationToken ct)
    {
        var tipo = Tipo(s.Tipo);
        if (s.Lat is not double lat || s.Lon is not double lon || !double.IsFinite(lat) || !double.IsFinite(lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180)
            throw new ErrorValidacion("Marque en el mapa el punto de partida.", "partida");
        if (!await repositorio.EsTrabajadorAsync(s.IdTrabajador, tipo, ct))
            throw new ErrorValidacion($"Seleccione un trabajador activo con el rol {tipo.Rol()}.", "trabajador");

        var configuracion = await repositorio.ObtenerConfiguracionAsync(ct);
        var activa = await repositorio.ObtenerActivaAsync(s.IdTrabajador, tipo, ct);
        var pendientes = activa?.Pendientes.ToList() ?? [];
        int disponibles = Math.Max(0, configuracion.MaximoParadas - pendientes.Count);
        if (s.Cantidad < 0 || s.Cantidad > disponibles)
            throw new ErrorValidacion(
                $"La cantidad debe estar entre 0 y {disponibles}: cada ruta admite hasta {configuracion.MaximoParadas} paradas " +
                $"y el trabajador ya tiene {pendientes.Count} pendientes.", "cantidad");
        if (s.Cantidad == 0 && pendientes.Count == 0)
            throw new ErrorValidacion("Indique cuántos códigos desea asignar.", "cantidad");

        var candidatos = await repositorio.ListarCandidatosAsync(tipo, ct);
        var codigos = candidatos.ToDictionary(c => c.IdCodigo);
        foreach (var p in pendientes) codigos[p.Codigo.IdCodigo] = p.Codigo;
        var idsPendientes = pendientes.Select(p => p.Codigo.IdCodigo).ToHashSet();

        var red = await redes.ObtenerAsync(ct);
        var plan = new PlanificadorRuta(red).Planificar(
            new Coordenada(lon, lat),
            [.. pendientes.Select(p => new ParadaCandidata(p.Codigo.IdCodigo, p.Codigo.Ubicacion))],
            [.. candidatos.Select(c => new ParadaCandidata(c.IdCodigo, c.Ubicacion))],
            s.Cantidad);

        // Las paradas ya realizadas conservan su número; la ruta nueva sigue la numeración.
        int desplazamiento = activa?.OrdenMaximoRealizado ?? 0;
        var paradas = plan.Paradas.Select(p => new ParadaPropuesta(
            desplazamiento + p.Orden, codigos[p.Id], idsPendientes.Contains(p.Id), p.DistanciaTramo,
            p.DistanciaAcumulada, p.DistanciaAcceso, p.EnRed, p.Trazado)).ToList();
        int nuevas = paradas.Count(p => !p.EraPendiente);
        var propuesta = new PropuestaRuta(
            tipo, s.IdTrabajador, activa?.IdAsignacion, pendientes.Count, nuevas, plan.Partida, plan.PartidaEnRed,
            paradas, plan.DistanciaTotal, configuracion.DuracionMinutos(plan.DistanciaTotal, paradas.Count), plan.Excluidas);
        return (propuesta, configuracion);
    }

    private static TipoAsignacion Tipo(string? tipo) =>
        TiposAsignacion.Parsear(tipo) ?? throw new ErrorValidacion("Tipo de asignación no válido: use Corte o Reconexión.", "tipo");
}
