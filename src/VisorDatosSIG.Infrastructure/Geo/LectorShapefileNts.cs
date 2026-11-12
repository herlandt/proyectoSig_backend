using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Migracion;

namespace VisorDatosSIG.Infrastructure.Geo;

/// <summary>Lectura de Shapefile con NetTopologySuite.IO.Esri (SHP + DBF, codificación desde el .cpg).</summary>
public sealed class LectorShapefileNts : ILectorShapefile
{
    // Las geometrías inválidas se entregan tal cual: el TransformadorCapa las valida,
    // repara con una regla documentada y lo registra en la bitácora.
    private static ShapefileReaderOptions Opciones() => new() { GeometryBuilderMode = GeometryBuilderMode.IgnoreInvalidShapes };

    public InfoShapefile Inspeccionar(ArchivosShapefile archivos)
    {
        if (archivos.Shp is null) throw new ErrorValidacion($"Falta el archivo {archivos.Nombre}.shp.");
        using var lector = Shapefile.OpenRead(archivos.Shp, Opciones());
        var campos = new List<CampoDbf>();
        for (int i = 0; i < lector.Fields.Count; i++)
        {
            var f = lector.Fields[i];
            campos.Add(new CampoDbf(f.Name, f.FieldType.ToString(), f.Length, f.NumericScale));
        }
        string? prj = archivos.Prj is null ? null : File.ReadAllText(archivos.Prj).Trim();
        return new InfoShapefile(
            lector.ShapeType.ToString(),
            lector.ShapeType.HasZ(),
            lector.RecordCount,
            campos,
            Extension.DesdeEnvelope(lector.BoundingBox),
            prj,
            lector.Encoding?.WebName ?? "desconocida");
    }

    public IEnumerable<RegistroShapefile> Leer(ArchivosShapefile archivos, CancellationToken ct = default)
    {
        if (archivos.Shp is null) throw new ErrorValidacion($"Falta el archivo {archivos.Nombre}.shp.");
        using var lector = Shapefile.OpenRead(archivos.Shp, Opciones());
        int fid = 0;
        while (lector.Read(out bool eliminado))
        {
            ct.ThrowIfCancellationRequested();
            if (!eliminado)
            {
                var atributos = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < lector.Fields.Count; i++)
                    atributos[lector.Fields[i].Name] = lector.Fields[i].Value;
                yield return new RegistroShapefile(fid, lector.Geometry, atributos);
            }
            fid++;
        }
    }
}
