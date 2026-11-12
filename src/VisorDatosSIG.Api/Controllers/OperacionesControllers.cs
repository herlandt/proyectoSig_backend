using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Api.Controllers;

/// <summary>Formato compacto de coordenadas para los mapas: [lon, lat].</summary>
internal static class FormatoGeo
{
    public static double[] Punto(Coordenada c) => [Math.Round(c.Lon, 7), Math.Round(c.Lat, 7)];
    public static double[][] Linea(IEnumerable<Coordenada> puntos) => [.. puntos.Select(Punto)];
}

/// <summary>Asignación de cortes y reconexiones con ruta óptima (Dijkstra sobre dbo.Vias).</summary>
[ApiController]
[Route("api/asignaciones")]
[Authorize(Policy = RolesSistema.Administrador)]
public sealed class AsignacionesController(AsignacionServicio servicio, IBitacoraRepositorio bitacora) : ControllerBase
{
    [HttpGet("trabajadores")]
    public async Task<IActionResult> Trabajadores([FromQuery] string? tipo, CancellationToken ct) =>
        Ok(await servicio.TrabajadoresAsync(tipo, ct));

    /// <summary>Códigos que se pueden asignar: «Para Corte» (cortes) o «Cortado» (reconexiones) sin parada pendiente.</summary>
    [HttpGet("candidatos")]
    public async Task<IActionResult> Candidatos([FromQuery] string? tipo, CancellationToken ct) =>
        Ok((await servicio.CandidatosAsync(tipo, ct)).Select(c => new { c.IdCodigo, c.CodFijo, c.Nombre, punto = FormatoGeo.Punto(c.Ubicacion) }));

    /// <summary>Ruta activa del trabajador (paradas pendientes y realizadas), o 204 si no tiene.</summary>
    [HttpGet("activa")]
    public async Task<IActionResult> Activa([FromQuery] string? tipo, [FromQuery] int trabajador, CancellationToken ct)
    {
        var a = await servicio.ActivaAsync(tipo, trabajador, ct);
        return a is null ? NoContent() : Ok(Asignacion(a));
    }

    /// <summary>«Calcular ruta óptima»: no guarda nada; se muestra para revisar el orden.</summary>
    [HttpPost("calcular")]
    public async Task<IActionResult> Calcular(SolicitudRuta solicitud, CancellationToken ct) =>
        Ok(Propuesta(await servicio.CalcularAsync(solicitud, ct)));

    [HttpPost]
    public async Task<IActionResult> Guardar(SolicitudRuta solicitud, CancellationToken ct)
    {
        var (id, propuesta) = await servicio.GuardarAsync(solicitud, User.Id(), ct);
        await bitacora.RegistrarAccesoAsync(User.Id(), User.Login(), EventosBitacora.AsignacionGuardada, true, this.Actor().Ip,
            $"{propuesta.Tipo.Nombre()} · trabajador {propuesta.IdTrabajador} · {propuesta.Paradas.Count} paradas ({propuesta.Nuevas} nuevas)", ct);
        return Ok(new { idAsignacion = id, propuesta = Propuesta(propuesta) });
    }

    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id, CancellationToken ct)
    {
        await servicio.AnularAsync(id, ct);
        await bitacora.RegistrarAccesoAsync(User.Id(), User.Login(), EventosBitacora.AsignacionAnulada, true, this.Actor().Ip, $"Asignación {id}", ct);
        return NoContent();
    }

    internal static object Propuesta(PropuestaRuta p) => new
    {
        tipo = p.Tipo.Codigo(), p.IdTrabajador, p.IdAsignacionActiva, p.PendientesPrevios, p.Nuevas,
        partida = FormatoGeo.Punto(p.Partida), partidaEnRed = FormatoGeo.Punto(p.PartidaEnRed),
        distanciaMetros = Math.Round(p.DistanciaMetros, 1), duracionMin = Math.Round(p.DuracionMin, 1),
        trazado = FormatoGeo.Linea(p.Trazado),
        paradas = p.Paradas.Select(x => new
        {
            x.Orden, x.Codigo.IdCodigo, x.Codigo.CodFijo, x.Codigo.Nombre, x.EraPendiente,
            punto = FormatoGeo.Punto(x.Codigo.Ubicacion), enRed = FormatoGeo.Punto(x.EnRed),
            distanciaTramo = Math.Round(x.DistanciaTramoMetros, 1), distanciaAcumulada = Math.Round(x.DistanciaAcumuladaMetros, 1),
            distanciaAcceso = Math.Round(x.DistanciaAccesoMetros, 1),
        }),
        excluidas = p.Excluidas,
    };

    internal static object Asignacion(AsignacionActiva a) => new
    {
        a.IdAsignacion, tipo = a.Tipo.Codigo(), a.IdTrabajador, a.Estado, a.FechaAsignacion,
        partida = FormatoGeo.Punto(a.Partida), distanciaMetros = Math.Round(a.DistanciaMetros, 1),
        duracionMin = Math.Round(a.DuracionEstimadaMin, 1),
        paradas = a.Paradas.Select(x => new
        {
            x.IdDetalle, x.Orden, x.Estado, x.Codigo.IdCodigo, x.Codigo.CodFijo, x.Codigo.Nombre,
            punto = FormatoGeo.Punto(x.Codigo.Ubicacion), distanciaTramo = Math.Round(x.DistanciaTramoMetros, 1),
            distanciaAcceso = Math.Round(x.DistanciaAccesoMetros, 1), tramo = FormatoGeo.Linea(x.Tramo),
        }),
    };
}

