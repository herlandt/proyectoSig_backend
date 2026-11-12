using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Application.Operaciones;

/// <summary>Recorrido de un trabajador en un día: posiciones GPS, el mismo recorrido ajustado a las calles y las paradas registradas.</summary>
public sealed record RecorridoTrabajador(
    int IdTrabajador, string Nombre, IReadOnlyList<PosicionGps> Posiciones,
    IReadOnlyList<Coordenada> TrazadoEnRed, double DistanciaEnRedMetros, IReadOnlyList<ParadaRealizada> Paradas);

/// <summary>
/// Monitoreo del personal: posición en vivo y reproducción del recorrido del día. El recorrido se ajusta a la
/// red vial uniendo posiciones consecutivas con Dijkstra, así la distancia recorrida se mide por las calles.
/// </summary>
public sealed class MonitoreoServicio(IOperacionesRepositorio repositorio, IRedVialProveedor redes)
{
    /// <summary>Posiciones a menos de esta distancia de la anterior se consideran el mismo lugar (trabajador detenido).</summary>
    public const double MinimoEntrePosiciones = 15;
    public const double MaximoAjuste = 150;

    public Task<IReadOnlyList<UltimaPosicion>> EnVivoAsync(CancellationToken ct) =>
        repositorio.ListarUltimasPosicionesAsync(DateTime.Today, ct);

    public Task<ResumenOperativo> ResumenAsync(DateOnly dia, CancellationToken ct) => repositorio.ObtenerResumenAsync(dia, ct);

    public async Task<IReadOnlyList<RecorridoTrabajador>> RecorridosAsync(DateOnly fecha, int? idTrabajador, string? tipo, CancellationToken ct)
    {
        TipoAsignacion? filtro = string.IsNullOrWhiteSpace(tipo) || tipo.Equals("todas", StringComparison.OrdinalIgnoreCase)
            ? null
            : TiposAsignacion.Parsear(tipo) ?? throw new ErrorValidacion("Actividad no válida: use Corte, Reconexión o Todas.", "actividad");
        var posiciones = await repositorio.ListarPosicionesAsync(fecha, idTrabajador, filtro, ct);
        var paradas = await repositorio.ListarRealizadasAsync(fecha, idTrabajador, filtro, ct);
        var red = await redes.ObtenerAsync(ct);

        var nombres = posiciones.Select(p => (p.IdTrabajador, p.Nombre)).Distinct().ToDictionary(x => x.IdTrabajador, x => x.Nombre);
        var trabajadores = posiciones.Select(p => p.IdTrabajador).Concat(paradas.Select(p => p.IdTrabajador)).Distinct();
        var resultado = new List<RecorridoTrabajador>();
        foreach (int id in trabajadores)
        {
            var propias = posiciones.Where(p => p.IdTrabajador == id).Select(p => p.Posicion).OrderBy(p => p.FechaHora).ToList();
            var (trazado, distancia) = AjustarARed(red, propias);
            resultado.Add(new RecorridoTrabajador(
                id, nombres.GetValueOrDefault(id, $"Trabajador {id}"), propias, trazado, distancia,
                [.. paradas.Where(p => p.IdTrabajador == id).Select(p => p.Parada).OrderBy(p => p.FechaEjecucion)]));
        }
        return resultado;
    }

    /// <summary>Une posiciones consecutivas por el camino más corto de la red; si una no se puede ajustar, se une en línea recta.</summary>
    internal static (IReadOnlyList<Coordenada> Trazado, double Distancia) AjustarARed(RedVial red, IReadOnlyList<PosicionGps> posiciones)
    {
        var puntos = new List<Coordenada>();
        foreach (var p in posiciones)
            if (puntos.Count == 0 || RedVial.Distancia(puntos[^1], p.Ubicacion) >= MinimoEntrePosiciones)
                puntos.Add(p.Ubicacion);
        if (puntos.Count < 2) return (puntos, 0);

        var trazado = new List<Coordenada>();
        double distancia = 0;
        PuntoEnRed? anterior = red.Ajustar(puntos[0], MaximoAjuste);
        trazado.Add(anterior?.Proyectado ?? puntos[0]);
        for (int i = 1; i < puntos.Count; i++)
        {
            var actual = red.Ajustar(puntos[i], MaximoAjuste);
            IReadOnlyList<Coordenada> tramo = [];
            if (anterior is { } a && actual is { } b)
            {
                var arbol = red.Dijkstra(a);
                double d = arbol.DistanciaA(b);
                // Si por calle sale más de 4 veces la recta, el GPS saltó: se une en línea recta.
                if (!double.IsPositiveInfinity(d) && d <= 4 * RedVial.Distancia(a.Proyectado, b.Proyectado) + 30)
                {
                    tramo = arbol.CaminoA(b);
                    distancia += d;
                }
            }
            if (tramo.Count == 0)
            {
                var destino = actual?.Proyectado ?? puntos[i];
                distancia += RedVial.Distancia(trazado[^1], destino);
                tramo = [trazado[^1], destino];
            }
            trazado.AddRange(tramo.Skip(1));
            anterior = actual;
        }
        return (trazado, distancia);
    }
}
