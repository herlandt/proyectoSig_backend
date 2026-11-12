using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Seguridad;

namespace VisorDatosSIG.Api.Controllers;

/// <summary>
/// Acceso rápido para demostraciones: inicia la sesión de una cuenta de desarrollo sin escribir la
/// contraseña, que nunca llega al navegador. Solo existe en el entorno Development y con
/// Demo:Habilitado = true; en cualquier otro caso responde 404, como si no existiera.
/// Las cuentas (login por perfil) se configuran en Demo:Cuentas.
/// </summary>
[ApiController]
[Route("api/autenticacion/demo")]
[AllowAnonymous]
public sealed class AccesoDemoController(IWebHostEnvironment entorno, IConfiguration config, IUsuarioRepositorio usuarios,
    IBitacoraRepositorio bitacora, IMemoryCache cache) : ControllerBase
{
    public sealed record SolicitudDemo(string? Perfil);

    /// <summary>Perfiles disponibles, para mostrar los botones en la pantalla de acceso.</summary>
    [HttpGet]
    public IActionResult Perfiles() => Habilitado ? Ok(new { perfiles = Cuentas().Keys }) : NotFound();

    [HttpPost, SinCsrf]
    public async Task<IActionResult> Ingresar(SolicitudDemo s, CancellationToken ct)
    {
        if (!Habilitado) return NotFound();
        if (s.Perfil is null || !Cuentas().TryGetValue(s.Perfil, out var login))
            throw new ErrorValidacion("Perfil de demostración no válido.", "perfil");

        var usuario = await usuarios.ObtenerPorLoginAsync(login, ct);
        if (usuario is not { Activo: true })
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Acceso no válido",
                detail: $"La cuenta de demostración «{login}» no existe o está bloqueada.");

        ValidacionSesion.Olvidar(cache, usuario.IdUsuario);
        var principal = UsuarioActual.Crear(usuario, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        await bitacora.RegistrarAccesoAsync(usuario.IdUsuario, usuario.Login, EventosBitacora.AccesoDemo, true,
            HttpContext.Connection.RemoteIpAddress?.ToString(), "Acceso rápido de demostración", ct);
        return Ok(new { usuario = principal.Datos() });
    }

    private bool Habilitado => entorno.IsDevelopment() && config.GetValue<bool>("Demo:Habilitado");

    private Dictionary<string, string> Cuentas() => config.GetSection("Demo:Cuentas").GetChildren()
        .Where(c => !string.IsNullOrWhiteSpace(c.Value))
        .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);
}
