using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Rutas;

namespace VisorDatosSIG.Tests;

/// <summary>
/// Motor de rutas (Dijkstra sobre la red vial). Cuadrícula sintética de 3×3 calles con 0,001° de separación
/// (≈107 m en X y ≈111 m en Y a la latitud de San Ignacio) y, al final, las vías oficiales.
/// </summary>
public class PruebasRutas
{
    private const double X0 = -61.0, Y0 = -16.38, Paso = 0.001;

    private static Coordenada C(double i, double j) => new(X0 + i * Paso, Y0 + j * Paso);

    /// <summary>Tres calles verticales y tres horizontales; cada una tiene vértice en todos los cruces.</summary>
    private static RedVial Cuadricula(params IReadOnlyList<Coordenada>[] extras)
    {
        var lineas = new List<IReadOnlyList<Coordenada>>();
        for (int k = 0; k <= 2; k++)
        {
            lineas.Add([C(k, 0), C(k, 1), C(k, 2)]);
            lineas.Add([C(0, k), C(1, k), C(2, k)]);
        }
        lineas.AddRange(extras);
        return new RedVial(lineas);
    }

    [Fact]
    public void Las_vias_se_unen_en_los_cruces()
    {
        var red = Cuadricula();
        Assert.Equal(9, red.Nodos);
        Assert.Equal(12, red.Tramos);
        Assert.Equal(1, red.Componentes);
    }

    [Fact]
    public void La_ruta_va_por_las_calles_y_no_cruza_la_manzana()
    {
        var red = Cuadricula();
        var origen = red.Ajustar(C(0, 0), 50)!.Value;
        var destino = red.Ajustar(C(1, 1), 50)!.Value;

        double porCalle = red.Dijkstra(origen).DistanciaA(destino);
        double enLineaRecta = RedVial.Distancia(C(0, 0), C(1, 1));
        double esperado = RedVial.Distancia(C(0, 0), C(1, 0)) + RedVial.Distancia(C(1, 0), C(1, 1));

        Assert.Equal(esperado, porCalle, 0.5);
        Assert.True(porCalle > enLineaRecta + 50, "la ruta no debe atravesar la manzana en diagonal");
    }

    [Fact]
    public void Un_punto_fuera_de_la_calle_se_proyecta_sobre_el_tramo_mas_cercano()
    {
        var red = Cuadricula();
        // A mitad de la calle inferior y unos 22 m hacia adentro de la manzana.
        var p = red.Ajustar(new Coordenada(X0 + 0.5 * Paso, Y0 + 0.0002), 100)!.Value;

        Assert.Equal(0.5, p.T is > 0.5 ? 1 - p.T : p.T, 0.01);
        Assert.Equal(22.1, p.DistanciaAcceso, 0.5);
        Assert.Equal(Y0, p.Proyectado.Lat, 1e-9);
    }

    [Fact]
    public void Dos_puntos_del_mismo_tramo_se_unen_directo()
    {
        var red = Cuadricula();
        var a = red.Ajustar(C(0.2, 0), 10)!.Value;
        var b = red.Ajustar(C(0.7, 0), 10)!.Value;

        var arbol = red.Dijkstra(a);
        Assert.Equal(RedVial.Distancia(C(0.2, 0), C(0.7, 0)), arbol.DistanciaA(b), 0.5);
        Assert.Equal(2, arbol.CaminoA(b).Count);
    }

    [Fact]
    public void El_camino_reconstruido_mide_lo_mismo_que_la_distancia()
    {
        var red = Cuadricula();
        var origen = red.Ajustar(C(0.3, 0), 10)!.Value;
        var destino = red.Ajustar(C(2, 1.6), 10)!.Value;
        var arbol = red.Dijkstra(origen);

        var camino = arbol.CaminoA(destino);
        double largo = camino.Zip(camino.Skip(1), RedVial.Distancia).Sum();

        Assert.Equal(arbol.DistanciaA(destino), largo, 0.5);
        Assert.Equal(origen.Proyectado, camino[0]);
        Assert.Equal(destino.Proyectado, camino[^1]);
    }

