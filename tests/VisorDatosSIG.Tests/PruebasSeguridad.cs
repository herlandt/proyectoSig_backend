using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Seguridad;

namespace VisorDatosSIG.Tests;

public class PruebasSeguridad
{
    private readonly HasherPbkdf2 _hasher = new();

    [Fact]
    public void Hash_generado_se_verifica_y_respeta_el_diseno_oficial()
    {
        var (hash, salt, iteraciones) = _hasher.Generar("Clave2026x");
        Assert.Equal(32, hash.Length);          // PasswordHash VARBINARY(32)
        Assert.Equal(32, salt.Length);          // PasswordSalt VARBINARY(32)
        Assert.Equal(100_000, iteraciones);     // DF_Usuarios_Iteraciones
        Assert.True(_hasher.Verificar("Clave2026x", hash, salt, iteraciones));
    }

    [Fact]
    public void Contrasena_incorrecta_no_se_verifica()
    {
        var (hash, salt, iteraciones) = _hasher.Generar("Clave2026x");
        Assert.False(_hasher.Verificar("clave2026x", hash, salt, iteraciones));
        Assert.False(_hasher.Verificar("Clave2026x", hash, salt, iteraciones - 1));
    }

    [Fact]
    public void La_sal_es_aleatoria()
    {
        var a = _hasher.Generar("MismaClave1");
        var b = _hasher.Generar("MismaClave1");
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Theory]
    [InlineData("corta1", false)]
    [InlineData("sololetras", false)]
    [InlineData("12345678", false)]
    [InlineData("Letras123", true)]
    public void Reglas_de_contrasena(string clave, bool valida) =>
        Assert.Equal(valida, ReglasUsuario.ValidarContrasena(clave) is null);

    [Theory]
    [InlineData("admin", true)]
    [InlineData("consultor.01", true)]
    [InlineData("ab", false)]
    [InlineData("con espacio", false)]
    [InlineData("x'; DROP TABLE Usuarios--", false)]
    public void Reglas_de_login(string login, bool valido) =>
        Assert.Equal(valido, ReglasUsuario.ValidarLogin(login) is null);

    [Fact]
    public void Roles_operativos_se_tratan_como_consultor()
    {
        Assert.Equal(RolesSistema.Administrador, RolesSistema.PerfilEfectivo(["Cortador", "Administrador"]));
        Assert.Equal(RolesSistema.Consultor, RolesSistema.PerfilEfectivo(["Lecturador"]));
        Assert.Equal(RolesSistema.Consultor, RolesSistema.PerfilEfectivo([]));
    }
}
