using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using VisorDatosSIG.Application.Seguridad;

namespace VisorDatosSIG.Api.Infraestructura;

/// <summary>
/// Revalida la cookie contra dbo.Usuarios: si el administrador bloquea al usuario o le cambia el
/// perfil o contraseña, la sesión abierta deja de servir aunque la cookie siga vigente (RF-SEG-03). Con la
/// expiración deslizante, sin esta comprobación la sesión de un usuario bloqueado no vencería
/// mientras siguiera en uso. La base se consulta como máximo cada 30 s por usuario, y
/// <see cref="Olvidar"/> fuerza la consulta inmediata tras un cambio administrativo.
/// </summary>
public static class ValidacionSesion
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(30);
    private sealed record EstadoSesion(string Perfil, string Sello);

    private static string Clave(int idUsuario) => $"sesion:{idUsuario}";

    public static void Olvidar(IMemoryCache cache, int idUsuario) => cache.Remove(Clave(idUsuario));

    public static async Task ValidarAsync(CookieValidatePrincipalContext ctx)
    {
        if (ctx.Principal is not { } principal) return;
        if (!await EsVigenteAsync(principal, ctx.HttpContext.RequestServices, ctx.HttpContext.RequestAborted))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    /// <summary>
    /// El usuario de la sesión (cookie o token del celular) sigue activo, con el mismo perfil y la misma contraseña.
    /// </summary>
    public static async Task<bool> EsVigenteAsync(ClaimsPrincipal principal, IServiceProvider servicios, CancellationToken ct)
    {
        int id = principal.Id();
        var cache = servicios.GetRequiredService<IMemoryCache>();

        // Perfil y sello vigentes, o null si el usuario ya no existe o está bloqueado.
        var estado = await cache.GetOrCreateAsync(Clave(id), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Vigencia;
            var u = await servicios.GetRequiredService<IUsuarioRepositorio>().ObtenerPorIdAsync(id, ct);
            return u is { Activo: true } ? new EstadoSesion(u.Perfil, UsuarioActual.Sello(u)) : null;
        });

        return estado is not null && estado.Perfil == principal.FindFirstValue(UsuarioActual.ClaimPerfil)
            && estado.Sello == principal.FindFirstValue(UsuarioActual.ClaimSello);
    }
}