    [Fact]
    public void Las_paradas_quedan_en_orden_por_calle()
    {
        var planificador = new PlanificadorRuta(Cuadricula());
        // Sobre la calle inferior, en desorden; la partida está en el extremo izquierdo.
        var candidatas = new[] { 1.7, 0.2, 1.2, 0.8, 0.5 }
            .Select((x, i) => new ParadaCandidata(10 + i, C(x, 0))).ToList();

        var plan = planificador.Planificar(C(0, 0), [], candidatas, 5);

        Assert.Equal([11, 14, 13, 12, 10], plan.Paradas.Select(p => p.Id));
        Assert.Equal([1, 2, 3, 4, 5], plan.Paradas.Select(p => p.Orden));
        Assert.Equal(RedVial.Distancia(C(0, 0), C(1.7, 0)), plan.DistanciaTotal, 1);
        Assert.Equal(plan.DistanciaTotal, plan.Paradas[^1].DistanciaAcumulada, 1e-6);
    }

    [Fact]
    public void Se_eligen_las_mas_cercanas_por_calle_no_en_linea_recta()
    {
        var planificador = new PlanificadorRuta(Cuadricula());
        // La 1 está cerca en línea recta pero hay que rodear la manzana; la 2 está más lejos en línea recta y sobre la misma calle.
        var cercaEnLineaRecta = new ParadaCandidata(1, new Coordenada(X0 + 0.5 * Paso, Y0 + 0.95 * Paso));
        var cercaPorCalle = new ParadaCandidata(2, C(1.6, 0));

        var plan = planificador.Planificar(C(0.5, 0), [], [cercaEnLineaRecta, cercaPorCalle], 1);

        Assert.Equal([2], plan.Paradas.Select(p => p.Id));
    }

    [Fact]
    public void Los_pendientes_siempre_entran_y_se_recalcula_la_ruta_acumulada()
    {
        var planificador = new PlanificadorRuta(Cuadricula());
        var pendiente = new ParadaCandidata(99, C(2, 2));
        var nuevas = new[] { new ParadaCandidata(1, C(0.5, 0)), new ParadaCandidata(2, C(1, 0.5)) };

        var plan = planificador.Planificar(C(0, 0), [pendiente], nuevas, 2);

        Assert.Equal(3, plan.Paradas.Count);
        Assert.Contains(plan.Paradas, p => p.Id == 99);
        Assert.Equal(99, plan.Paradas[^1].Id);   // el pendiente lejano queda al final
    }

    [Fact]
    public void Una_parada_a_mas_de_500_m_de_la_red_queda_excluida_con_motivo()
    {
        var planificador = new PlanificadorRuta(Cuadricula());
        var lejana = new ParadaCandidata(7, new Coordenada(X0 - 0.01, Y0));   // ≈1 km al oeste

        var plan = planificador.Planificar(C(0, 0), [lejana], [], 0);

        Assert.Empty(plan.Paradas);
        Assert.Equal(7, Assert.Single(plan.Excluidas).Id);
    }

    [Fact]
    public void Un_tramo_aislado_no_atrapa_a_las_paradas()
    {
        // Calle suelta, sin conexión, justo al lado de la parada: debe ajustarse a la red principal.
        var aislada = new[] { new Coordenada(X0 + 2.3 * Paso, Y0 + 0.5 * Paso), new Coordenada(X0 + 2.3 * Paso, Y0 + 0.6 * Paso) };
        var red = Cuadricula(aislada);
        Assert.Equal(2, red.Componentes);

        var plan = new PlanificadorRuta(red).Planificar(C(0, 0), [], [new ParadaCandidata(5, C(2.25, 0.55))], 1);

        var parada = Assert.Single(plan.Paradas);
        Assert.Equal(X0 + 2 * Paso, parada.EnRed.Lon, 1e-9);   // proyectada sobre la calle x = 2
    }

    [Fact]
    public void Un_punto_de_partida_lejos_de_las_calles_es_error_de_validacion()
    {
        var planificador = new PlanificadorRuta(Cuadricula());
        Assert.Throws<ErrorValidacion>(() => planificador.Planificar(new Coordenada(X0 - 0.05, Y0), [], [], 1));
    }

