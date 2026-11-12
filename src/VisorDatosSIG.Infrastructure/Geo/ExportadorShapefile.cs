using System.Globalization;
using System.Text;
using Dapper;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Writers;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Datos;

namespace VisorDatosSIG.Infrastructure.Geo;

/// <summary>
/// Exporta las cuatro tablas de SQL Server a Shapefile (WGS 84) para revisarlas en ArcMap/QGIS:
/// permite comprobar la migración de ida y vuelta (conteos, vínculos y coordenadas).
/// Los nombres de campo respetan el límite de 10 caracteres del DBF.
/// </summary>
public sealed class ExportadorShapefile(FabricaConexion fabrica)
{
    public const string PrjWgs84 =
        "GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]]," +
        "PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]]";

    public async Task<IReadOnlyDictionary<string, int>> ExportarAsync(string carpeta, CancellationToken ct)
    {
        Directory.CreateDirectory(carpeta);
        var conteos = new Dictionary<string, int>();

        conteos["SQL_Manzanas"] = await ExportarTablaAsync(carpeta, "SQL_Manzanas", ShapeType.Polygon, """
            SELECT m.IdManzana, m.IdOrigen, m.UV_MZA, m.UV, m.MZA,
                   (SELECT COUNT(*) FROM dbo.Lotes l WHERE l.IdManzana = m.IdManzana) AS NumLotes,
                   m.Geom.STAsBinary() AS Wkb
              FROM dbo.Manzanas m ORDER BY m.IdManzana
            """, o =>
        {
            o.AddNumericInt32Field("IdManzana"); o.AddNumericInt32Field("IdOrigen");
            o.AddCharacterField("UV_MZA", 20); o.AddCharacterField("UV", 15); o.AddCharacterField("MZA", 10);
            o.AddNumericInt32Field("NumLotes");
        }, ct);

        conteos["SQL_Lotes"] = await ExportarTablaAsync(carpeta, "SQL_Lotes", ShapeType.Polygon, """
            SELECT l.IdLote, l.IdOrigen, l.NroLote, l.IdManzana, m.UV_MZA,
                   (SELECT COUNT(*) FROM dbo.CodigosFijos c WHERE c.IdLote = l.IdLote) AS NumCodigos,
                   l.Geom.STAsBinary() AS Wkb
              FROM dbo.Lotes l LEFT JOIN dbo.Manzanas m ON m.IdManzana = l.IdManzana ORDER BY l.IdLote
            """, o =>
        {
            o.AddNumericInt32Field("IdLote"); o.AddNumericInt32Field("IdOrigen"); o.AddCharacterField("NroLote", 15);
            o.AddNumericInt32Field("IdManzana"); o.AddCharacterField("UV_MZA", 20); o.AddNumericInt32Field("NumCodigos");
        }, ct);

        conteos["SQL_CodigosFijos"] = await ExportarTablaAsync(carpeta, "SQL_CodigosFijos", ShapeType.Point, """
            SELECT c.IdCodigo, c.CodF_SQL, c.CodF_SIG, c.CodFijo, c.Nombre, c.Estado,
                   CONVERT(NVARCHAR(19), c.FechaCambioEstado, 120) AS FechaEstad, c.IdLote, l.NroLote,
                   c.Longitud, c.Latitud, c.Geom.STAsBinary() AS Wkb
              FROM dbo.CodigosFijos c LEFT JOIN dbo.Lotes l ON l.IdLote = c.IdLote ORDER BY c.IdCodigo
            """, o =>
        {
            o.AddNumericInt32Field("IdCodigo"); o.AddNumericInt32Field("CodF_SQL"); o.AddCharacterField("CodF_SIG", 25);
            o.AddNumericInt32Field("CodFijo"); o.AddCharacterField("Nombre", 120); o.AddNumericInt32Field("Estado");
            o.AddCharacterField("EstadoDesc", 15); o.AddCharacterField("FechaEstad", 19); o.AddNumericInt32Field("IdLote");
            o.AddCharacterField("NroLote", 15); o.AddNumericDoubleField("Longitud"); o.AddNumericDoubleField("Latitud");
        }, ct, fila => fila["EstadoDesc"] = EstadoServicioExtensiones.Descripcion(Convert.ToInt32(fila["Estado"], CultureInfo.InvariantCulture)));

        conteos["SQL_Vias"] = await ExportarTablaAsync(carpeta, "SQL_Vias", ShapeType.PolyLine, """
            SELECT v.IdVia, v.OBJECTID, v.Nombre, v.TipoVia, v.OSMID, v.Geom.STAsBinary() AS Wkb
              FROM dbo.Vias v ORDER BY v.IdVia
            """, o =>
        {
            o.AddNumericInt32Field("IdVia"); o.AddNumericInt32Field("OBJECTID"); o.AddCharacterField("Nombre", 40);
            o.AddCharacterField("TipoVia", 30); o.AddCharacterField("OSMID", 20);
        }, ct);

        return conteos;
    }

    private async Task<int> ExportarTablaAsync(string carpeta, string nombre, ShapeType tipo, string sql,
        Action<ShapefileWriterOptions> campos, CancellationToken ct, Action<IDictionary<string, object?>>? derivados = null)
    {
        await using var cn = fabrica.Crear();
        var filas = (await cn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: 300)))
            .Cast<IDictionary<string, object>>().ToList();

        var opciones = new ShapefileWriterOptions(tipo) { Projection = PrjWgs84, Encoding = new UTF8Encoding(false) };
        campos(opciones);
        var ruta = Path.Combine(carpeta, nombre + ".shp");
        using (var escritor = Shapefile.OpenWrite(ruta, opciones))
        {
            foreach (var f in filas)
            {
                var valores = f.ToDictionary(kv => kv.Key, kv => kv.Value is DBNull ? null : (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
                derivados?.Invoke(valores);
                escritor.Geometry = GeoJsonEscritor.DesdeWkb(valores["Wkb"] as byte[]);
                for (int i = 0; i < escritor.Fields.Count; i++)
                {
                    var campo = escritor.Fields[i];
                    campo.Value = valores.TryGetValue(campo.Name, out var v) ? v : null;
                }
                escritor.Write();
            }
        }
        // .prj en ASCII sin BOM (ArcMap no reconoce la proyección si el archivo empieza con BOM)
        // y codificación declarada para ArcMap/QGIS (los nombres de titulares llevan tildes y Ñ).
        await File.WriteAllTextAsync(Path.Combine(carpeta, nombre + ".prj"), PrjWgs84, Encoding.ASCII, ct);
        await File.WriteAllTextAsync(Path.Combine(carpeta, nombre + ".cpg"), "UTF-8", Encoding.ASCII, ct);
        return filas.Count;
    }
}
