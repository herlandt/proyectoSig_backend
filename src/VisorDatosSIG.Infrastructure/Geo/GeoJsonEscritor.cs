using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Converters;
using NetTopologySuite.Precision;
using VisorDatosSIG.Application.Consultas;

namespace VisorDatosSIG.Infrastructure.Geo;

/// <summary>
/// Serialización GeoJSON estándar (RFC 7946): FeatureCollection / Feature / geometry / properties,
/// coordenadas en orden longitud, latitud, redondeadas a 1e-7° (~1 cm).
/// </summary>
public static class GeoJsonEscritor
{
    private static readonly PrecisionModel Precision = new(1e7);
    private static readonly JsonSerializerOptions Opciones = new() { Converters = { new GeoJsonConverterFactory() } };

    public static void EscribirColeccion(Utf8JsonWriter w, IEnumerable<EntidadGeo> entidades, Action<Utf8JsonWriter>? miembrosAdicionales = null)
    {
        w.WriteStartObject();
        w.WriteString("type", "FeatureCollection");
        w.WriteStartArray("features");
        foreach (var e in entidades) EscribirFeature(w, e);
        w.WriteEndArray();
        miembrosAdicionales?.Invoke(w);
        w.WriteEndObject();
    }

    public static void EscribirFeature(Utf8JsonWriter w, EntidadGeo e)
    {
        w.WriteStartObject();
        w.WriteString("type", "Feature");
        w.WriteString("id", $"{e.Capa}.{e.Id}");
        w.WritePropertyName("geometry");
        EscribirGeometria(w, e.Geometria);
        w.WritePropertyName("properties");
        w.WriteStartObject();
        w.WriteString("capa", e.Capa);
        w.WriteNumber("id", e.Id);
        if (!string.IsNullOrEmpty(e.Titulo)) w.WriteString("titulo", e.Titulo);
        foreach (var (clave, valor) in e.Atributos)
        {
            w.WritePropertyName(clave);
            EscribirValor(w, valor);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static void EscribirGeometria(Utf8JsonWriter w, Geometry? g)
    {
        if (g is null || g.IsEmpty) { w.WriteNullValue(); return; }
        JsonSerializer.Serialize(w, GeometryPrecisionReducer.ReducePointwise(g, Precision), Opciones);
    }

    public static void EscribirValor(Utf8JsonWriter w, object? valor)
    {
        switch (valor)
        {
            case null or DBNull: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case DateTime d: w.WriteStringValue(d.ToString("yyyy-MM-ddTHH:mm:ss")); break;
            case bool b: w.WriteBooleanValue(b); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            case decimal m: w.WriteNumberValue(m); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case short s16: w.WriteNumberValue(s16); break;
            case byte b8: w.WriteNumberValue(b8); break;
            default: w.WriteStringValue(valor.ToString()); break;
        }
    }

    /// <summary>Lee WKB (SQL Server: Geom.STAsBinary()) a geometría NTS con SRID 4326.</summary>
    public static Geometry? DesdeWkb(byte[]? wkb)
    {
        if (wkb is null || wkb.Length == 0) return null;
        var g = new WKBReader().Read(wkb);
        g.SRID = 4326;
        return g;
    }
}