/// <summary>Revisión de lo registrado en campo: resultado, lectura, observación, fotos y validación GPS.</summary>
[ApiController]
[Route("api/ejecuciones")]
[Authorize(Policy = RolesSistema.Administrador)]
public sealed class EjecucionesController(IOperacionesRepositorio repositorio, IAlmacenEvidencias almacen) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? tipo, [FromQuery] DateOnly? desde, [FromQuery] DateOnly? hasta,
        [FromQuery] int? trabajador, [FromQuery] bool soloAlertas = false, [FromQuery] int pagina = 1, [FromQuery] int tamano = 20,
        CancellationToken ct = default)
    {
        TipoAsignacion? t = string.IsNullOrWhiteSpace(tipo) ? null
            : TiposAsignacion.Parsear(tipo) ?? throw new ErrorValidacion("Tipo no válido: use Corte o Reconexión.", "tipo");
        if (pagina is < 1 or > 100000) throw new ErrorValidacion("La página debe estar entre 1 y 100000.", "pagina");
        var p = await repositorio.ListarEjecucionesAsync(
            new FiltroEjecuciones(t, desde, hasta, trabajador, soloAlertas, pagina, Math.Clamp(tamano, 5, 100)), ct);
        return Ok(new
        {
            p.Total, pagina = p.NumeroPagina, p.Tamano, p.TotalPaginas,
            elementos = p.Elementos.Select(e => new
            {
                e.IdDetalle, e.IdAsignacion, tipo = e.Tipo.Codigo(), e.Orden, e.Trabajador, e.IdCodigo, e.CodFijo, e.Titular,
                e.Resultado, e.NumeroMedidor, e.Lectura, e.Observacion, e.FechaEjecucion, e.FechaDispositivo,
                punto = new[] { e.LonCodigo, e.LatCodigo },
                ubicacion = e.LonEjecucion is double lon && e.LatEjecucion is double lat ? new[] { lon, lat } : null,
                e.PrecisionMetros, e.DistanciaAlPuntoMetros, e.DentroDelRadio, e.Fotos,
            }),
        });
    }

    [HttpGet("{idDetalle:int}/fotos")]
    public async Task<IActionResult> Fotos(int idDetalle, CancellationToken ct) =>
        Ok((await repositorio.ListarFotosAsync(idDetalle, ct)).Select(f => new { f.IdFoto, f.TipoContenido, f.TamanoBytes }));

    /// <summary>Imagen de evidencia (solo con sesión de administrador; no se publica en wwwroot).</summary>
    [HttpGet("fotos/{idFoto:int}")]
    public async Task<IActionResult> Foto(int idFoto, CancellationToken ct)
    {
        var foto = await repositorio.ObtenerFotoAsync(idFoto, ct);
        var flujo = foto is null ? null : almacen.Abrir(foto.NombreArchivo);
        return flujo is null ? NotFound() : File(flujo, foto!.TipoContenido);
    }
}

