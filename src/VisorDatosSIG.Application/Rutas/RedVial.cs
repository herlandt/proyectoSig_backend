namespace VisorDatosSIG.Application.Rutas;

/// <summary>Punto geográfico WGS 84 (X = longitud, Y = latitud).</summary>
public readonly record struct Coordenada(double Lon, double Lat);

/// <summary>Punto ajustado a la red: tramo, posición relativa en el tramo (0..1) y distancia desde el punto original.</summary>
public readonly record struct PuntoEnRed(int Tramo, double T, Coordenada Proyectado, double DistanciaAcceso);

/// <summary>
/// Red vial para calcular rutas con Dijkstra, armada con las líneas de dbo.Vias. Cada vértice es un nodo
/// (los vértices que coinciden a 7 decimales son el mismo nodo: así se unen las vías en las intersecciones)
/// y cada segmento es un tramo con su longitud geodésica. Los puntos solo se ajustan a la componente conexa
/// mayor, de modo que toda ruta es alcanzable y siempre va por las calles, sin atravesar manzanas.
/// </summary>
public sealed class RedVial
{
    private const double RadioTierra = 6371008.8;
    private const double MetrosPorGrado = Math.PI / 180 * RadioTierra;
    private const double TamCelda = 0.002;   // grados (~220 m): índice de tramos para el ajuste

    private readonly Coordenada[] _nodos;
    private readonly (int A, int B, double Largo)[] _tramos;
    private readonly List<(int Vecino, int Tramo)>[] _ady;
    private readonly Dictionary<(int, int), List<int>> _celdas = [];

    public int Nodos => _nodos.Length;
    public int Tramos => _tramos.Length;
    public double LongitudTotal { get; }
    public int Componentes { get; }
    public int NodosComponentePrincipal { get; }

    public RedVial(IEnumerable<IReadOnlyList<Coordenada>> lineas)
    {
        var indice = new Dictionary<(long, long), int>();
        var nodos = new List<Coordenada>();
        var tramos = new List<(int, int, double)>();
        var vistos = new HashSet<(int, int)>();
        int Nodo(Coordenada c)
        {
            var clave = ((long)Math.Round(c.Lon * 1e7), (long)Math.Round(c.Lat * 1e7));
            if (!indice.TryGetValue(clave, out int i))
            {
                i = nodos.Count;
                indice[clave] = i;
                nodos.Add(c);
            }
            return i;
        }
        foreach (var linea in lineas)
            for (int i = 1; i < linea.Count; i++)
            {
                int a = Nodo(linea[i - 1]), b = Nodo(linea[i]);
                // Un segmento repetido en dos vías se cuenta una sola vez.
                if (a != b && vistos.Add((Math.Min(a, b), Math.Max(a, b))))
                    tramos.Add((a, b, Distancia(nodos[a], nodos[b])));
            }

        _nodos = [.. nodos];
        _tramos = [.. tramos];
        _ady = new List<(int, int)>[_nodos.Length];
        for (int i = 0; i < _ady.Length; i++) _ady[i] = [];
        for (int t = 0; t < _tramos.Length; t++)
        {
            var (a, b, _) = _tramos[t];
            _ady[a].Add((b, t));
            _ady[b].Add((a, t));
        }
        LongitudTotal = _tramos.Sum(t => t.Largo);

        // Componentes conexas: solo la mayor entra al índice de ajuste.
        var componente = new int[_nodos.Length];
        Array.Fill(componente, -1);
        var tamanos = new List<int>();
        for (int inicio = 0; inicio < _nodos.Length; inicio++)
        {
            if (componente[inicio] >= 0) continue;
            int id = tamanos.Count, tam = 0;
            var pila = new Stack<int>([inicio]);
            componente[inicio] = id;
            while (pila.TryPop(out int u))
            {
                tam++;
                foreach (var (v, _) in _ady[u])
                    if (componente[v] < 0) { componente[v] = id; pila.Push(v); }
            }
            tamanos.Add(tam);
        }
        Componentes = tamanos.Count;
        if (tamanos.Count == 0) return;
        int principal = tamanos.IndexOf(tamanos.Max());
        NodosComponentePrincipal = tamanos[principal];
        for (int t = 0; t < _tramos.Length; t++)
        {
            if (componente[_tramos[t].A] != principal) continue;
            var (a, b) = (_nodos[_tramos[t].A], _nodos[_tramos[t].B]);
            for (int cx = Celda(Math.Min(a.Lon, b.Lon)); cx <= Celda(Math.Max(a.Lon, b.Lon)); cx++)
                for (int cy = Celda(Math.Min(a.Lat, b.Lat)); cy <= Celda(Math.Max(a.Lat, b.Lat)); cy++)
                {
                    if (!_celdas.TryGetValue((cx, cy), out var lista)) _celdas[(cx, cy)] = lista = [];
                    lista.Add(t);
                }
        }
    }

