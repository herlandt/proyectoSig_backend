using Microsoft.Extensions.DependencyInjection;
using VisorDatosSIG.Application.Consultas;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Infrastructure.Datos;
using VisorDatosSIG.Infrastructure.Geo;
using VisorDatosSIG.Infrastructure.Seguridad;

namespace VisorDatosSIG.Infrastructure;

public static class ServiciosInfraestructura
{
    /// <summary>Registra repositorios, servicios de aplicación y utilidades. La cadena viene de la configuración, nunca del código.</summary>
    public static IServiceCollection AgregarVisorDatosSig(this IServiceCollection services, string cadenaConexion, string? carpetaEvidencias = null)
    {
        if (string.IsNullOrWhiteSpace(cadenaConexion))
            throw new InvalidOperationException(
                "Falta la cadena de conexión 'ConnectionStrings:VisorDatosSIG' (user-secrets o variable de entorno ConnectionStrings__VisorDatosSIG).");
        if (string.IsNullOrWhiteSpace(carpetaEvidencias))
            carpetaEvidencias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisorDatosSIG", "evidencias");

        services.AddSingleton(new FabricaConexion(cadenaConexion));
        // Parte 2: asignaciones con ruta óptima, trabajo en campo y monitoreo.
        services.AddSingleton<IRedVialProveedor, RedVialProveedor>();
        services.AddSingleton<IAlmacenEvidencias>(new AlmacenEvidencias(carpetaEvidencias));
        services.AddScoped<IOperacionesRepositorio, OperacionesRepositorio>();
        services.AddScoped<AsignacionServicio>();
        services.AddScoped<CampoServicio>();
        services.AddScoped<MonitoreoServicio>();
        services.AddSingleton<IHasherContrasena, HasherPbkdf2>();
        services.AddSingleton<ILectorShapefile, LectorShapefileNts>();
        services.AddScoped<IConsultaCapasRepositorio, ConsultaCapasRepositorio>();
        services.AddScoped<IUsuarioRepositorio, UsuarioRepositorio>();
        services.AddScoped<IBitacoraRepositorio, BitacoraRepositorio>();
        services.AddScoped<IMigracionRepositorio, MigracionRepositorio>();
        services.AddScoped<ConsultaServicio>();
        services.AddScoped<AutenticacionServicio>();
        services.AddScoped<UsuarioServicio>();
        services.AddScoped<MigracionServicio>();
        return services;
    }
}