/// <summary>Monitoreo del personal: posición en vivo y recorridos del día ajustados a las calles.</summary>
[ApiController]
[Route("api/monitoreo")]
[Authorize(Policy = RolesSistema.Administrador)]
public sealed class MonitoreoController(MonitoreoServicio servicio) : ControllerBase
{
    [HttpGet("en-vivo")]
    public async Task<IActionResult> EnVivo(CancellationToken ct) => Ok(await servicio.EnVivoAsync(ct));

    [HttpGet("recorridos")]
    public async Task<IActionResult> Recorridos([FromQuery] DateOnly? fecha, [FromQuery] int? trabajador, [FromQuery] string? actividad,
        CancellationToken ct)
    {
        var recorridos = await servicio.RecorridosAsync(fecha ?? DateOnly.FromDateTime(DateTime.Today), trabajador, actividad, ct);
        return Ok(recorridos.Select(r => new
        {
            r.IdTrabajador, r.Nombre, distanciaEnRedMetros = Math.Round(r.DistanciaEnRedMetros, 1),
            posiciones = r.Posiciones.Select(p => new { p.FechaHora, punto = FormatoGeo.Punto(p.Ubicacion), p.PrecisionMetros, p.VelocidadMs }),
            trazadoEnRed = FormatoGeo.Linea(r.TrazadoEnRed),
            paradas = r.Paradas.Select(p => new
            {
                p.IdDetalle, p.IdAsignacion, tipo = p.Tipo.Codigo(), p.Orden, p.CodFijo, p.Titular, p.Resultado, p.FechaEjecucion,
                punto = new[] { p.Lon, p.Lat }, p.DentroDelRadio,
            }),
        }));
    }

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen([FromQuery] DateOnly? dia, CancellationToken ct) =>
        Ok(await servicio.ResumenAsync(dia ?? DateOnly.FromDateTime(DateTime.Today), ct));
}

/// <summary>Parámetros operativos: velocidad, minutos por parada, intervalo GPS, radio de validación, fotos y paradas.</summary>
[ApiController]
[Route("api/configuracion-operativa")]
public sealed class ConfiguracionOperativaController(IOperacionesRepositorio repositorio, IBitacoraRepositorio bitacora) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Obtener(CancellationToken ct) => Ok(await repositorio.ObtenerConfiguracionAsync(ct));

    [HttpPut, Authorize(Policy = RolesSistema.Administrador)]
    public async Task<IActionResult> Guardar(ConfiguracionOperativa c, CancellationToken ct)
    {
        if (c.VelocidadKmH is < 1 or > 120) throw new ErrorValidacion("La velocidad debe estar entre 1 y 120 km/h.", "velocidadKmH");
        if (c.MinutosPorParada is < 0 or > 240) throw new ErrorValidacion("Los minutos por parada deben estar entre 0 y 240.", "minutosPorParada");
        if (c.IntervaloGpsSegundos is < 5 or > 600) throw new ErrorValidacion("El intervalo GPS debe estar entre 5 y 600 segundos.", "intervaloGpsSegundos");
        if (c.RadioValidacionMetros is < 10 or > 1000) throw new ErrorValidacion("El radio de validación debe estar entre 10 y 1000 m.", "radioValidacionMetros");
        if (c.MaximoFotos is < 1 or > 10) throw new ErrorValidacion("Las fotografías por parada deben estar entre 1 y 10.", "maximoFotos");
        if (c.MaximoParadas is < 1 or > 200) throw new ErrorValidacion("Las paradas por ruta deben estar entre 1 y 200.", "maximoParadas");
        await repositorio.GuardarConfiguracionAsync(c, ct);
        await bitacora.RegistrarAccesoAsync(User.Id(), User.Login(), EventosBitacora.ConfiguracionOperativa, true, this.Actor().Ip,
            $"{c.VelocidadKmH} km/h · {c.MinutosPorParada} min/parada · GPS {c.IntervaloGpsSegundos} s · radio {c.RadioValidacionMetros} m", ct);
        return NoContent();
    }
}