    /// <summary>Distancia geodésica (haversine) en metros.</summary>
    public static double Distancia(Coordenada p, Coordenada q)
    {
        double f1 = p.Lat * Math.PI / 180, f2 = q.Lat * Math.PI / 180;
        double df = f2 - f1, dl = (q.Lon - p.Lon) * Math.PI / 180;
        double h = Math.Sin(df / 2) * Math.Sin(df / 2) + Math.Cos(f1) * Math.Cos(f2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * RadioTierra * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public Coordenada CoordenadaNodo(int nodo) => _nodos[nodo];

    /// <summary>Proyecta el punto sobre el tramo más cercano de la red principal; null si queda a más de <paramref name="maximoMetros"/>.</summary>
    public PuntoEnRed? Ajustar(Coordenada p, double maximoMetros)
    {
        double cos = Math.Cos(p.Lat * Math.PI / 180);
        double metrosCelda = TamCelda * MetrosPorGrado * Math.Min(1, cos);
        int anillos = (int)Math.Ceiling(maximoMetros / metrosCelda) + 1;
        int cx = Celda(p.Lon), cy = Celda(p.Lat);
        PuntoEnRed? mejor = null;
        var revisados = new HashSet<int>();
        for (int r = 0; r <= anillos; r++)
        {
            // Lo que haya más allá de este anillo está, como mínimo, a (r - 1) celdas.
            if (mejor is { } m && m.DistanciaAcceso < (r - 1) * metrosCelda) break;
            for (int x = cx - r; x <= cx + r; x++)
                for (int y = cy - r; y <= cy + r; y++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != r || !_celdas.TryGetValue((x, y), out var lista)) continue;
                    foreach (int t in lista)
                    {
                        if (!revisados.Add(t)) continue;
                        var candidato = Proyectar(p, t, cos);
                        if (mejor is null || candidato.DistanciaAcceso < mejor.Value.DistanciaAcceso) mejor = candidato;
                    }
                }
        }
        return mejor is { } final && final.DistanciaAcceso <= maximoMetros ? final : null;
    }

    private PuntoEnRed Proyectar(Coordenada p, int tramo, double cos)
    {
        var (ia, ib, _) = _tramos[tramo];
        Coordenada a = _nodos[ia], b = _nodos[ib];
        // Plano local en metros con origen en p (precisión de centímetros a esta escala).
        double ax = (a.Lon - p.Lon) * cos * MetrosPorGrado, ay = (a.Lat - p.Lat) * MetrosPorGrado;
        double bx = (b.Lon - p.Lon) * cos * MetrosPorGrado, by = (b.Lat - p.Lat) * MetrosPorGrado;
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / l2, 0, 1);
        double px = ax + t * dx, py = ay + t * dy;
        var proyectado = new Coordenada(a.Lon + t * (b.Lon - a.Lon), a.Lat + t * (b.Lat - a.Lat));
        return new PuntoEnRed(tramo, t, proyectado, Math.Sqrt(px * px + py * py));
    }

    /// <summary>Dijkstra desde un punto de la red: distancias mínimas por calle a todos los nodos.</summary>
    public ArbolCaminos Dijkstra(PuntoEnRed origen)
    {
        var dist = new double[_nodos.Length];
        var previo = new int[_nodos.Length];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(previo, -1);
        var (a, b, largo) = _tramos[origen.Tramo];
        var cola = new PriorityQueue<int, double>();
        dist[a] = origen.T * largo;
        dist[b] = (1 - origen.T) * largo;
        previo[a] = previo[b] = ArbolCaminos.DesdeOrigen;
        cola.Enqueue(a, dist[a]);
        cola.Enqueue(b, dist[b]);
        while (cola.TryDequeue(out int u, out double du))
        {
            if (du > dist[u]) continue;   // entrada vieja de la cola
            foreach (var (v, t) in _ady[u])
            {
                double nueva = du + _tramos[t].Largo;
                if (nueva < dist[v])
                {
                    dist[v] = nueva;
                    previo[v] = u;
                    cola.Enqueue(v, nueva);
                }
            }
        }
        return new ArbolCaminos(this, origen, dist, previo);
    }

    internal (int A, int B, double Largo) Tramo(int tramo) => _tramos[tramo];

    private static int Celda(double grados) => (int)Math.Floor(grados / TamCelda);
}

/// <summary>Resultado de un Dijkstra: distancia y camino por calle desde el origen hasta cualquier punto de la red.</summary>
public sealed class ArbolCaminos
{
    internal const int DesdeOrigen = -2;

    private readonly RedVial _red;
    private readonly double[] _dist;
    private readonly int[] _previo;

    internal ArbolCaminos(RedVial red, PuntoEnRed origen, double[] dist, int[] previo)
    {
        _red = red;
        Origen = origen;
        _dist = dist;
        _previo = previo;
    }

    public PuntoEnRed Origen { get; }

    public double DistanciaA(PuntoEnRed destino) => Elegir(destino).Distancia;

    /// <summary>Camino por calle desde el origen hasta el destino (ambos puntos proyectados incluidos).</summary>
    public IReadOnlyList<Coordenada> CaminoA(PuntoEnRed destino)
    {
        var (distancia, nodoFinal) = Elegir(destino);
        if (double.IsPositiveInfinity(distancia)) return [];
        var camino = new List<Coordenada> { destino.Proyectado };
        if (nodoFinal >= 0)
        {
            // Retrocede por los predecesores hasta el extremo del tramo de origen.
            for (int n = nodoFinal; n >= 0; n = _previo[n])
                camino.Add(_red.CoordenadaNodo(n));
        }
        camino.Add(Origen.Proyectado);
        camino.Reverse();
        return camino;
    }

    /// <summary>Mejor forma de llegar: por uno de los extremos del tramo destino, o directo si comparte tramo con el origen.</summary>
    private (double Distancia, int NodoFinal) Elegir(PuntoEnRed destino)
    {
        var (a, b, largo) = _red.Tramo(destino.Tramo);
        double porA = _dist[a] + destino.T * largo;
        double porB = _dist[b] + (1 - destino.T) * largo;
        var mejor = porA <= porB ? (porA, a) : (porB, b);
        if (destino.Tramo == Origen.Tramo)
        {
            double directo = Math.Abs(destino.T - Origen.T) * largo;
            if (directo <= mejor.Item1) return (directo, -1);
        }
        return mejor;
    }
}
