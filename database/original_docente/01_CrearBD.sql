IF DB_ID(N'VisorDatosSIG') IS NULL CREATE DATABASE VisorDatosSIG;
GO
USE VisorDatosSIG;
GO

CREATE TABLE dbo.Usuarios(
    IdUsuario INT IDENTITY PRIMARY KEY,
    Login NVARCHAR(50) NOT NULL UNIQUE,
    Nombre NVARCHAR(120) NOT NULL,
    PasswordHash VARBINARY(32) NOT NULL,
    PasswordSalt VARBINARY(32) NOT NULL,
    Iteraciones INT NOT NULL CONSTRAINT DF_Usuarios_Iteraciones DEFAULT(100000),
    Activo BIT NOT NULL CONSTRAINT DF_Usuarios_Activo DEFAULT(1),
    FechaRegistro DATETIME2 NOT NULL CONSTRAINT DF_Usuarios_Fecha DEFAULT(SYSDATETIME())
);

CREATE TABLE dbo.Roles(
    IdRol INT IDENTITY(1,1) PRIMARY KEY,
    NombreRol VARCHAR(50) NOT NULL UNIQUE,
    Descripcion VARCHAR(200) NULL,
    Estado BIT NOT NULL CONSTRAINT DF_Roles_Estado DEFAULT(1)
);

CREATE TABLE dbo.UsuariosRoles(
    IdUsuarioRol INT IDENTITY(1,1) PRIMARY KEY,
    IdUsuario INT NOT NULL,
    IdRol INT NOT NULL,
    CONSTRAINT FK_UsuariosRoles_Usuarios FOREIGN KEY(IdUsuario)
        REFERENCES dbo.Usuarios(IdUsuario),
    CONSTRAINT FK_UsuariosRoles_Roles FOREIGN KEY(IdRol)
        REFERENCES dbo.Roles(IdRol),
    CONSTRAINT UQ_UsuariosRoles UNIQUE(IdUsuario,IdRol)
);

CREATE TABLE dbo.MenuOpciones(
    IdMenu INT IDENTITY(1,1) PRIMARY KEY,
    IdMenuPadre INT NULL,
    Nivel INT NOT NULL,
    NombreMenu NVARCHAR(100) NOT NULL,
    Url NVARCHAR(200) NULL,
    Icono NVARCHAR(50) NULL,
    Orden INT NOT NULL CONSTRAINT DF_MenuOpciones_Orden DEFAULT(0),
    Estado BIT NOT NULL CONSTRAINT DF_MenuOpciones_Estado DEFAULT(1),
    CONSTRAINT FK_MenuOpciones_Padre FOREIGN KEY(IdMenuPadre) REFERENCES dbo.MenuOpciones(IdMenu),
    CONSTRAINT CK_MenuOpciones_Nivel CHECK(Nivel IN(1,2,3))
);

CREATE TABLE dbo.UsuarioMenu(
    IdUsuarioMenu INT IDENTITY(1,1) PRIMARY KEY,
    IdUsuario INT NOT NULL,
    IdMenu INT NOT NULL,
    PuedeVer BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeVer DEFAULT(1),
    PuedeCrear BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeCrear DEFAULT(0),
    PuedeEditar BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeEditar DEFAULT(0),
    PuedeEliminar BIT NOT NULL CONSTRAINT DF_UsuarioMenu_PuedeEliminar DEFAULT(0),
    CONSTRAINT FK_UsuarioMenu_Usuarios FOREIGN KEY(IdUsuario) REFERENCES dbo.Usuarios(IdUsuario),
    CONSTRAINT FK_UsuarioMenu_Menu FOREIGN KEY(IdMenu) REFERENCES dbo.MenuOpciones(IdMenu),
    CONSTRAINT UQ_UsuarioMenu UNIQUE(IdUsuario,IdMenu)
);

