using System.Text.RegularExpressions;

namespace VisorDatosSIG.Domain;

/// <summary>Roles de dbo.Roles. La especificación exige al menos Administrador y Consultor.</summary>
public static class RolesSistema
{
    public const string Administrador = "Administrador";
    public const string Consultor = "Consultor";

    /// <summary>
    /// Perfil de acceso del visor. Los roles operativos del diseño oficial
    /// (Catastro, Lecturador, Cortador, Reconexion) se tratan como Consultor.
    /// </summary>
    public static string PerfilEfectivo(IEnumerable<string> roles) =>
        roles.Any(r => string.Equals(r, Administrador, StringComparison.OrdinalIgnoreCase))
            ? Administrador
            : Consultor;
}

public sealed class Usuario
{
    public int IdUsuario { get; init; }
    public string Login { get; init; } = "";
    public string Nombre { get; set; } = "";
    public byte[] PasswordHash { get; set; } = [];
    public byte[] PasswordSalt { get; set; } = [];
    public int Iteraciones { get; set; }
    public bool Activo { get; set; }
    public DateTime FechaRegistro { get; init; }
    public List<string> Roles { get; init; } = [];

    public string Perfil => RolesSistema.PerfilEfectivo(Roles);
    public bool EsAdministrador => Perfil == RolesSistema.Administrador;
}

public static partial class ReglasUsuario
{
    public const int LongitudMinimaContrasena = 8;

    [GeneratedRegex("^[A-Za-z0-9._-]{3,50}$")]
    private static partial Regex PatronLogin();

    public static string? ValidarLogin(string? login) =>
        string.IsNullOrWhiteSpace(login) || !PatronLogin().IsMatch(login)
            ? "El usuario debe tener entre 3 y 50 caracteres (letras, números, punto, guion o guion bajo)."
            : null;

    public static string? ValidarNombre(string? nombre) =>
        string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 120
            ? "El nombre completo es obligatorio y admite hasta 120 caracteres."
            : null;

    public static string? ValidarContrasena(string? contrasena)
    {
        if (string.IsNullOrEmpty(contrasena) || contrasena.Length < LongitudMinimaContrasena)
            return $"La contraseña debe tener al menos {LongitudMinimaContrasena} caracteres.";
        if (contrasena.Length > 128)
            return "La contraseña no puede superar los 128 caracteres.";
        if (!contrasena.Any(char.IsLetter) || !contrasena.Any(char.IsDigit))
            return "La contraseña debe combinar letras y números.";
        return null;
    }
}
