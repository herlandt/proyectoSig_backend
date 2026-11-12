using NetTopologySuite.Geometries;

namespace VisorDatosSIG.Application.Comun;

/// <summary>Error de datos de entrada: la API lo traduce a HTTP 400 con un mensaje para el usuario.</summary>
public sealed class ErrorValidacion(string mensaje, string? campo = null) : Exception(mensaje)
{
    public string? Campo { get; } = campo;
}

/// <summary>Extensión espacial en grados (X = longitud, Y = latitud).</summary>
public sealed record Extension(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Extension? DesdeEnvelope(Envelope? e) =>
        e is null || e.IsNull ? null : new Extension(e.MinX, e.MinY, e.MaxX, e.MaxY);

    public Extension Unir(Extension otra) => new(
        Math.Min(MinX, otra.MinX), Math.Min(MinY, otra.MinY),
        Math.Max(MaxX, otra.MaxX), Math.Max(MaxY, otra.MaxY));
}

public sealed record Pagina<T>(int Total, int NumeroPagina, int Tamano, IReadOnlyList<T> Elementos)
{
    public int TotalPaginas => Tamano <= 0 ? 0 : (int)Math.Ceiling(Total / (double)Tamano);
}
