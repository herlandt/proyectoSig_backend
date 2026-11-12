/* =====================================================================
   VisorDatosSIG 2026 · 03_Seguridad.sql
   - Roles oficiales del docente + rol "Consultor" que exige la
     especificación (RF-SEG-02). Solo se agregan FILAS; el esquema no cambia.
   - Usuarios iniciales del script oficial (admin, Juan, Pedro) con sus
     hashes PBKDF2-SHA256 (100.000 iteraciones). Cambiar la contraseña
     de admin antes de publicar.
   - Menú del Anexo E de la especificación en dbo.MenuOpciones y permisos
     por usuario en dbo.UsuarioMenu (reemplaza al menú WebForms del script 08,
     que no se instala).
   - Roles de base de datos para el principio de mínimo privilegio.
   Re-ejecutable.
   ===================================================================== */
USE VisorDatosSIG;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- Roles de la aplicación ---------- */
MERGE dbo.Roles AS d
USING (VALUES
 ('Administrador','Administrador del Sistema'),
 ('Consultor','Consulta del visor cartográfico y búsquedas (perfil de la especificación)'),
 ('Catastro','Rol para crear, modificar y eliminar Manzanas, Lotes, Codigo Fijo y Vias'),
 ('Lecturador','Rol para lecturar medidores'),
 ('Cortador','Rol para cortar servicios'),
 ('Reconexion','Rol para la reconnexion por corte')
) AS o(NombreRol,Descripcion)
ON d.NombreRol=o.NombreRol
WHEN MATCHED THEN UPDATE SET Descripcion=o.Descripcion
WHEN NOT MATCHED THEN INSERT(NombreRol,Descripcion,Estado) VALUES(o.NombreRol,o.Descripcion,1);
GO

/* ---------- Usuarios iniciales (hashes del script oficial) ---------- */
IF NOT EXISTS(SELECT 1 FROM dbo.Usuarios WHERE Login=N'admin')
    INSERT dbo.Usuarios(Login,Nombre,PasswordHash,PasswordSalt,Iteraciones)
    VALUES(N'admin',N'Administrador',
        0x599F3972C925D7D968B215FB5385865FDF961BCA18BB332E13AB9F014903F89B,
        0x82A4FC4EE67761C1A4E95BCD6BF9CF9B63344B9E665C839C8F9F654D1718B665,100000);

IF NOT EXISTS(SELECT 1 FROM dbo.Usuarios WHERE Login=N'Juan')
    INSERT dbo.Usuarios(Login,Nombre,PasswordHash,PasswordSalt,Iteraciones)
    VALUES(N'Juan',N'Juan',
        0xB14BC9A077C4CC29BC030A25AD517456D88224BBB57ECA2C5D977C4E85131676,
        0x3F2B95B127A4E8C161A840BDF72EA6623E318F7969B3CCF7A7B66BA33A90D89C,100000);

IF NOT EXISTS(SELECT 1 FROM dbo.Usuarios WHERE Login=N'Pedro')
    INSERT dbo.Usuarios(Login,Nombre,PasswordHash,PasswordSalt,Iteraciones)
    VALUES(N'Pedro',N'Pedro',
        0xC1BA4D699543F1E1D12C302CD0FD1BB4DDECF6A9C55620B9CC718E918930A528,
        0xA4E75D92036F1B847CD4298A18D3F16B2746AE50A81683CD375943EE15F7BD01,100000);

INSERT dbo.UsuariosRoles(IdUsuario,IdRol)
SELECT u.IdUsuario,r.IdRol
FROM dbo.Usuarios u CROSS JOIN dbo.Roles r
WHERE ((u.Login=N'admin' AND r.NombreRol='Administrador')
    OR (u.Login=N'Juan' AND r.NombreRol='Lecturador')
    OR (u.Login=N'Pedro' AND r.NombreRol='Cortador'))
  AND NOT EXISTS(SELECT 1 FROM dbo.UsuariosRoles x WHERE x.IdUsuario=u.IdUsuario AND x.IdRol=r.IdRol);
GO

