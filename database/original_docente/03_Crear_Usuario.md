# Creación del usuario inicial

El sistema utiliza PBKDF2-SHA256 con sal aleatoria y 100.000 iteraciones.

La forma recomendada es ejecutar una vez el siguiente código dentro de una
página administrativa temporal, insertar los valores y luego eliminar la
página:

```csharp
byte[] salt = Seguridad.GenerarSalt();
byte[] hash = Seguridad.GenerarHash("Admin123!", salt, 100000);

using (SqlConnection cn = Db.Abrir())
using (SqlCommand cmd = new SqlCommand(
  @"INSERT dbo.Usuarios(Login,Nombre,PasswordHash,PasswordSalt,Iteraciones)
    VALUES(@Login,@Nombre,@Hash,@Salt,100000)", cn))
{
    cmd.Parameters.AddWithValue("@Login", "admin");
    cmd.Parameters.AddWithValue("@Nombre", "Administrador");
    cmd.Parameters.Add("@Hash", SqlDbType.VarBinary, 32).Value = hash;
    cmd.Parameters.Add("@Salt", SqlDbType.VarBinary, 32).Value = salt;
    cmd.ExecuteNonQuery();
}
```

Cambie la contraseña inicial antes de publicar el sitio.
