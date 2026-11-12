using Dapper;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Infrastructure.Datos;

namespace VisorDatosSIG.Infrastructure.Geo;

/// <summary>
/// Arma la red vial con las líneas de dbo.Vias y la conserva en memoria (singleton). Se vuelve a armar
/// solo si cambió la fecha de la última migración y pasaron al menos cinco minutos desde la última revisión.
/// </summary>
public sealed class RedVialProveedor(FabricaConexion fabrica) : IRedVialProveedor
{
    private static readonly TimeSpan Revision = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _candado = new(1, 1);
    private RedVial? _red;
    private DateTime? _versionDatos;
    private DateTime _ultimaRevision;

    public async Task<RedVial> ObtenerAsync(CancellationToken ct)
    {
        if (_red is not null && DateTime.UtcNow - _ultimaRevision < Revision) return _red;
        await _candado.WaitAsync(ct);
        try
        {
            if (_red is not null && DateTime.UtcNow - _ultimaRevision < Revision) return _red;
            await using var cn = fabrica.Crear();
            var version = await cn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(FechaFin) FROM dbo.BitacoraMigracion WHERE Estado = N'Completada'", cancellationToken: ct));
            if (_red is null || version != _versionDatos)
            {
                var wkbs = await cn.QueryAsync<byte[]>(new CommandDefinition(
                    "SELECT Geom.STAsBinary() FROM dbo.Vias WHERE Geom IS NOT NULL", cancellationToken: ct, commandTimeout: 120));
                _red = Construir(wkbs);
                _versionDatos = version;
            }
            _ultimaRevision = DateTime.UtcNow;
            return _red;
        }
        finally
        {
            _candado.Release();
        }
    }

    internal static RedVial Construir(IEnumerable<byte[]> wkbs)
    {
        var lector = new WKBReader();
        var lineas = new List<IReadOnlyList<Coordenada>>();
        foreach (var wkb in wkbs)
        {
            var geometria = lector.Read(wkb);
            for (int i = 0; i < geometria.NumGeometries; i++)
                if (geometria.GetGeometryN(i) is LineString linea && linea.NumPoints > 1)
                    lineas.Add([.. linea.Coordinates.Select(c => new Coordenada(c.X, c.Y))]);
        }
        return new RedVial(lineas);
    }
}
