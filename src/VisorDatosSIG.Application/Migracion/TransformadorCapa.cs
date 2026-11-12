using System.Globalization;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Valid;

namespace VisorDatosSIG.Application.Migracion;

public enum EstadoFila { Valida, Fallida, Duplicada }

public sealed record ResultadoFila(EstadoFila Estado, FilaDestino? Fila, bool Reparada);

/// <summary>
/// Convierte un registro SHP en una fila destino: valida y repara la geometría (regla
/// documentada: GeometryFixer de NTS), aplica el mapeo explícito con conversión de
/// tipos y longitudes, y detecta duplicados por clave natural dentro del archivo.
/// </summary>
public sealed class TransformadorCapa(DefinicionCapaMigracion definicion, RegistroIncidencias incidencias)
{
    public const int Srid = 4326;
    private readonly HashSet<string> _clavesVistas = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (double X, double Y)> _codigosVistos = new();

    public ResultadoFila Transformar(RegistroShapefile r)
    {
        int fila = r.Fid;
        var geometria = PrepararGeometria(r.Geometria, fila, out bool reparada);
        if (geometria is null) return new(EstadoFila.Fallida, null, false);

        var valores = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        bool conErrores = false;
        foreach (var m in definicion.Mapeo)
        {
            object? origen = m.Origen switch
            {
                OrigenValor.Campo => r.Atributos.TryGetValue(m.CampoOrigen!, out var v) ? v : null,
                OrigenValor.Fid => r.Fid,
                OrigenValor.CoordenadaX => geometria.Coordinate.X,
                OrigenValor.CoordenadaY => geometria.Coordinate.Y,
                OrigenValor.Constante => m.Constante,
                _ => null
            };
            if (Convertir(m, origen, fila, out var convertido)) valores[m.ColumnaDestino] = convertido;
            else conErrores = true;
        }
        if (conErrores) return new(EstadoFila.Fallida, null, reparada);

        if (definicion.Id == DefinicionesMigracion.CodigosFijos.Id)
            RevisarCodigoFijo(r, valores, (Point)geometria, fila);

        var clave = DefinicionesMigracion.ClaveNatural(definicion, valores);
        if (!_clavesVistas.Add(clave))
        {
            incidencias.Agregar(NivelIncidencia.Advertencia, "DUPLICADO_EN_ARCHIVO",
                $"Registro repetido por clave natural ({definicion.DescripcionClave}); se omite.", fila, "Clave", clave);
            return new(EstadoFila.Duplicada, null, reparada);
        }
        return new(EstadoFila.Valida, new FilaDestino { Fid = r.Fid, Valores = valores, Geometria = geometria, ClaveNatural = clave }, reparada);
    }

    private Geometry? PrepararGeometria(Geometry? g, int fila, out bool reparada)
    {
        reparada = false;
        if (g is null || g.IsEmpty)
        {
            incidencias.Agregar(NivelIncidencia.Error, "GEOMETRIA_VACIA", "Registro sin geometría; se rechaza.", fila);
            return null;
        }
        if (g is MultiPoint { NumGeometries: 1 } mp && definicion.TiposGeometria.Contains("Point"))
            g = mp.GetGeometryN(0);
        if (!definicion.TiposGeometria.Contains(g.GeometryType))
        {
            incidencias.Agregar(NivelIncidencia.Error, "GEOMETRIA_TIPO",
                $"Tipo {g.GeometryType} no compatible con {definicion.Tabla} ({string.Join("/", definicion.TiposGeometria)}).", fila);
            return null;
        }
        var e = g.EnvelopeInternal;
        if (e.MinX < -180 || e.MaxX > 180 || e.MinY < -90 || e.MaxY > 90)
        {
            incidencias.Agregar(NivelIncidencia.Error, "COORDENADAS_FUERA_DE_RANGO",
                "Coordenadas fuera del rango WGS 84 (¿datos proyectados en metros?); se rechaza.", fila,
                valor: string.Create(CultureInfo.InvariantCulture, $"{e.MinX:0.###},{e.MinY:0.###}"));
            return null;
        }
        if (!g.IsValid)
        {
            var causa = new IsValidOp(g).ValidationError;
            var corregida = GeometryFixer.Fix(g);
            if (corregida.IsEmpty || !definicion.TiposGeometria.Contains(corregida.GeometryType))
            {
                incidencias.Agregar(NivelIncidencia.Error, "GEOMETRIA_IRREPARABLE",
                    $"Geometría inválida ({causa?.Message}) que no se pudo reparar; se rechaza.", fila);
                return null;
            }
            incidencias.Agregar(NivelIncidencia.Advertencia, "GEOMETRIA_REPARADA",
                $"Geometría inválida reparada con GeometryFixer (causa: {causa?.Message} en {causa?.Coordinate}).", fila);
            g = corregida;
            reparada = true;
        }
        g.SRID = Srid;
        return g;
    }

