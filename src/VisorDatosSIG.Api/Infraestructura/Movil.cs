using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Api.Infraestructura;

public static class EsquemasAutenticacion
{
    /// <summary>Token Bearer de la aplicación móvil (cortadores y reconectadores).</summary>
    public const string Movil = "Movil";

    /// <summary>Roles que pueden usar la aplicación móvil.</summary>
    public const string RolesMovil = TiposAsignacion.RolCortador + "," + TiposAsignacion.RolReconexion;
}

/// <summary>
/// Revalida el token del celular en cada solicitud: el usuario debe seguir activo, con la misma contraseña y con
/// rol de cortador o reconectador. Así un bloqueo o un cambio de rol corta el acceso aunque el token no haya vencido.
/// </summary>
public sealed class ValidarTrabajadorFiltro(IUsuarioRepositorio usuarios) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return;
        var principal = context.HttpContext.User;
        if (principal.Identity?.IsAuthenticated != true) return;   // [Authorize] ya responde 401
        var usuario = await usuarios.ObtenerPorIdAsync(principal.Id(), context.HttpContext.RequestAborted);
        bool vigente = usuario is { Activo: true }
            && UsuarioActual.Sello(usuario) == principal.FindFirstValue(UsuarioActual.ClaimSello)
            && usuario.Roles.Any(r => r is TiposAsignacion.RolCortador or TiposAsignacion.RolReconexion);
        if (!vigente) context.Result = new UnauthorizedResult();
    }
}