CREATE TABLE dbo.CodigosFijos(
    IdCodigo INT IDENTITY PRIMARY KEY,
    CodF_SQL INT NULL,
    CodF_SIG NVARCHAR(25) NULL,
    CodFijo INT NULL,
    Nombre NVARCHAR(120) NULL,
    Estado TINYINT NOT NULL CONSTRAINT DF_CodigosFijos_Estado DEFAULT(1),
    FechaCambioEstado DATETIME2 NOT NULL
        CONSTRAINT DF_CodigosFijos_FechaCambioEstado DEFAULT(SYSDATETIME()),
    IdLote INT NULL,
    Longitud FLOAT NULL,
    Latitud FLOAT NULL,
    Geom geometry NULL,
    CONSTRAINT CK_CodigosFijos_Estado CHECK (Estado BETWEEN 1 AND 5)
);
CREATE INDEX IX_CodigosFijos_CodFijo ON dbo.CodigosFijos(CodFijo);
CREATE INDEX IX_CodigosFijos_Nombre ON dbo.CodigosFijos(Nombre);
CREATE INDEX IX_CodigosFijos_Estado ON dbo.CodigosFijos(Estado);
CREATE INDEX IX_CodigosFijos_IdLote ON dbo.CodigosFijos(IdLote);

CREATE TABLE dbo.Manzanas(
    IdManzana INT IDENTITY PRIMARY KEY,
    IdOrigen INT NULL,
    UV_MZA NVARCHAR(20) NULL,
    UV NVARCHAR(15) NULL,
    MZA NVARCHAR(10) NULL,
    Geom geometry NULL
);
CREATE INDEX IX_Manzanas_UV_MZA ON dbo.Manzanas(UV,MZA);

CREATE TABLE dbo.Lotes(
    IdLote INT IDENTITY PRIMARY KEY,
    IdOrigen INT NULL,
    NroLote NVARCHAR(15) NULL,
    IdManzana INT NULL,
    Geom geometry NULL,
    CONSTRAINT FK_Lotes_Manzanas FOREIGN KEY(IdManzana) REFERENCES dbo.Manzanas(IdManzana)
);
CREATE INDEX IX_Lotes_NroLote ON dbo.Lotes(NroLote);
ALTER TABLE dbo.CodigosFijos ADD CONSTRAINT FK_CodigosFijos_Lotes
    FOREIGN KEY(IdLote) REFERENCES dbo.Lotes(IdLote);

CREATE TABLE dbo.Vias(
    IdVia INT IDENTITY PRIMARY KEY,
    OBJECTID INT NULL,
    Nombre NVARCHAR(40) NULL,
    TipoVia NVARCHAR(30) NULL,
    OSMID NVARCHAR(20) NULL,
    Geom geometry NULL
);

INSERT dbo.Usuarios(Login,Nombre,PasswordHash,PasswordSalt,Iteraciones)
VALUES(
    N'admin',
    N'Administrador',
    0x599F3972C925D7D968B215FB5385865FDF961BCA18BB332E13AB9F014903F89B,
    0x82A4FC4EE67761C1A4E95BCD6BF9CF9B63344B9E665C839C8F9F654D1718B665,
    100000
);

INSERT dbo.Roles(NombreRol,Descripcion) VALUES
('Administrador','Administrador del Sistema'),
('Catastro','Rol para crear, modificar y eliminar Manzanas, Lotes, Codigo Fijo y Vias'),
('Lecturador','Rol para lecturar medidores'),
('Cortador','Rol para cortar servicios'),
('Reconexion','Rol para la reconnexion por corte');

INSERT dbo.Usuarios(Login,Nombre,PasswordHash,PasswordSalt,Iteraciones)
VALUES
(N'Juan',N'Juan',
 0xB14BC9A077C4CC29BC030A25AD517456D88224BBB57ECA2C5D977C4E85131676,
 0x3F2B95B127A4E8C161A840BDF72EA6623E318F7969B3CCF7A7B66BA33A90D89C,100000),
(N'Pedro',N'Pedro',
 0xC1BA4D699543F1E1D12C302CD0FD1BB4DDECF6A9C55620B9CC718E918930A528,
 0xA4E75D92036F1B847CD4298A18D3F16B2746AE50A81683CD375943EE15F7BD01,100000);

INSERT dbo.UsuariosRoles(IdUsuario,IdRol)
SELECT u.IdUsuario,r.IdRol
FROM dbo.Usuarios u
CROSS JOIN dbo.Roles r
WHERE (u.Login=N'admin' AND r.NombreRol='Administrador')
   OR (u.Login=N'Juan' AND r.NombreRol='Lecturador')
   OR (u.Login=N'Pedro' AND r.NombreRol='Cortador');