    [Fact]
    public void DosOpt_corrige_un_recorrido_que_se_cruza()
    {
        // Cuatro puntos en línea (0 = partida). El recorrido 0-2-1-3 se cruza; el óptimo es 0-1-2-3.
        double[] x = [0, 1, 2, 3];
        var d = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                d[i, j] = Math.Abs(x[i] - x[j]);
        var orden = new List<int> { 0, 2, 1, 3 };

        PlanificadorRuta.DosOpt(orden, d);

        Assert.Equal([0, 1, 2, 3], orden);
    }

    [Fact]
    public void Red_real_de_San_Ignacio_esta_conectada_y_las_rutas_son_coherentes()
    {
        var red = new RedVial(LeerVias());
        Assert.Equal(4168, red.Nodos);
        Assert.True(red.NodosComponentePrincipal >= 0.97 * red.Nodos, $"componente principal: {red.NodosComponentePrincipal} de {red.Nodos}");

        // Plaza principal → Avenida Santa Cruz (puntos del centro de San Ignacio).
        var origen = red.Ajustar(new Coordenada(-60.9605, -16.3795), 200)!.Value;
        var destino = red.Ajustar(new Coordenada(-60.9530, -16.3740), 200)!.Value;
        var arbol = red.Dijkstra(origen);
        double porCalle = arbol.DistanciaA(destino);
        double recta = RedVial.Distancia(origen.Proyectado, destino.Proyectado);
        var camino = arbol.CaminoA(destino);

        Assert.InRange(porCalle, recta, 2 * recta);
        Assert.Equal(porCalle, camino.Zip(camino.Skip(1), RedVial.Distancia).Sum(), 1);
    }

    [Fact]
    public void Treinta_cortes_reales_se_planifican_en_orden_y_rapido()
    {
        var planificador = new PlanificadorRuta(new RedVial(LeerVias()));
        // Códigos «Para Corte» (estado 2) exportados desde SQL Server.
        using var lector = Shapefile.OpenRead(Path.Combine(Carpeta("ProyectoSHP"), "2_DesdeSQLServer", "SQL_CodigosFijos.shp"));
        var candidatas = new List<ParadaCandidata>();
        while (lector.Read(out bool eliminado))
            if (!eliminado && Convert.ToInt32(lector.Fields["Estado"].Value) == 2 && lector.Geometry is Point p)
                candidatas.Add(new ParadaCandidata(Convert.ToInt32(lector.Fields["IdCodigo"].Value), new Coordenada(p.X, p.Y)));
        Assert.Equal(272, candidatas.Count);

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var plan = planificador.Planificar(new Coordenada(-60.9609, -16.3797), [], candidatas, 30);
        reloj.Stop();

        Assert.Equal(30, plan.Paradas.Count);
        Assert.Equal(Enumerable.Range(1, 30), plan.Paradas.Select(p => p.Orden));
        Assert.Equal(30, plan.Paradas.Select(p => p.Id).Distinct().Count());
        Assert.All(plan.Paradas, p => Assert.True(p.Trazado.Count >= 2));
        Assert.Equal(plan.Paradas.Sum(p => p.DistanciaTramo), plan.DistanciaTotal, 1e-6);
        Assert.True(reloj.ElapsedMilliseconds < 3000, $"tardó {reloj.ElapsedMilliseconds} ms");
    }

    private static string Carpeta(string nombre)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, nombre))) return Path.Combine(d.FullName, nombre);
        throw new DirectoryNotFoundException($"No se encontró la carpeta {nombre}.");
    }

    private static IEnumerable<IReadOnlyList<Coordenada>> LeerVias()
    {
        using var lector = Shapefile.OpenRead(Path.Combine(Carpeta("DatosSIG_Reproj"), "Exp_MapaBase_VIAS_4326.shp"));
        var lineas = new List<IReadOnlyList<Coordenada>>();
        while (lector.Read(out bool eliminado))
        {
            if (eliminado || lector.Geometry is null) continue;
            var partes = lector.Geometry is MultiLineString m ? m.Geometries.Cast<LineString>() : [(LineString)lector.Geometry];
            lineas.AddRange(partes.Select(l => (IReadOnlyList<Coordenada>)[.. l.Coordinates.Select(c => new Coordenada(c.X, c.Y))]));
        }
        return lineas;
    }
}