/* ---------- Menú del Anexo E ---------- */
MERGE dbo.MenuOpciones AS d
USING (VALUES
 (N'Inicio',             N'bi-house',     1),
 (N'Visor cartográfico', N'bi-map',       2),
 (N'Consultas',          N'bi-search',    3),
 (N'Administración',     N'bi-gear',      4),
 (N'Cuenta',             N'bi-person',    5),
 (N'Ayuda',              N'bi-question-circle', 6)
) AS o(NombreMenu,Icono,Orden)
ON d.IdMenuPadre IS NULL AND d.NombreMenu=o.NombreMenu
WHEN MATCHED THEN UPDATE SET Nivel=1,Icono=o.Icono,Orden=o.Orden,Estado=1,Url=NULL
WHEN NOT MATCHED THEN
 INSERT(IdMenuPadre,Nivel,NombreMenu,Url,Icono,Orden,Estado)
 VALUES(NULL,1,o.NombreMenu,NULL,o.Icono,o.Orden,1);
GO

DECLARE @padres TABLE(Nombre NVARCHAR(100) PRIMARY KEY, IdMenu INT);
INSERT @padres SELECT NombreMenu, IdMenu FROM dbo.MenuOpciones
 WHERE IdMenuPadre IS NULL
   AND NombreMenu IN (N'Inicio',N'Visor cartográfico',N'Consultas',N'Administración',N'Cuenta',N'Ayuda');

MERGE dbo.MenuOpciones AS d
USING (
 SELECT p.IdMenu, o.NombreMenu, o.Url, o.Icono, o.Orden
 FROM (VALUES
  (N'Inicio',             N'Panel principal',          N'/',                          N'bi-speedometer2', 1),
  (N'Visor cartográfico', N'Mapa general',             N'/Visor',                     N'bi-globe-americas', 1),
  (N'Visor cartográfico', N'Capas',                    N'/Visor?panel=capas',         N'bi-layers', 2),
  (N'Visor cartográfico', N'Herramientas',             N'/Visor?panel=herramientas',  N'bi-tools', 3),
  (N'Consultas',          N'Búsqueda general',         N'/Consultas',                 N'bi-search', 1),
  (N'Consultas',          N'Filtros avanzados',        N'/Consultas?modo=filtros',    N'bi-funnel', 2),
  (N'Consultas',          N'Resultados',               N'/Consultas#resultados',      N'bi-table', 3),
  (N'Administración',     N'Usuarios',                 N'/Administracion/Usuarios',   N'bi-people', 1),
  (N'Administración',     N'Perfiles y permisos',      N'/Administracion/Permisos',   N'bi-shield-lock', 2),
  (N'Administración',     N'Catálogo de capas',        N'/Administracion/Capas',      N'bi-stack', 3),
  (N'Administración',     N'Historial de migraciones', N'/Administracion/Migraciones', N'bi-clock-history', 4),
  (N'Administración',     N'Bitácora del sistema',     N'/Administracion/Bitacora',   N'bi-journal-text', 5),
  (N'Cuenta',             N'Mi perfil',                N'/Cuenta/Perfil',             N'bi-person-badge', 1),
  (N'Cuenta',             N'Cerrar sesión',            N'/Cuenta/Salir',              N'bi-box-arrow-right', 2),
  (N'Ayuda',              N'Manual de usuario',        N'/Ayuda/Manual',              N'bi-book', 1),
  (N'Ayuda',              N'Acerca del sistema',       N'/Ayuda/AcercaDe',            N'bi-info-circle', 2)
 ) AS o(Padre,NombreMenu,Url,Icono,Orden)
 JOIN @padres p ON p.Nombre=o.Padre
) AS o
ON d.IdMenuPadre=o.IdMenu AND d.NombreMenu=o.NombreMenu
WHEN MATCHED THEN UPDATE SET Nivel=2,Url=o.Url,Icono=o.Icono,Orden=o.Orden,Estado=1
WHEN NOT MATCHED THEN
 INSERT(IdMenuPadre,Nivel,NombreMenu,Url,Icono,Orden,Estado)
 VALUES(o.IdMenu,2,o.NombreMenu,o.Url,o.Icono,o.Orden,1);
GO

/* ---------- Permisos de menú por usuario ----------
   Administrador: todas las opciones con todos los permisos.
   Resto de perfiles (Consultor y roles operativos): todo menos Administración. */
INSERT dbo.UsuarioMenu(IdUsuario,IdMenu,PuedeVer,PuedeCrear,PuedeEditar,PuedeEliminar)
SELECT u.IdUsuario, m.IdMenu, 1,
       CASE WHEN r.EsAdmin = 1 THEN 1 ELSE 0 END,
       CASE WHEN r.EsAdmin = 1 THEN 1 ELSE 0 END,
       CASE WHEN r.EsAdmin = 1 THEN 1 ELSE 0 END
