using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Migracion;

namespace VisorDatosSIG.Tests;

public class PruebasModoCarga
{
    [Theory]
    [InlineData(null, ModoCarga.Reemplazar)]
    [InlineData("reemplazar", ModoCarga.Reemplazar)]
    [InlineData("REEMPLAZAR", ModoCarga.Reemplazar)]
    [InlineData("anexar", ModoCarga.Anexar)]
    [InlineData("AnExAr", ModoCarga.Anexar)]
    public void Solo_los_modos_documentados_se_aceptan_y_omitir_conserva_reemplazar(string? valor, ModoCarga esperado) =>
        Assert.Equal(esperado, ModosCarga.Parsear(valor));

    [Theory]
    [InlineData("anexra")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("true")]
    public void Modo_desconocido_se_rechaza_con_error_claro(string valor)
    {
        var error = Assert.Throws<ErrorValidacion>(() => ModosCarga.Parsear(valor));
        Assert.Contains("reemplazar", error.Message);
        Assert.Contains("anexar", error.Message);
    }
}
