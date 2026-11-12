USE VisorDatosSIG;
GO

IF OBJECT_ID(N'dbo.MenuOpciones',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MenuOpciones
    (
        IdMenu INT IDENTITY(1,1) PRIMARY KEY,
        IdMenuPadre INT NULL,
        Nivel INT NOT NULL,
        NombreMenu NVARCHAR(100) NOT NULL,
        Url NVARCHAR(200) NULL,
        Icono NVARCHAR(50) NULL,
        Orden INT NOT NULL CONSTRAINT DF_MenuOpciones_Orden DEFAULT(0),
        Estado BIT NOT NULL CONSTRAINT DF_MenuOpciones_Estado DEFAULT(1),
        CONSTRAINT FK_MenuOpciones_Padre FOREIGN KEY(IdMenuPadre)
            REFERENCES dbo.MenuOpciones(IdMenu),
        CONSTRAINT CK_MenuOpciones_Nivel CHECK(Nivel IN(1,2,3))
    );
END;
GO

IF OBJECT_ID(N'dbo.UsuarioMenu',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UsuarioMenu
    (
        IdUsuarioMenu INT IDENTITY(1,1) PRIMARY KEY,
        IdUsuario INT NOT NULL,
        IdMenu INT NOT NULL,
        PuedeVer BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeVer DEFAULT(1),
        PuedeCrear BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeCrear DEFAULT(0),
        PuedeEditar BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeEditar DEFAULT(0),
        PuedeEliminar BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeEliminar DEFAULT(0),
        CONSTRAINT FK_UsuarioMenu_Usuarios FOREIGN KEY(IdUsuario)
            REFERENCES dbo.Usuarios(IdUsuario),
        CONSTRAINT FK_UsuarioMenu_Menu FOREIGN KEY(IdMenu)
            REFERENCES dbo.MenuOpciones(IdMenu),
        CONSTRAINT UQ_UsuarioMenu UNIQUE(IdUsuario,IdMenu)
    );
END;
GO

-- Nivel 1
MERGE dbo.MenuOpciones AS d
USING (VALUES
 (N'Dashboard',    N'▦',1),
 (N'Asignaciones', N'✓',2),
 (N'Ejecución',    N'▶',3),
 (N'Monitoreo',    N'◉',4)
) AS o(NombreMenu,Icono,Orden)
ON d.IdMenuPadre IS NULL AND d.NombreMenu=o.NombreMenu
WHEN MATCHED THEN UPDATE SET Nivel=1,Icono=o.Icono,Orden=o.Orden,Estado=1
WHEN NOT MATCHED THEN
 INSERT(IdMenuPadre,Nivel,NombreMenu,Url,Icono,Orden,Estado)
 VALUES(NULL,1,o.NombreMenu,NULL,o.Icono,o.Orden,1);
GO

DECLARE @Dashboard INT=(SELECT TOP(1) IdMenu FROM dbo.MenuOpciones WHERE IdMenuPadre IS NULL AND NombreMenu=N'Dashboard');
DECLARE @Asignaciones INT=(SELECT TOP(1) IdMenu FROM dbo.MenuOpciones WHERE IdMenuPadre IS NULL AND NombreMenu=N'Asignaciones');
DECLARE @Ejecucion INT=(SELECT TOP(1) IdMenu FROM dbo.MenuOpciones WHERE IdMenuPadre IS NULL AND NombreMenu=N'Ejecución');
DECLARE @Monitoreo INT=(SELECT TOP(1) IdMenu FROM dbo.MenuOpciones WHERE IdMenuPadre IS NULL AND NombreMenu=N'Monitoreo');

-- Nivel 2. Las opciones sin formulario desarrollado se registran con Url=NULL.
MERGE dbo.MenuOpciones AS d
USING (VALUES
 (@Dashboard,    N'Visualización General',          N'~/Mapa.aspx',          1),
 (@Asignaciones, N'Asignación de Lecturación',      NULL,                    1),
 (@Asignaciones, N'Asignación de Cortes',           N'~/AsigCortes.aspx',    2),
 (@Asignaciones, N'Asignación de Reconexión',       N'~/AsigReconexion.aspx',3),
 (@Asignaciones, N'Asignación de Orden de Trabajo', NULL,                    4),
 (@Ejecucion,    N'Ejecución de Lecturación',       NULL,                    1),
 (@Ejecucion,    N'Ejecución de Cortes',            N'~/EjeCortes.aspx',     2),
 (@Ejecucion,    N'Ejecución de Reconexión',        N'~/EjeReconexion.aspx', 3),
 (@Ejecucion,    N'Ejecución de Orden de Trabajo',  NULL,                    4),
 (@Monitoreo,    N'Monitoreo de Lecturación',       NULL,                    1),
 (@Monitoreo,    N'Monitoreo de Cortes',            N'~/MoniCortes.aspx',    2),
 (@Monitoreo,    N'Monitoreo de Reconexión',        N'~/MoniReconexion.aspx',3),
 (@Monitoreo,    N'Monitoreo de Orden de Trabajo',  NULL,                    4)
) AS o(IdMenuPadre,NombreMenu,Url,Orden)
ON d.IdMenuPadre=o.IdMenuPadre AND d.NombreMenu=o.NombreMenu
WHEN MATCHED THEN UPDATE SET Nivel=2,Url=o.Url,Orden=o.Orden,Estado=1
WHEN NOT MATCHED THEN
 INSERT(IdMenuPadre,Nivel,NombreMenu,Url,Icono,Orden,Estado)
 VALUES(o.IdMenuPadre,2,o.NombreMenu,o.Url,NULL,o.Orden,1);

-- El administrador recibe todas las opciones y permisos.
INSERT dbo.UsuarioMenu(IdUsuario,IdMenu,PuedeVer,PuedeCrear,PuedeEditar,PuedeEliminar)
SELECT u.IdUsuario,m.IdMenu,1,1,1,1
FROM dbo.Usuarios u CROSS JOIN dbo.MenuOpciones m
WHERE u.Login=N'admin' AND m.Estado=1
  AND NOT EXISTS(SELECT 1 FROM dbo.UsuarioMenu x WHERE x.IdUsuario=u.IdUsuario AND x.IdMenu=m.IdMenu);

-- Permisos operativos iniciales de Juan (Lecturador).
INSERT dbo.UsuarioMenu(IdUsuario,IdMenu,PuedeVer)
SELECT u.IdUsuario,m.IdMenu,1
FROM dbo.Usuarios u CROSS JOIN dbo.MenuOpciones m
WHERE u.Login=N'Juan'
  AND (m.NombreMenu IN(N'Dashboard',N'Ejecución',N'Monitoreo',N'Visualización General',
      N'Ejecución de Lecturación',N'Monitoreo de Lecturación'))
  AND NOT EXISTS(SELECT 1 FROM dbo.UsuarioMenu x WHERE x.IdUsuario=u.IdUsuario AND x.IdMenu=m.IdMenu);

-- Permisos operativos iniciales de Pedro (Cortador).
INSERT dbo.UsuarioMenu(IdUsuario,IdMenu,PuedeVer)
SELECT u.IdUsuario,m.IdMenu,1
FROM dbo.Usuarios u CROSS JOIN dbo.MenuOpciones m
WHERE u.Login=N'Pedro'
  AND (m.NombreMenu IN(N'Dashboard',N'Ejecución',N'Monitoreo',N'Visualización General',
      N'Ejecución de Cortes',N'Monitoreo de Cortes'))
  AND NOT EXISTS(SELECT 1 FROM dbo.UsuarioMenu x WHERE x.IdUsuario=u.IdUsuario AND x.IdMenu=m.IdMenu);
GO

SELECT m.IdMenu,m.Nivel,p.NombreMenu AS MenuPadre,m.NombreMenu,m.Url,m.Orden
FROM dbo.MenuOpciones m
LEFT JOIN dbo.MenuOpciones p ON p.IdMenu=m.IdMenuPadre
ORDER BY COALESCE(p.Orden,m.Orden),m.Nivel,m.Orden;
GO
