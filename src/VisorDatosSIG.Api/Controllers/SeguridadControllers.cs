using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Api.Controllers;

[ApiController]
[Route("api/autenticacion")]
public sealed class AutenticacionController(
    AutenticacionServicio autenticacion, IBitacoraRepositorio bitacora, IAntiforgery antiforgery, IMemoryCache cache) : ControllerBase
{
    public sealed record SolicitudInicio(string? Login, string? Contrasena);

    /// <summary>Inicia sesión y emite la cookie segura (HttpOnly, SameSite=Strict).</summary>
    [HttpPost("iniciar"), AllowAnonymous, SinCsrf]
    public async Task<IActionResult> Iniciar(SolicitudInicio solicitud, CancellationToken ct)
    {
        var r = await autenticacion.IniciarSesionAsync(solicitud.Login, solicitud.Contrasena, Ip, ct);
        if (!r.Exito) return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Acceso no válido", detail: r.Mensaje);

        ValidacionSesion.Olvidar(cache, r.Usuario!.IdUsuario);
        var principal = UsuarioActual.Crear(r.Usuario!, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return Ok(new { usuario = principal.Datos() });
    }

    [HttpPost("cerrar")]
    public async Task<IActionResult> Cerrar(CancellationToken ct)
    {
        await bitacora.RegistrarAccesoAsync(User.Id(), User.Login(), EventosBitacora.CierreSesion, true, Ip, null, ct);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("actual")]
    public IActionResult Actual() => Ok(User.Datos());

    /// <summary>Token CSRF para las operaciones de escritura (cabecera X-CSRF-TOKEN).</summary>
    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
}

[ApiController]
[Route("api/cuenta")]
public sealed class CuentaController(IUsuarioRepositorio usuarios, AutenticacionServicio autenticacion, IMemoryCache cache) : ControllerBase
{
    public sealed record SolicitudContrasena(string? Actual, string? Nueva);

    [HttpGet("perfil")]
    public async Task<IActionResult> Perfil(CancellationToken ct)
    {
        var u = await usuarios.ObtenerPorIdAsync(User.Id(), ct);
        if (u is null) return NotFound();
        return Ok(new { u.IdUsuario, u.Login, u.Nombre, u.Perfil, u.Roles, u.FechaRegistro, u.Activo });
    }

    /// <summary>Menú del usuario según dbo.UsuarioMenu: las opciones no autorizadas no se envían.</summary>
    [HttpGet("menu")]
    public async Task<IActionResult> Menu(CancellationToken ct) => Ok(await usuarios.ObtenerMenuAsync(User.Id(), ct));

    [HttpPost("contrasena")]
    public async Task<IActionResult> CambiarContrasena(SolicitudContrasena s, CancellationToken ct)
    {
        int id = User.Id();
        await autenticacion.CambiarContrasenaAsync(id, s.Actual, s.Nueva, this.Actor().Ip, ct);
        ValidacionSesion.Olvidar(cache, id);
        var usuario = await usuarios.ObtenerPorIdAsync(id, ct);
        if (usuario is not { Activo: true })
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Unauthorized();
        }
        var principal = UsuarioActual.Crear(usuario, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return NoContent();
    }
}

[ApiController]
[Route("api/usuarios")]
[Authorize(Policy = RolesSistema.Administrador)]
public sealed class UsuariosController(UsuarioServicio servicio, IMemoryCache cache) : ControllerBase
{
    public sealed record SolicitudUsuario(string? Login, string? Nombre, string? Rol, string? Contrasena);
    public sealed record SolicitudRestablecer(string? Contrasena);

    [HttpGet]
    public async Task<IActionResult> Listar(CancellationToken ct) =>
        Ok((await servicio.ListarAsync(ct)).Select(u => new { u.IdUsuario, u.Login, u.Nombre, u.Activo, u.FechaRegistro, u.Roles, u.Perfil }));

    [HttpGet("roles")]
    public async Task<IActionResult> Roles(CancellationToken ct) => Ok(await servicio.RolesAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Crear(SolicitudUsuario s, CancellationToken ct)
    {
        int id = await servicio.CrearAsync(new DatosUsuario(s.Login, s.Nombre, s.Rol, s.Contrasena), this.Actor(), ct);
        return Created($"/api/usuarios/{id}", new { idUsuario = id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Actualizar(int id, SolicitudUsuario s, CancellationToken ct)
    {
        await servicio.ActualizarAsync(id, new DatosUsuario(null, s.Nombre, s.Rol, null), this.Actor(), ct);
        ValidacionSesion.Olvidar(cache, id);   // la sesión abierta del usuario se revalida ya
        return NoContent();
    }

    [HttpPost("{id:int}/bloquear")]
    public async Task<IActionResult> Bloquear(int id, CancellationToken ct)
    {
        await servicio.CambiarEstadoAsync(id, false, this.Actor(), ct);
        ValidacionSesion.Olvidar(cache, id);   // la sesión abierta del usuario se revalida ya
        return NoContent();
    }

    [HttpPost("{id:int}/activar")]
    public async Task<IActionResult> Activar(int id, CancellationToken ct)
    {
        await servicio.CambiarEstadoAsync(id, true, this.Actor(), ct);
        ValidacionSesion.Olvidar(cache, id);   // la sesión abierta del usuario se revalida ya
        return NoContent();
    }

    [HttpPost("{id:int}/restablecer")]
    public async Task<IActionResult> Restablecer(int id, SolicitudRestablecer s, CancellationToken ct)
    {
        await servicio.RestablecerContrasenaAsync(id, s.Contrasena, this.Actor(), ct);
        ValidacionSesion.Olvidar(cache, id);
        return NoContent();
    }
}

[ApiController]
[Route("api/bitacora")]
[Authorize(Policy = RolesSistema.Administrador)]
public sealed class BitacoraController(IBitacoraRepositorio bitacora) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Listar(int pagina = 1, int tamano = 20, string? texto = null, CancellationToken ct = default)
    {
        if (pagina is < 1 or > 100000) throw new ErrorValidacion("La página debe estar entre 1 y 100000.", "pagina");
        var p = await bitacora.ListarAccesosAsync(pagina, tamano, texto, ct);
        return Ok(new { p.Total, pagina = p.NumeroPagina, p.Tamano, p.TotalPaginas, elementos = p.Elementos });
    }
}

[ApiController]
[Route("api/migraciones")]
[Authorize(Policy = RolesSistema.Administrador)]
public sealed class MigracionesController(IMigracionRepositorio migraciones) : ControllerBase
{
    /// <summary>Historial de cargas (fecha, usuario, archivo, tabla, duración, conteos y estado).</summary>
    [HttpGet]
    public async Task<IActionResult> Listar(int pagina = 1, int tamano = 20, CancellationToken ct = default)
    {
        if (pagina is < 1 or > 100000) throw new ErrorValidacion("La página debe estar entre 1 y 100000.", "pagina");
        var p = await migraciones.ListarHistorialAsync(pagina, tamano, ct);
        return Ok(new { p.Total, pagina = p.NumeroPagina, p.Tamano, p.TotalPaginas, elementos = p.Elementos });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Obtener(int id, CancellationToken ct)
    {
        var (cabecera, detalle) = await migraciones.ObtenerAsync(id, ct);
        return cabecera is null ? NotFound() : Ok(new { cabecera, detalle });
    }
}
