using VisorDatosSIG.Application.Comun;

namespace VisorDatosSIG.Application.Rutas;

/// <summary>Código fijo que puede entrar en una ruta.</summary>
public sealed record ParadaCandidata(int Id, Coordenada Ubicacion);

/// <summary>Parada en el orden calculado. Las distancias son por calle, en metros.</summary>
public sealed record ParadaPlanificada(
    int Id, int Orden, Coordenada Ubicacion, Coordenada EnRed, double DistanciaAcceso,
    double DistanciaTramo, double DistanciaAcumulada, IReadOnlyList<Coordenada> Trazado);

public sealed record ParadaExcluida(int Id, string Motivo);

/// <summary>Ruta ordenada: paradas con el trazado de cada tramo por las calles y las paradas que no se pudieron ubicar.</summary>
public sealed record PlanRuta(
    Coordenada Partida, Coordenada PartidaEnRed, IReadOnlyList<ParadaPlanificada> Paradas,
    double DistanciaTotal, IReadOnlyList<ParadaExcluida> Excluidas)
{
    /// <summary>Recorrido completo por la red, de la partida a la última parada.</summary>
    public IReadOnlyList<Coordenada> Trazado =>
        [.. Paradas.SelectMany((p, i) => i == 0 ? p.Trazado : p.Trazado.Skip(1))];
}

/// <summary>
/// Calcula la «ruta óptima» de una asignación sobre la red vial:
/// 1) elige las <c>N</c> candidatas más cercanas al punto de partida por calle (un Dijkstra);
/// 2) arma la matriz de distancias por red entre todas las paradas (un Dijkstra por parada);
/// 3) ordena con el vecino más cercano y mejora con 2-opt (camino abierto, la partida fija).
/// Las paradas obligatorias (pendientes del trabajador) siempre entran: la ruta se recalcula acumulada.
/// </summary>
public sealed class PlanificadorRuta(RedVial red)
{
    /// <summary>Distancia máxima de una casa a la calle para incluirla (el 99 % de los códigos está a menos de 200 m).</summary>
    public const double MaximoAccesoParada = 500;
    public const double MaximoAccesoPartida = 1000;

    public PlanRuta Planificar(Coordenada partida, IReadOnlyList<ParadaCandidata> obligatorias,
        IReadOnlyList<ParadaCandidata> candidatas, int cantidadNuevas)
    {
        var origen = red.Ajustar(partida, MaximoAccesoPartida)
            ?? throw new ErrorValidacion($"El punto de partida está a más de {MaximoAccesoPartida:0} m de una vía. Márquelo sobre una calle.", "partida");
        var excluidas = new List<ParadaExcluida>();
        var arbolPartida = red.Dijkstra(origen);

        var paradas = new List<(ParadaCandidata Parada, PuntoEnRed EnRed)>();
        foreach (var o in obligatorias)
        {
            if (Ubicar(o, excluidas) is { } p) paradas.Add((o, p));
        }
        var yaIncluidas = paradas.Select(p => p.Parada.Id).ToHashSet();
        var nuevas = candidatas
            .Where(c => !yaIncluidas.Contains(c.Id))
            .Select(c => (Parada: c, EnRed: Ubicar(c, excluidas)))
            .Where(c => c.EnRed is not null)
            .Select(c => (c.Parada, EnRed: c.EnRed!.Value, Distancia: arbolPartida.DistanciaA(c.EnRed!.Value)))
            .Where(c => !double.IsPositiveInfinity(c.Distancia))
            .OrderBy(c => c.Distancia).ThenBy(c => c.Parada.Id)
            .Take(Math.Max(0, cantidadNuevas));
        paradas.AddRange(nuevas.Select(n => (n.Parada, n.EnRed)));
        if (paradas.Count == 0) return new PlanRuta(partida, origen.Proyectado, [], 0, excluidas);

        // Matriz de distancias por red: índice 0 = partida, 1..n = paradas.
        var puntos = new List<PuntoEnRed> { origen };
        puntos.AddRange(paradas.Select(p => p.EnRed));
        var arboles = new ArbolCaminos[puntos.Count];
        arboles[0] = arbolPartida;
        for (int i = 1; i < puntos.Count; i++) arboles[i] = red.Dijkstra(puntos[i]);
        var d = new double[puntos.Count, puntos.Count];
        for (int i = 0; i < puntos.Count; i++)
            for (int j = 0; j < puntos.Count; j++)
                d[i, j] = i == j ? 0 : arboles[i].DistanciaA(puntos[j]);

        var orden = VecinoMasCercano(d, puntos.Count);
        DosOpt(orden, d);

        var resultado = new List<ParadaPlanificada>();
        double acumulada = 0;
        for (int k = 1; k < orden.Count; k++)
        {
            int desde = orden[k - 1], hasta = orden[k];
            var (parada, enRed) = paradas[hasta - 1];
            double tramo = d[desde, hasta];
            acumulada += tramo;
            resultado.Add(new ParadaPlanificada(parada.Id, k, parada.Ubicacion, enRed.Proyectado, enRed.DistanciaAcceso,
                tramo, acumulada, arboles[desde].CaminoA(enRed)));
        }
        return new PlanRuta(partida, origen.Proyectado, resultado, acumulada, excluidas);
    }

    private PuntoEnRed? Ubicar(ParadaCandidata parada, List<ParadaExcluida> excluidas)
    {
        var enRed = red.Ajustar(parada.Ubicacion, MaximoAccesoParada);
        if (enRed is null)
            excluidas.Add(new ParadaExcluida(parada.Id, $"Está a más de {MaximoAccesoParada:0} m de una vía de la red principal."));
        return enRed;
    }

    /// <summary>Recorrido inicial: siempre a la parada pendiente más cercana por calle.</summary>
    internal static List<int> VecinoMasCercano(double[,] d, int n)
    {
        var orden = new List<int> { 0 };
        var pendientes = Enumerable.Range(1, n - 1).ToHashSet();
        while (pendientes.Count > 0)
        {
            int actual = orden[^1];
            int siguiente = pendientes.MinBy(j => (d[actual, j], j));
            orden.Add(siguiente);
            pendientes.Remove(siguiente);
        }
        return orden;
    }

    /// <summary>Mejora 2-opt para un camino abierto: invierte subtramos mientras acorten el total. La partida no se mueve.</summary>
    internal static void DosOpt(List<int> orden, double[,] d)
    {
        int n = orden.Count;
        bool mejoro = true;
        for (int vuelta = 0; mejoro && vuelta < 100; vuelta++)
        {
            mejoro = false;
            for (int i = 1; i < n - 1; i++)
                for (int k = i + 1; k < n; k++)
                {
                    int antes = orden[i - 1], ini = orden[i], fin = orden[k];
                    double actual = d[antes, ini], propuesto = d[antes, fin];
                    if (k + 1 < n)
                    {
                        int despues = orden[k + 1];
                        actual += d[fin, despues];
                        propuesto += d[ini, despues];
                    }
                    if (propuesto < actual - 1e-6)
                    {
                        orden.Reverse(i, k - i + 1);
                        mejoro = true;
                    }
                }
        }
    }
}