FROM dbo.Usuarios u
CROSS APPLY (SELECT CASE WHEN EXISTS(
        SELECT 1 FROM dbo.UsuariosRoles ur JOIN dbo.Roles ro ON ro.IdRol=ur.IdRol
         WHERE ur.IdUsuario=u.IdUsuario AND ro.NombreRol='Administrador') THEN 1 ELSE 0 END AS EsAdmin) r
JOIN dbo.MenuOpciones m ON m.Estado=1
LEFT JOIN dbo.MenuOpciones p ON p.IdMenu=m.IdMenuPadre
WHERE (r.EsAdmin = 1 OR COALESCE(p.NombreMenu, m.NombreMenu) <> N'Administración')
  AND COALESCE(p.NombreMenu, m.NombreMenu) IN
      (N'Inicio',N'Visor cartográfico',N'Consultas',N'Administración',N'Cuenta',N'Ayuda')
  AND NOT EXISTS(SELECT 1 FROM dbo.UsuarioMenu x WHERE x.IdUsuario=u.IdUsuario AND x.IdMenu=m.IdMenu);
GO

/* ---------- Roles de base de datos (mínimo privilegio) ----------
   rol_visor_web      -> cuenta que usa la API web.
   rol_visor_migrador -> cuenta que usa el migrador de escritorio.
   Los logins se crean con 05_UsuariosSql.sql (opcional). */
IF DATABASE_PRINCIPAL_ID(N'rol_visor_web') IS NULL CREATE ROLE rol_visor_web;
IF DATABASE_PRINCIPAL_ID(N'rol_visor_migrador') IS NULL CREATE ROLE rol_visor_migrador;
GO
GRANT SELECT ON dbo.Manzanas TO rol_visor_web;
GRANT SELECT ON dbo.Lotes TO rol_visor_web;
GRANT SELECT ON dbo.CodigosFijos TO rol_visor_web;
GRANT SELECT ON dbo.Vias TO rol_visor_web;
GRANT SELECT ON dbo.Roles TO rol_visor_web;
GRANT SELECT ON dbo.MenuOpciones TO rol_visor_web;
GRANT SELECT, INSERT, UPDATE ON dbo.Usuarios TO rol_visor_web;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.UsuariosRoles TO rol_visor_web;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.UsuarioMenu TO rol_visor_web;
GRANT SELECT ON dbo.BitacoraMigracion TO rol_visor_web;
GRANT SELECT ON dbo.BitacoraMigracionDetalle TO rol_visor_web;
GRANT SELECT, INSERT ON dbo.BitacoraAcceso TO rol_visor_web;
GRANT EXECUTE ON dbo.sp_BuscarInmueble TO rol_visor_web;

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Manzanas TO rol_visor_migrador;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Lotes TO rol_visor_migrador;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.CodigosFijos TO rol_visor_migrador;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.Vias TO rol_visor_migrador;
GRANT SELECT ON dbo.Usuarios TO rol_visor_migrador;
GRANT SELECT ON dbo.UsuariosRoles TO rol_visor_migrador;
GRANT SELECT ON dbo.Roles TO rol_visor_migrador;
GRANT SELECT, INSERT, UPDATE ON dbo.BitacoraMigracion TO rol_visor_migrador;
GRANT SELECT, INSERT ON dbo.BitacoraMigracionDetalle TO rol_visor_migrador;
GRANT INSERT ON dbo.BitacoraAcceso TO rol_visor_migrador;
GRANT EXECUTE ON dbo.sp_ActualizarLoteCodigosFijos TO rol_visor_migrador;
GRANT EXECUTE ON dbo.sp_ActualizarVinculosEspaciales TO rol_visor_migrador;
GRANT EXECUTE ON dbo.sp_ReconstruirIndicesEspaciales TO rol_visor_migrador;
GO

SELECT u.Login, r.NombreRol, COUNT(um.IdMenu) AS OpcionesMenu
FROM dbo.Usuarios u
LEFT JOIN dbo.UsuariosRoles ur ON ur.IdUsuario=u.IdUsuario
LEFT JOIN dbo.Roles r ON r.IdRol=ur.IdRol
LEFT JOIN dbo.UsuarioMenu um ON um.IdUsuario=u.IdUsuario
GROUP BY u.Login, r.NombreRol
ORDER BY u.Login;
GO