    private bool Convertir(MapeoCampo m, object? valor, int fila, out object? resultado)
    {
        resultado = null;
        try
        {
            switch (m.Tipo)
            {
                case TipoDestino.Texto:
                    var texto = valor switch
                    {
                        null => null,
                        double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
                        decimal d => d.ToString("0.##########", CultureInfo.InvariantCulture),
                        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                        _ => valor.ToString()
                    };
                    texto = texto?.Trim();
                    if (string.IsNullOrEmpty(texto)) return true;
                    if (texto.Length > m.Longitud)
                    {
                        incidencias.Agregar(NivelIncidencia.Error, "LONGITUD_EXCEDIDA",
                            $"El valor supera NVARCHAR({m.Longitud}) de {m.ColumnaDestino}.", fila, m.CampoOrigen, Recortar(texto));
                        return false;
                    }
                    resultado = texto;
                    return true;

                case TipoDestino.Entero:
                    if (valor is null || valor is string { Length: 0 }) return true;
                    var numero = Convert.ToDecimal(valor, CultureInfo.InvariantCulture);
                    if (numero != decimal.Truncate(numero) || numero < int.MinValue || numero > int.MaxValue)
                    {
                        incidencias.Agregar(NivelIncidencia.Error, "CONVERSION_ENTERO",
                            $"{m.ColumnaDestino} espera un entero (INT).", fila, m.CampoOrigen, Convert.ToString(valor, CultureInfo.InvariantCulture));
                        return false;
                    }
                    resultado = (int)numero;
                    return true;

                case TipoDestino.TinyInt:
                    resultado = Convert.ToByte(valor, CultureInfo.InvariantCulture);
                    return true;

                case TipoDestino.Flotante:
                    if (valor is null) return true;
                    resultado = Convert.ToDouble(valor, CultureInfo.InvariantCulture);
                    return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            incidencias.Agregar(NivelIncidencia.Error, "CONVERSION",
                $"No se pudo convertir a {m.DescripcionTipo} ({ex.Message}).", fila, m.CampoOrigen, Recortar(Convert.ToString(valor, CultureInfo.InvariantCulture)));
        }
        return false;
    }

    /// <summary>Controles de calidad propios de los códigos fijos (coherencia DBF ↔ geometría y códigos repetidos).</summary>
    private void RevisarCodigoFijo(RegistroShapefile r, Dictionary<string, object?> valores, Point p, int fila)
    {
        if (r.Atributos.TryGetValue("Latid", out var latid) && latid is not null
            && Math.Abs(Convert.ToDouble(latid, CultureInfo.InvariantCulture) - p.Y) > 1e-4)
            incidencias.Agregar(NivelIncidencia.Advertencia, "LATID_INCOHERENTE",
                "El campo Latid del DBF no coincide con la Y de la geometría; se usa la geometría.", fila, "Latid",
                Convert.ToString(latid, CultureInfo.InvariantCulture));
        if (r.Atributos.TryGetValue("Longi", out var longi) && longi is not null
            && Math.Abs(Convert.ToDouble(longi, CultureInfo.InvariantCulture) - p.X) > 1e-3)
            incidencias.Agregar(NivelIncidencia.Advertencia, "LONGI_INCOHERENTE",
                "El campo Longi del DBF no coincide con la X de la geometría; se usa la geometría.", fila, "Longi",
                Convert.ToString(longi, CultureInfo.InvariantCulture));

        if (valores["CodFijo"] is int codigo)
        {
            if (_codigosVistos.TryGetValue(codigo, out var previo))
            {
                double metros = Math.Sqrt(Math.Pow((p.X - previo.X) * 106_700, 2) + Math.Pow((p.Y - previo.Y) * 110_600, 2));
                if (metros > 1)
                    incidencias.Agregar(NivelIncidencia.Advertencia, "CODFIJO_REPETIDO",
                        $"CodFijo repetido en otra ubicación (a {metros:0} m de la anterior); se carga ambos.", fila, "CodFijo",
                        codigo.ToString(CultureInfo.InvariantCulture));
            }
            else _codigosVistos[codigo] = (p.X, p.Y);
        }
    }

    private static string? Recortar(string? s) => s is { Length: > 200 } ? s[..200] : s;
}
