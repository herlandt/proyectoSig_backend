using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Api.Controllers;

/// <summary>
/// API de la aplicación móvil (cortadores y reconectadores). Usa token Bearer en lugar de cookie, por eso no
/// lleva protección CSRF; el token se revalida contra la base en cada solicitud (ValidarTrabajadorFiltro).
/// </summary>
[ApiController]
[Route("api/movil")]
[SinCsrf]
[Authorize(AuthenticationSchemes = EsquemasAutenticacion.Movil, Roles = EsquemasAutenticacion.RolesMovil)]
[TypeFilter(typeof(ValidarTrabajadorFiltro))]
public sealed class MovilController(CampoServicio campo, AutenticacionServicio autenticacion, IBitacoraRepositorio bitacora) : ControllerBase
{
    public sealed record SolicitudSesion(string? Login, string? Contrasena);
    public sealed record PosicionEnviada(DateTimeOffset? Fecha, double? Lat, double? Lon, double? Precision, double? Velocidad);
    public sealed record EnvioPosiciones(int? IdAsignacion, IReadOnlyList<PosicionEnviada>? Posiciones);

    /// <summary>Inicia sesión y devuelve el token: {tokenType, accessToken, expiresIn, refreshToken}.</summary>
    [HttpPost("sesion"), AllowAnonymous]
    public async Task<IActionResult> Sesion(SolicitudSesion s, CancellationToken ct)
    {
        var r = await autenticacion.IniciarSesionAsync(s.Login, s.Contrasena, Ip, ct);
        if (!r.Exito) return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Acceso no válido", detail: r.Mensaje);
        if (!r.Usuario!.Roles.Any(x => x is TiposAsignacion.RolCortador or TiposAsignacion.RolReconexion))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Acceso no permitido",
                detail: "La aplicación móvil es para cortadores y reconectadores. Use el visor web.");
        await bitacora.RegistrarAccesoAsync(r.Usuario.IdUsuario, r.Usuario.Login, EventosBitacora.LoginMovil, true, Ip, "Aplicación móvil", ct);
        // El esquema Bearer escribe la respuesta con el token.
        await HttpContext.SignInAsync(EsquemasAutenticacion.Movil, UsuarioActual.Crear(r.Usuario, EsquemasAutenticacion.Movil));
        return new EmptyResult();
    }

    /// <summary>Datos del trabajador y parámetros que usa la app (intervalo GPS, fotos, radio de validación).</summary>
    [HttpGet("perfil")]
    public async Task<IActionResult> Perfil(CancellationToken ct)
    {
        var c = await campo.ConfiguracionAsync(ct);
        var roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(x => x.Value).Distinct().ToArray();
        return Ok(new
        {
            id = User.Id(), login = User.Login(), nombre = User.FindFirst(UsuarioActual.ClaimNombre)?.Value, roles,
            tipos = new[] { TipoAsignacion.Corte, TipoAsignacion.Reconexion }.Where(t => roles.Contains(t.Rol())).Select(t => t.Codigo()),
            configuracion = new { c.IntervaloGpsSegundos, c.MaximoFotos, c.RadioValidacionMetros },
        });
    }

    /// <summary>Paradas pendientes en el orden de la ruta.</summary>
    [HttpGet("tareas")]
    public async Task<IActionResult> Tareas(CancellationToken ct) =>
        Ok((await campo.TareasAsync(User.Id(), ct)).Select(t => new
        {
            t.IdDetalle, t.IdAsignacion, tipo = t.Tipo.Codigo(), t.EstadoAsignacion, t.Orden,
            t.Codigo.IdCodigo, t.Codigo.CodFijo, t.Codigo.Nombre, lat = t.Codigo.Lat, lon = t.Codigo.Lon,
            distanciaTramoMetros = Math.Round(t.DistanciaTramoMetros, 1), distanciaAccesoMetros = Math.Round(t.DistanciaAccesoMetros, 1),
        }));

    /// <summary>Ruta completa de una asignación del trabajador, para dibujarla en el mapa del celular.</summary>
    [HttpGet("asignaciones/{id:int}/ruta")]
    public async Task<IActionResult> Ruta(int id, CancellationToken ct) =>
        Ok(AsignacionesController.Asignacion(await campo.RutaAsync(id, User.Id(), ct)));

    [HttpPost("asignaciones/{id:int}/iniciar")]
    public async Task<IActionResult> Iniciar(int id, CancellationToken ct)
    {
        await campo.IniciarRecorridoAsync(id, User.Id(), ct);
        return NoContent();
    }

    /// <summary>
    /// Registro de la parada (multipart/form-data): resultado, numeroMedidor, lectura, observacion, lat, lon, precision,
    /// fechaDispositivo (ISO 8601) y hasta N archivos «fotos». Sin GPS no se guarda.
    /// </summary>
    [HttpPost("tareas/{idDetalle:int}/ejecucion")]
    [RequestSizeLimit(40 * 1024 * 1024)]
    public async Task<IActionResult> Registrar(int idDetalle, [FromForm] IFormCollection formulario, CancellationToken ct)
    {
        string? Campo(string nombre) => formulario.TryGetValue(nombre, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString().Trim() : null;
        double? Numero(string nombre) => Campo(nombre) is { } t
            ? double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ErrorValidacion($"El campo {nombre} no es un número.", nombre)
            : null;
        decimal? lectura = Campo("lectura") is { } l
            ? decimal.TryParse(l, NumberStyles.Number, CultureInfo.InvariantCulture, out var dl) ? dl : throw new ErrorValidacion("La lectura no es un número.", "lectura")
            : null;
        DateTime? fecha = Campo("fechaDispositivo") is { } f
            ? DateTimeOffset.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto.LocalDateTime : throw new ErrorValidacion("La fecha del dispositivo no es válida.", "fechaDispositivo")
            : null;

        var datos = new DatosEjecucion(Campo("resultado"), Campo("numeroMedidor"), lectura, Campo("observacion"),
            Numero("lat"), Numero("lon"), Numero("precision"), fecha);
        var flujos = formulario.Files.Where(x => x.Name == "fotos" && x.Length > 0).Select(x => x.OpenReadStream()).ToList();
        try
        {
            var r = await campo.RegistrarAsync(User.Id(), idDetalle, datos, flujos, ct);
            await bitacora.RegistrarAccesoAsync(User.Id(), User.Login(), EventosBitacora.EjecucionRegistrada, true, Ip,
                $"Parada {idDetalle}: {r.Estado} · {r.DistanciaAlPuntoMetros:0} m del punto{(r.DentroDelRadio ? "" : " (fuera del radio)")} · {r.Fotos} foto(s)", ct);
            return Ok(r);
        }
        finally
        {
            foreach (var s in flujos) await s.DisposeAsync();
        }
    }

    /// <summary>Lote de posiciones GPS mientras el recorrido está iniciado (monitoreo).</summary>
    [HttpPost("posiciones")]
    public async Task<IActionResult> Posiciones(EnvioPosiciones envio, CancellationToken ct)
    {
        var posiciones = (envio.Posiciones ?? [])
            .Where(p => p.Fecha is not null && p.Lat is not null && p.Lon is not null)
            .Select(p => new PosicionGps(p.Fecha!.Value.LocalDateTime, p.Lat!.Value, p.Lon!.Value, p.Precision, p.Velocidad))
            .ToList();
        int guardadas = await campo.RegistrarPosicionesAsync(User.Id(), envio.IdAsignacion, posiciones, ct);
        return Ok(new { recibidas = envio.Posiciones?.Count ?? 0, guardadas });
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
}
