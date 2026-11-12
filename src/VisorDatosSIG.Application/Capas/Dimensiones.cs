using System.Globalization;
using NetTopologySuite.Geometries;
using VisorDatosSIG.Application.Rutas;

namespace VisorDatosSIG.Application.Capas;

/// <summary>Etiqueta de la capa «Dimensiones y superficie»: largo de un lado o superficie de un polígono.</summary>
public sealed record EtiquetaDimension(string Tipo, double Lon, double Lat, string Texto, double Angulo);

/// <summary>
/// Calcula las dimensiones de manzanas y lotes para rotular el mapa: el largo de cada lado del anillo exterior
/// (distancia geodésica) y la superficie (fórmula del área sobre un plano local en metros, centrado en el polígono;
/// a esta escala difiere menos de 0,1 % del área geodésica).
/// </summary>
public static class Dimensiones
{
    public const string Lado = "lado";
    public const string Superficie = "superficie";
    private const double MetrosPorGrado = Math.PI / 180 * 6371008.8;
    private static readonly CultureInfo Bolivia = CultureInfo.GetCultureInfo("es-BO");

    public static IEnumerable<EtiquetaDimension> Calcular(Geometry geometria, double ladoMinimo = 4)
    {
        for (int i = 0; i < geometria.NumGeometries; i++)
        {
            if (geometria.GetGeometryN(i) is not Polygon poligono || poligono.IsEmpty) continue;
            var anillo = poligono.ExteriorRing.Coordinates;
            double lat0 = poligono.Centroid.Y, cos = Math.Cos(lat0 * Math.PI / 180);
            for (int k = 1; k < anillo.Length; k++)
            {
                var a = new Coordenada(anillo[k - 1].X, anillo[k - 1].Y);
                var b = new Coordenada(anillo[k].X, anillo[k].Y);
                double largo = RedVial.Distancia(a, b);
                if (largo < ladoMinimo) continue;
                // Ángulo del texto en pantalla, entre -90° y 90° para que nunca quede cabeza abajo.
                double angulo = Math.Atan2((b.Lat - a.Lat), (b.Lon - a.Lon) * cos) * 180 / Math.PI;
                if (angulo > 90) angulo -= 180;
                if (angulo < -90) angulo += 180;
                yield return new EtiquetaDimension(Lado, (a.Lon + b.Lon) / 2, (a.Lat + b.Lat) / 2, string.Format(Bolivia, "{0:0.0} m", largo), Math.Round(angulo, 1));
            }
            double area = Area(poligono, cos);
            var centro = poligono.InteriorPoint;
            yield return new EtiquetaDimension(Superficie, centro.X, centro.Y, string.Format(Bolivia, "{0:N0} m²", area), 0);
        }
    }

    /// <summary>Área en m² del polígono (anillo exterior menos huecos) sobre un plano local.</summary>
    internal static double Area(Polygon poligono, double cos)
    {
        // Origen local en el primer vértice: evita perder precisión con coordenadas de millones de metros.
        var origen = poligono.ExteriorRing.Coordinates[0];
        double Anillo(Coordinate[] c)
        {
            double suma = 0;
            for (int k = 1; k < c.Length; k++)
            {
                double x1 = (c[k - 1].X - origen.X) * cos * MetrosPorGrado, y1 = (c[k - 1].Y - origen.Y) * MetrosPorGrado;
                double x2 = (c[k].X - origen.X) * cos * MetrosPorGrado, y2 = (c[k].Y - origen.Y) * MetrosPorGrado;
                suma += x1 * y2 - x2 * y1;
            }
            return Math.Abs(suma) / 2;
        }
        double area = Anillo(poligono.ExteriorRing.Coordinates);
        foreach (var hueco in poligono.InteriorRings) area -= Anillo(hueco.Coordinates);
        return area;
    }
}
