/* =====================================================================
   VisorDatosSIG 2026 · 01_CrearBD.sql
   Crea la base de datos y el DISEÑO FÍSICO OFICIAL entregado por el
   docente (ScriptDatabaseV13/01_CrearBD.sql), sin cambios de nombres,
   tipos, claves ni nulabilidad. Los datos iniciales (roles, usuarios,
   menú) se movieron a 03_Seguridad.sql.

   Es re-ejecutable: cada objeto se crea solo si no existe.
   Ejecutar con:  sqlcmd -S . -E -i 01_CrearBD.sql
   ===================================================================== */
IF DB_ID(N'VisorDatosSIG') IS NULL CREATE DATABASE VisorDatosSIG;
GO
/* Opción de base (no de esquema): los lectores del visor no se bloquean
   mientras el migrador mantiene abierta su transacción de carga. */
IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = N'VisorDatosSIG') = 0
    ALTER DATABASE VisorDatosSIG SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
USE VisorDatosSIG;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- Seguridad ---------- */
IF OBJECT_ID(N'dbo.Usuarios', N'U') IS NULL
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

IF OBJECT_ID(N'dbo.Roles', N'U') IS NULL
CREATE TABLE dbo.Roles(
    IdRol INT IDENTITY(1,1) PRIMARY KEY,
    NombreRol VARCHAR(50) NOT NULL UNIQUE,
    Descripcion VARCHAR(200) NULL,
    Estado BIT NOT NULL CONSTRAINT DF_Roles_Estado DEFAULT(1)
);

IF OBJECT_ID(N'dbo.UsuariosRoles', N'U') IS NULL
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

IF OBJECT_ID(N'dbo.MenuOpciones', N'U') IS NULL
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

IF OBJECT_ID(N'dbo.UsuarioMenu', N'U') IS NULL
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
GO

/* ---------- Capas geográficas (SRID 4326, X = longitud, Y = latitud) ---------- */
IF OBJECT_ID(N'dbo.CodigosFijos', N'U') IS NULL
BEGIN
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
END;

IF OBJECT_ID(N'dbo.Manzanas', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Manzanas(
        IdManzana INT IDENTITY PRIMARY KEY,
        IdOrigen INT NULL,
        UV_MZA NVARCHAR(20) NULL,
        UV NVARCHAR(15) NULL,
        MZA NVARCHAR(10) NULL,
        Geom geometry NULL
    );
    CREATE INDEX IX_Manzanas_UV_MZA ON dbo.Manzanas(UV,MZA);
END;

IF OBJECT_ID(N'dbo.Lotes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Lotes(
        IdLote INT IDENTITY PRIMARY KEY,
        IdOrigen INT NULL,
        NroLote NVARCHAR(15) NULL,
        IdManzana INT NULL,
        Geom geometry NULL,
        CONSTRAINT FK_Lotes_Manzanas FOREIGN KEY(IdManzana) REFERENCES dbo.Manzanas(IdManzana)
    );
    CREATE INDEX IX_Lotes_NroLote ON dbo.Lotes(NroLote);
END;

IF OBJECT_ID(N'dbo.FK_CodigosFijos_Lotes', N'F') IS NULL
    ALTER TABLE dbo.CodigosFijos ADD CONSTRAINT FK_CodigosFijos_Lotes
        FOREIGN KEY(IdLote) REFERENCES dbo.Lotes(IdLote);

IF OBJECT_ID(N'dbo.Vias', N'U') IS NULL
CREATE TABLE dbo.Vias(
    IdVia INT IDENTITY PRIMARY KEY,
    OBJECTID INT NULL,
    Nombre NVARCHAR(40) NULL,
    TipoVia NVARCHAR(30) NULL,
    OSMID NVARCHAR(20) NULL,
    Geom geometry NULL
);
GO

/* ---------- Índices espaciales (definición oficial) ----------
   La caja (-180,-90,180,90) es la del diseño entregado. Para el área real
   de San Ignacio de Velasco conviene reconstruirlos con una caja ajustada:
   EXEC dbo.sp_ReconstruirIndicesEspaciales (ver 02_Objetos.sql). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'SIX_CodigosFijos_Geom')
    CREATE SPATIAL INDEX SIX_CodigosFijos_Geom ON dbo.CodigosFijos(Geom)
    USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'SIX_Manzanas_Geom')
    CREATE SPATIAL INDEX SIX_Manzanas_Geom ON dbo.Manzanas(Geom)
    USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'SIX_Lotes_Geom')
    CREATE SPATIAL INDEX SIX_Lotes_Geom ON dbo.Lotes(Geom)
    USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'SIX_Vias_Geom')
    CREATE SPATIAL INDEX SIX_Vias_Geom ON dbo.Vias(Geom)
    USING GEOMETRY_GRID WITH (BOUNDING_BOX=(-180,-90,180,90));
GO

/* ---------- Trigger oficial: fecha de cambio de estado ---------- */
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

PRINT N'01_CrearBD.sql: diseño físico oficial creado/verificado.';
GO