CREATE SPATIAL INDEX SIX_CodigosFijos_Geom ON dbo.CodigosFijos(Geom)
USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
CREATE SPATIAL INDEX SIX_Manzanas_Geom ON dbo.Manzanas(Geom)
USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
CREATE SPATIAL INDEX SIX_Lotes_Geom ON dbo.Lotes(Geom)
USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
CREATE SPATIAL INDEX SIX_Vias_Geom ON dbo.Vias(Geom)
USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
GO

CREATE OR ALTER TRIGGER dbo.TR_CodigosFijos_FechaCambioEstado
ON dbo.CodigosFijos
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT UPDATE(Estado) RETURN;

    UPDATE c
       SET FechaCambioEstado = SYSDATETIME()
      FROM dbo.CodigosFijos c
      JOIN inserted i ON i.IdCodigo = c.IdCodigo
      JOIN deleted d ON d.IdCodigo = i.IdCodigo
     WHERE i.Estado <> d.Estado;
END;
GO

CREATE OR ALTER PROCEDURE dbo.sp_BuscarInmueble
    @Texto NVARCHAR(100)=NULL,
    @UV NVARCHAR(15)=NULL,
    @Mza NVARCHAR(10)=NULL,
    @Lote NVARCHAR(15)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP(100)
        c.IdCodigo, c.CodFijo, c.Nombre, c.Estado, c.FechaCambioEstado,
        m.UV, m.MZA, l.NroLote,
        COALESCE(c.Latitud, c.Geom.STY) AS Latitud,
        COALESCE(c.Longitud, c.Geom.STX) AS Longitud
    FROM dbo.CodigosFijos c
    LEFT JOIN dbo.Lotes l ON l.IdLote=c.IdLote
    LEFT JOIN dbo.Manzanas m ON m.IdManzana=l.IdManzana
    WHERE (@Texto IS NULL OR CONVERT(NVARCHAR(30),c.CodFijo)=@Texto
           OR c.Nombre LIKE N'%' + @Texto + N'%')
      AND (@UV IS NULL OR m.UV=@UV)
      AND (@Mza IS NULL OR m.MZA=@Mza)
      AND (@Lote IS NULL OR l.NroLote=@Lote)
    ORDER BY c.CodFijo;
END;
GO

CREATE OR ALTER PROCEDURE dbo.sp_ActualizarLoteCodigosFijos
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Completa IdManzana cuando la importación SHP dejó el vínculo vacío. */
    UPDATE l
       SET IdManzana = a.IdManzana
      FROM dbo.Lotes l
      CROSS APPLY (
          SELECT TOP (1) m.IdManzana
            FROM dbo.Manzanas m
           WHERE l.Geom IS NOT NULL
             AND m.Geom IS NOT NULL
             AND m.Geom.STSrid = l.Geom.STSrid
             AND m.Geom.Filter(l.Geom.STPointOnSurface()) = 1
             AND m.Geom.STIntersects(l.Geom.STPointOnSurface()) = 1
           ORDER BY m.IdManzana
      ) a
     WHERE l.IdManzana IS NULL;

    UPDATE c
       SET IdLote = a.IdLote
      FROM dbo.CodigosFijos c
      OUTER APPLY (
          SELECT TOP (1) l.IdLote
            FROM dbo.Lotes l
           WHERE c.Geom IS NOT NULL
             AND l.Geom IS NOT NULL
             AND l.Geom.STSrid = c.Geom.STSrid
             AND l.Geom.Filter(c.Geom) = 1
             AND l.Geom.STIntersects(c.Geom) = 1
           ORDER BY l.Geom.STArea(), l.IdLote
      ) a
     WHERE ISNULL(c.IdLote, -1) <> ISNULL(a.IdLote, -1);

    SELECT COUNT(*) AS TotalCodigos,
           COUNT(IdLote) AS AsociadosALote,
           COUNT(*) - COUNT(IdLote) AS SinLote
      FROM dbo.CodigosFijos;

    SELECT COUNT(*) AS TotalLotes,
           COUNT(IdManzana) AS AsociadosAManzana,
           COUNT(*) - COUNT(IdManzana) AS SinManzana
      FROM dbo.Lotes;
END;
GO

/* Usuario inicial: admin / Admin123!
   Cambiar la contraseña antes de publicar la aplicación. */
