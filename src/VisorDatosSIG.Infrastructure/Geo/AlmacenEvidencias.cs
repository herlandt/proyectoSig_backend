using System.Security.Cryptography;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Operaciones;

namespace VisorDatosSIG.Infrastructure.Geo;

/// <summary>
/// Fotografías de evidencia en disco, fuera de wwwroot (no se publican: la API las entrega con autorización).
/// El tipo se decide por el contenido (firma JPEG o PNG), nunca por el nombre o la cabecera que envía el celular.
/// Estructura: {carpeta}/aaaa/mm/{guid}.jpg
/// </summary>
public sealed class AlmacenEvidencias(string carpeta) : IAlmacenEvidencias
{
    public const int MaximoBytes = 8 * 1024 * 1024;

    public string Carpeta { get; } = Path.GetFullPath(carpeta);

    public async Task<FotoGuardada> GuardarAsync(Stream contenido, CancellationToken ct)
    {
        using var memoria = new MemoryStream();
        var buffer = new byte[81920];
        int leidos;
        while ((leidos = await contenido.ReadAsync(buffer, ct)) > 0)
        {
            if (memoria.Length + leidos > MaximoBytes)
                throw new ErrorValidacion($"Cada fotografía admite hasta {MaximoBytes / 1024 / 1024} MB.", "fotos");
            memoria.Write(buffer, 0, leidos);
        }
        var bytes = memoria.ToArray();
        var (extension, tipo) = Tipo(bytes) ?? throw new ErrorValidacion("Las fotografías deben ser JPEG o PNG.", "fotos");

        var ahora = DateTime.Now;
        string relativo = Path.Combine(ahora.ToString("yyyy"), ahora.ToString("MM"), $"{Guid.NewGuid():N}{extension}");
        string ruta = Path.Combine(Carpeta, relativo);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        await File.WriteAllBytesAsync(ruta, bytes, ct);
        return new FotoGuardada(relativo.Replace('\\', '/'), tipo, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public Stream? Abrir(string nombreArchivo)
    {
        var ruta = RutaSegura(nombreArchivo);
        return ruta is not null && File.Exists(ruta) ? File.OpenRead(ruta) : null;
    }

    public void Eliminar(string nombreArchivo)
    {
        var ruta = RutaSegura(nombreArchivo);
        if (ruta is not null && File.Exists(ruta)) File.Delete(ruta);
    }

    /// <summary>Ruta absoluta dentro de la carpeta de evidencias; null si el nombre intenta salir de ella.</summary>
    private string? RutaSegura(string nombreArchivo)
    {
        var ruta = Path.GetFullPath(Path.Combine(Carpeta, nombreArchivo));
        return ruta.StartsWith(Carpeta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? ruta : null;
    }

    internal static (string Extension, string TipoContenido)? Tipo(ReadOnlySpan<byte> b) =>
        b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? (".jpg", "image/jpeg")
        : b.Length > 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? (".png", "image/png")
        : null;
}
