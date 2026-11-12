using NetTopologySuite.Geometries;
using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Consultas;

namespace VisorDatosSIG.Tests;

public class PruebasLimitesConsultas
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    [InlineData(int.MaxValue)]
    public async Task Pagina_fuera_de_rango_no_llega_al_repositorio(int pagina)
    {
        var repo = new RepositorioSimulado();
        var servicio = new ConsultaServicio(repo);
        var error = await Assert.ThrowsAsync<ErrorValidacion>(() => servicio.BuscarAsync(
            new SolicitudBusqueda("lotes", [], Pagina: pagina), CancellationToken.None));
        Assert.Equal("pagina", error.Campo);
        Assert.Null(repo.UltimaSolicitud);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100000)]
    public async Task Pagina_en_los_extremos_del_rango_se_conserva(int pagina)
    {
        var repo = new RepositorioSimulado();
        await new ConsultaServicio(repo).BuscarAsync(new SolicitudBusqueda("lotes", [], Pagina: pagina), CancellationToken.None);
        Assert.Equal(pagina, repo.UltimaSolicitud!.Pagina);
    }

    [Fact]
    public async Task Filtro_nulo_se_rechaza_antes_de_consultar()
    {
        var repo = new RepositorioSimulado();
        var error = await Assert.ThrowsAsync<ErrorValidacion>(() => new ConsultaServicio(repo).BuscarAsync(
            new SolicitudBusqueda("lotes", [null!]), CancellationToken.None));
        Assert.Equal("filtros", error.Campo);
        Assert.Null(repo.UltimaSolicitud);
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(25, false)]
    [InlineData(26, true)]
    public async Task Busqueda_general_informa_si_existen_mas_coincidencias(int cantidad, bool hayMas)
    {
        var repo = new RepositorioSimulado { CoincidenciasTexto = cantidad };
        var grupos = await new ConsultaServicio(repo).BuscarGeneralAsync("L1", CancellationToken.None);
        Assert.Equal(3, grupos.Count);
        Assert.All(grupos, grupo =>
        {
            Assert.Equal(Math.Min(25, cantidad), grupo.Entidades.Count);
            Assert.Equal(hayMas, grupo.HayMas);
            Assert.False(grupo.LimiteAlcanzado);
        });
        Assert.All(repo.LimitesTexto, limite => Assert.Equal(26, limite));
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    public async Task Codigos_fijos_informa_el_limite_sin_inventar_un_total(int cantidad, bool limiteAlcanzado)
    {
        var repo = new RepositorioSimulado { CoincidenciasInmuebles = cantidad };
        var grupo = Assert.Single(await new ConsultaServicio(repo).BuscarGeneralAsync("Ana", CancellationToken.None));
        Assert.Equal("codigosfijos", grupo.Capa);
        Assert.Equal(cantidad, grupo.Entidades.Count);
        Assert.Equal(limiteAlcanzado, grupo.LimiteAlcanzado);
        Assert.False(grupo.HayMas);
    }

    [Fact]
    public async Task Csv_con_cincuenta_mil_registros_se_exporta_completo()
    {
        var repo = new RepositorioSimulado { FilasExportacion = ConsultaServicio.MaximoExportacion };
        var filas = await new ConsultaServicio(repo).ExportarAsync(new SolicitudBusqueda("lotes", []), CancellationToken.None);
        Assert.Equal(50000, filas.Count);
        Assert.False(repo.UltimaSolicitud!.IncluirGeometria);
    }

    [Fact]
    public async Task Csv_que_supera_el_limite_se_rechaza_en_lugar_de_truncarse()
    {
        var repo = new RepositorioSimulado { FilasExportacion = ConsultaServicio.MaximoExportacion + 1 };
        var error = await Assert.ThrowsAsync<ErrorValidacion>(() => new ConsultaServicio(repo).ExportarAsync(
            new SolicitudBusqueda("lotes", []), CancellationToken.None));
        Assert.Contains("50.000", error.Message);
        Assert.Equal(50001, repo.LimiteExportacion);
    }

    [Fact]
    public async Task Consulta_y_exportacion_conservan_el_texto_general_y_los_filtros_aplicados()
    {
        var repo = new RepositorioSimulado();
        var servicio = new ConsultaServicio(repo);
        var solicitud = new SolicitudBusqueda("manzanas", [new("UV", "igual", " 01 ")], TextoGeneral: " MZ ");
        await servicio.BuscarAsync(solicitud, CancellationToken.None);
        var aplicada = repo.UltimaSolicitud!;
        await servicio.ExportarAsync(solicitud, CancellationToken.None);
        Assert.Equal("MZ", aplicada.TextoGeneral);
        Assert.Equal(aplicada.TextoGeneral, repo.UltimaSolicitud!.TextoGeneral);
        Assert.Equal(aplicada.Filtros, repo.UltimaSolicitud.Filtros);
        Assert.Equal("01", aplicada.Filtros[0].Valor);
    }

    private sealed class RepositorioSimulado : IConsultaCapasRepositorio
    {
        public int CoincidenciasTexto { get; init; }
        public int CoincidenciasInmuebles { get; init; }
        public int FilasExportacion { get; init; }
        public SolicitudBusqueda? UltimaSolicitud { get; private set; }
        public int LimiteExportacion { get; private set; }
        public List<int> LimitesTexto { get; } = [];

        private static IReadOnlyList<EntidadGeo> Filas(string capa, int cantidad) =>
            Enumerable.Range(1, cantidad).Select(id => new EntidadGeo { Capa = capa, Id = id }).ToList();

        public Task<ResultadoBusqueda> BuscarAsync(CapaDefinicion capa, SolicitudBusqueda solicitud, CancellationToken ct)
        {
            UltimaSolicitud = solicitud;
            return Task.FromResult(new ResultadoBusqueda(new Pagina<EntidadGeo>(0, solicitud.Pagina, solicitud.Tamano, []), null));
        }

        public Task<IReadOnlyList<EntidadGeo>> ExportarAsync(CapaDefinicion capa, SolicitudBusqueda solicitud, int maximo, CancellationToken ct)
        {
            UltimaSolicitud = solicitud;
            LimiteExportacion = maximo;
            return Task.FromResult(Filas(capa.Id, FilasExportacion));
        }

        public Task<IReadOnlyList<EntidadGeo>> BuscarInmueblesAsync(string texto, CancellationToken ct) =>
            Task.FromResult(Filas("codigosfijos", CoincidenciasInmuebles));

        public Task<IReadOnlyList<EntidadGeo>> BuscarTextoAsync(CapaDefinicion capa, string texto, int limite, CancellationToken ct)
        {
            LimitesTexto.Add(limite);
            return Task.FromResult(Filas(capa.Id, Math.Min(limite, CoincidenciasTexto)));
        }

        public Task<IReadOnlyList<ResumenCapa>> ObtenerResumenAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<int, int>> ObtenerConteoPorEstadoAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<(IReadOnlyList<EntidadGeo> Entidades, bool Truncado)> ObtenerPorExtensionAsync(CapaDefinicion capa, Envelope bbox, double toleranciaSimplificacion, int limite, CancellationToken ct) => throw new NotSupportedException();
        public Task<EntidadGeo?> ObtenerPorIdAsync(CapaDefinicion capa, int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntidadGeo>> IdentificarAsync(CapaDefinicion capa, double lon, double lat, double tolerancia, int limite, CancellationToken ct) => throw new NotSupportedException();
    }
}
