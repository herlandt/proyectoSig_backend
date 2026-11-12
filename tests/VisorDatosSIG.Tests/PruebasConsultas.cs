using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Consultas;
using VisorDatosSIG.Infrastructure.Datos;

namespace VisorDatosSIG.Tests;

public class PruebasConsultas
{
    private readonly ConsultaServicio _servicio = new(new RepositorioQueNoDebeUsarse());

    [Fact]
    public void Todo_campo_del_catalogo_tiene_expresion_sql() =>
        Assert.Empty(SqlCatalogo.CamposSinExpresion());

    [Fact]
    public void Las_cuatro_capas_estan_en_el_catalogo()
    {
        Assert.Equal(["manzanas", "lotes", "codigosfijos", "vias"], CatalogoCapas.Todas.Select(c => c.Id));
        Assert.Equal(5, CatalogoCapas.EstadosServicio.Count);
    }

    [Theory]
    [InlineData("Nombre; DROP TABLE x")]
    [InlineData("PasswordHash")]
    [InlineData("1=1")]
    public async Task Campo_fuera_del_catalogo_se_rechaza(string campo)
    {
        var s = new SolicitudBusqueda("codigosfijos", [new FiltroConsulta(campo, "igual", "a")]);
        await Assert.ThrowsAsync<ErrorValidacion>(() => _servicio.BuscarAsync(s, CancellationToken.None));
    }

    [Fact]
    public async Task Operador_incompatible_con_el_tipo_se_rechaza()
    {
        var s = new SolicitudBusqueda("codigosfijos", [new FiltroConsulta("Nombre", "mayor", "A")]);
        await Assert.ThrowsAsync<ErrorValidacion>(() => _servicio.BuscarAsync(s, CancellationToken.None));
    }

    [Fact]
    public async Task Valor_no_numerico_en_campo_numerico_se_rechaza()
    {
        var s = new SolicitudBusqueda("codigosfijos", [new FiltroConsulta("CodFijo", "igual", "12abc")]);
        await Assert.ThrowsAsync<ErrorValidacion>(() => _servicio.BuscarAsync(s, CancellationToken.None));
    }

    [Fact]
    public async Task Mas_de_cinco_filtros_se_rechaza()
    {
        var filtros = Enumerable.Range(0, 6).Select(_ => new FiltroConsulta("Nombre", "contiene", "a")).ToList();
        await Assert.ThrowsAsync<ErrorValidacion>(() => _servicio.BuscarAsync(new SolicitudBusqueda("codigosfijos", filtros), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("-60,-16,-61,-15")]          // mínimo mayor que máximo
    [InlineData("-200,-16,-60,-15")]         // fuera de WGS 84
    [InlineData("a,b,c,d")]
    public void Bbox_invalido_se_rechaza(string bbox) =>
        Assert.Throws<ErrorValidacion>(() => ConsultaServicio.ParsearBbox(bbox));

    [Fact]
    public void Bbox_valido_se_interpreta_lon_lat()
    {
        var e = ConsultaServicio.ParsearBbox("-61.0,-16.44,-60.92,-16.32");
        Assert.Equal(-61.0, e.MinX);
        Assert.Equal(-16.32, e.MaxY);
    }

    [Fact]
    public async Task Fuera_del_zoom_minimo_no_consulta_la_base()
    {
        // Lotes exige zoom 16: con zoom 12 debe responder vacío sin tocar el repositorio.
        var r = await _servicio.PorExtensionAsync("lotes", "-61.0,-16.44,-60.92,-16.32", 12, CancellationToken.None);
        Assert.True(r.FueraDeZoom);
        Assert.Empty(r.Entidades);
    }

    [Fact]
    public async Task Busqueda_general_exige_dos_caracteres() =>
        await Assert.ThrowsAsync<ErrorValidacion>(() => _servicio.BuscarGeneralAsync("a", CancellationToken.None));

    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("[x]", "\\[x]")]
    public void Comodines_like_se_escapan(string entrada, string esperado) =>
        Assert.Equal(esperado, ConsultaCapasRepositorio.EscaparLike(entrada));

    /// <summary>Si una validación deja pasar algo hasta la base, la prueba falla.</summary>
    private sealed class RepositorioQueNoDebeUsarse : IConsultaCapasRepositorio
    {
        private static Exception Falla() => new InvalidOperationException("La validación debió impedir llegar a la base.");
        public Task<IReadOnlyList<ResumenCapa>> ObtenerResumenAsync(CancellationToken ct) => throw Falla();
        public Task<IReadOnlyDictionary<int, int>> ObtenerConteoPorEstadoAsync(CancellationToken ct) => throw Falla();
        public Task<(IReadOnlyList<EntidadGeo> Entidades, bool Truncado)> ObtenerPorExtensionAsync(CapaDefinicion capa, NetTopologySuite.Geometries.Envelope bbox, double t, int l, CancellationToken ct) => throw Falla();
        public Task<EntidadGeo?> ObtenerPorIdAsync(CapaDefinicion capa, int id, CancellationToken ct) => throw Falla();
        public Task<IReadOnlyList<EntidadGeo>> IdentificarAsync(CapaDefinicion capa, double lon, double lat, double t, int l, CancellationToken ct) => throw Falla();
        public Task<ResultadoBusqueda> BuscarAsync(CapaDefinicion capa, SolicitudBusqueda s, CancellationToken ct) => throw Falla();
        public Task<IReadOnlyList<EntidadGeo>> ExportarAsync(CapaDefinicion capa, SolicitudBusqueda s, int m, CancellationToken ct) => throw Falla();
        public Task<IReadOnlyList<EntidadGeo>> BuscarInmueblesAsync(string texto, CancellationToken ct) => throw Falla();
        public Task<IReadOnlyList<EntidadGeo>> BuscarTextoAsync(CapaDefinicion capa, string texto, int limite, CancellationToken ct) => throw Falla();
    }
}
