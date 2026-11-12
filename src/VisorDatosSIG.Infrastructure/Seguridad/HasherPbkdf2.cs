using System.Security.Cryptography;
using VisorDatosSIG.Application.Seguridad;

namespace VisorDatosSIG.Infrastructure.Seguridad;

/// <summary>
/// PBKDF2-SHA256 con sal aleatoria de 32 bytes y hash de 32 bytes, igual que el diseño
/// oficial (dbo.Usuarios: PasswordHash/PasswordSalt VARBINARY(32), Iteraciones = 100000;
/// ver ScriptDatabaseV13/03_Crear_Usuario.md). La contraseña se codifica en UTF-8.
/// </summary>
public sealed class HasherPbkdf2 : IHasherContrasena
{
    public const int TamanoBytes = 32;
    public int IteracionesPorDefecto => 100_000;

    public (byte[] Hash, byte[] Salt, int Iteraciones) Generar(string contrasena)
    {
        var salt = RandomNumberGenerator.GetBytes(TamanoBytes);
        return (Calcular(contrasena, salt, IteracionesPorDefecto), salt, IteracionesPorDefecto);
    }

    public bool Verificar(string contrasena, byte[] hash, byte[] salt, int iteraciones)
    {
        if (hash.Length != TamanoBytes || iteraciones <= 0) return false;
        var calculado = Calcular(contrasena, salt, iteraciones);
        return CryptographicOperations.FixedTimeEquals(calculado, hash);
    }

    private static byte[] Calcular(string contrasena, byte[] salt, int iteraciones) =>
        Rfc2898DeriveBytes.Pbkdf2(contrasena, salt, iteraciones, HashAlgorithmName.SHA256, TamanoBytes);
}
