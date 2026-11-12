using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace VisorDatosSIG.Migrador;

/// <summary>
/// Preferencias del migrador en %APPDATA%\VisorDatosSIG\migrador.json.
/// La contraseña SQL NUNCA se guarda: se pide en cada sesión.
/// </summary>
public sealed class ConfiguracionLocal
{
    public string Servidor { get; set; } = "localhost";
    public string BaseDatos { get; set; } = "VisorDatosSIG";
    public bool AutenticacionWindows { get; set; } = true;
    public string UsuarioSql { get; set; } = "";
    public bool ConfiarCertificado { get; set; } = true;
    public string UltimaCarpeta { get; set; } = "";
    public string UltimoOperador { get; set; } = "";

    private static string Ruta => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisorDatosSIG", "migrador.json");

    public static ConfiguracionLocal Cargar()
    {
        try { return File.Exists(Ruta) ? JsonSerializer.Deserialize<ConfiguracionLocal>(File.ReadAllText(Ruta)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Guardar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
            File.WriteAllText(Ruta, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* preferencia opcional */ }
    }

    public string CadenaConexion(string? contrasenaSql) => new SqlConnectionStringBuilder
    {
        DataSource = Servidor.Trim(),
        InitialCatalog = BaseDatos.Trim(),
        IntegratedSecurity = AutenticacionWindows,
        UserID = AutenticacionWindows ? "" : UsuarioSql.Trim(),
        Password = AutenticacionWindows ? "" : contrasenaSql ?? "",
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = ConfiarCertificado,
        ApplicationName = "VisorDatosSIG.Migrador",
        ConnectTimeout = 15,
    }.ConnectionString;
}
