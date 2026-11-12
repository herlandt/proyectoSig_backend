/* =====================================================================
   VisorDatosSIG 2026 · 07_Operaciones.sql  (segunda parte del proyecto)
   Asignación de cortes y reconexiones con ruta óptima, ejecución en campo
   desde la aplicación móvil y monitoreo del personal.

   Ampliación documentada en docs/Decisiones.md: NO modifica las tablas
   oficiales del diseño V13; solo agrega tablas auxiliares, reorganiza el
   menú (dbo.MenuOpciones / dbo.UsuarioMenu) y concede permisos.
   Re-ejecutable: cada objeto se crea solo si no existe.
   Ejecutar con:  sqlcmd -S . -E -I -f 65001 -i 07_Operaciones.sql
   ===================================================================== */
USE VisorDatosSIG;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- Parámetros operativos (una sola fila) ---------- */
IF OBJECT_ID(N'dbo.ConfiguracionOperativa', N'U') IS NULL
CREATE TABLE dbo.ConfiguracionOperativa(
    IdConfiguracion TINYINT NOT NULL CONSTRAINT PK_ConfiguracionOperativa PRIMARY KEY
        CONSTRAINT CK_ConfiguracionOperativa_Unica CHECK (IdConfiguracion = 1),
    VelocidadKmH DECIMAL(5,1) NOT NULL CONSTRAINT DF_ConfOp_Velocidad DEFAULT (20)
        CONSTRAINT CK_ConfOp_Velocidad CHECK (VelocidadKmH BETWEEN 1 AND 120),
    MinutosPorParada DECIMAL(5,1) NOT NULL CONSTRAINT DF_ConfOp_MinParada DEFAULT (5)
        CONSTRAINT CK_ConfOp_MinParada CHECK (MinutosPorParada BETWEEN 0 AND 240),
    IntervaloGpsSegundos INT NOT NULL CONSTRAINT DF_ConfOp_Gps DEFAULT (30)
        CONSTRAINT CK_ConfOp_Gps CHECK (IntervaloGpsSegundos BETWEEN 5 AND 600),
    RadioValidacionMetros INT NOT NULL CONSTRAINT DF_ConfOp_Radio DEFAULT (60)
        CONSTRAINT CK_ConfOp_Radio CHECK (RadioValidacionMetros BETWEEN 10 AND 1000),
    MaximoFotos TINYINT NOT NULL CONSTRAINT DF_ConfOp_Fotos DEFAULT (3)
        CONSTRAINT CK_ConfOp_Fotos CHECK (MaximoFotos BETWEEN 1 AND 10),
    MaximoParadas INT NOT NULL CONSTRAINT DF_ConfOp_Paradas DEFAULT (60)
        CONSTRAINT CK_ConfOp_Paradas CHECK (MaximoParadas BETWEEN 1 AND 200),
    FechaModificacion DATETIME2 NOT NULL CONSTRAINT DF_ConfOp_Fecha DEFAULT (SYSDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM dbo.ConfiguracionOperativa)
    INSERT dbo.ConfiguracionOperativa(IdConfiguracion) VALUES (1);
GO

/* ---------- Asignaciones (cabecera): una ruta de un trabajador ----------
   Un trabajador tiene como máximo una asignación activa por tipo: al asignarle
   más códigos se recalcula la misma ruta, acumulada con sus pendientes. */
IF OBJECT_ID(N'dbo.Asignacion', N'U') IS NULL
CREATE TABLE dbo.Asignacion(
    IdAsignacion INT IDENTITY CONSTRAINT PK_Asignacion PRIMARY KEY,
    Tipo NVARCHAR(12) NOT NULL CONSTRAINT CK_Asignacion_Tipo CHECK (Tipo IN (N'Corte', N'Reconexion')),
    IdTrabajador INT NOT NULL CONSTRAINT FK_Asignacion_Trabajador REFERENCES dbo.Usuarios(IdUsuario),
    IdAsignadoPor INT NOT NULL CONSTRAINT FK_Asignacion_AsignadoPor REFERENCES dbo.Usuarios(IdUsuario),
    FechaAsignacion DATETIME2 NOT NULL CONSTRAINT DF_Asignacion_Fecha DEFAULT (SYSDATETIME()),
    FechaActualizacion DATETIME2 NOT NULL CONSTRAINT DF_Asignacion_FechaAct DEFAULT (SYSDATETIME()),
    Estado NVARCHAR(12) NOT NULL CONSTRAINT DF_Asignacion_Estado DEFAULT (N'Pendiente')
        CONSTRAINT CK_Asignacion_Estado CHECK (Estado IN (N'Pendiente', N'EnCurso', N'Completada', N'Anulada')),
    PartidaGeom geometry NOT NULL,
    DistanciaMetros FLOAT NOT NULL CONSTRAINT DF_Asignacion_Distancia DEFAULT (0),
    DuracionEstimadaMin FLOAT NOT NULL CONSTRAINT DF_Asignacion_Duracion DEFAULT (0),
    RutaGeom geometry NULL,                 -- recorrido completo por dbo.Vias (LineString)
    FechaInicio DATETIME2 NULL,             -- el trabajador pulsó «Iniciar recorrido»
    FechaFin DATETIME2 NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Asignacion_Trabajador' AND object_id = OBJECT_ID(N'dbo.Asignacion'))
    CREATE INDEX IX_Asignacion_Trabajador ON dbo.Asignacion(IdTrabajador, Tipo, Estado);
GO

/* ---------- Paradas de la ruta y su ejecución en campo ---------- */
IF OBJECT_ID(N'dbo.AsignacionDetalle', N'U') IS NULL
CREATE TABLE dbo.AsignacionDetalle(
    IdDetalle INT IDENTITY CONSTRAINT PK_AsignacionDetalle PRIMARY KEY,
    IdAsignacion INT NOT NULL CONSTRAINT FK_Detalle_Asignacion REFERENCES dbo.Asignacion(IdAsignacion),
    IdCodigo INT NOT NULL CONSTRAINT FK_Detalle_Codigo REFERENCES dbo.CodigosFijos(IdCodigo),
    Orden INT NOT NULL,
    EstadoCodigoAlAsignar TINYINT NOT NULL,          -- 2 Para Corte · 3 Cortado
    DistanciaTramoMetros FLOAT NOT NULL,             -- por calle, desde la parada anterior o la partida
    DistanciaAccesoMetros FLOAT NOT NULL,            -- de la calle a la casa (línea recta)
    TramoGeom geometry NULL,                         -- trazado del tramo por dbo.Vias
    Estado NVARCHAR(12) NOT NULL CONSTRAINT DF_Detalle_Estado DEFAULT (N'Pendiente')
        CONSTRAINT CK_Detalle_Estado CHECK (Estado IN (N'Pendiente', N'Ejecutada', N'Observada', N'Anulada')),
    -- Registro en campo (aplicación móvil)
    Resultado NVARCHAR(12) NULL CONSTRAINT CK_Detalle_Resultado CHECK (Resultado IN (N'Cortado', N'Reconectado', N'Observado')),
    NumeroMedidor NVARCHAR(30) NULL,
    Lectura DECIMAL(12,2) NULL,
    Observacion NVARCHAR(300) NULL,
    FechaEjecucion DATETIME2 NULL,                   -- hora del servidor al recibir
    FechaDispositivo DATETIME2 NULL,                 -- hora del celular al guardar
    UbicacionEjecucion geometry NULL,                -- GPS al guardar (obligatorio para registrar)
    PrecisionMetros FLOAT NULL,
    DistanciaAlPuntoMetros FLOAT NULL,
    DentroDelRadio BIT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Detalle_Asignacion' AND object_id = OBJECT_ID(N'dbo.AsignacionDetalle'))
    CREATE INDEX IX_Detalle_Asignacion ON dbo.AsignacionDetalle(IdAsignacion, Orden);
-- Un código fijo no puede estar pendiente en dos rutas a la vez.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Detalle_CodigoPendiente' AND object_id = OBJECT_ID(N'dbo.AsignacionDetalle'))
    CREATE UNIQUE INDEX UX_Detalle_CodigoPendiente ON dbo.AsignacionDetalle(IdCodigo) WHERE Estado = N'Pendiente';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Detalle_Ejecucion' AND object_id = OBJECT_ID(N'dbo.AsignacionDetalle'))
    CREATE INDEX IX_Detalle_Ejecucion ON dbo.AsignacionDetalle(FechaEjecucion) INCLUDE (IdAsignacion, Estado, Resultado);
GO

/* ---------- Fotografías de evidencia (archivos en disco, metadatos aquí) ---------- */
IF OBJECT_ID(N'dbo.EvidenciaFoto', N'U') IS NULL
CREATE TABLE dbo.EvidenciaFoto(
    IdFoto INT IDENTITY CONSTRAINT PK_EvidenciaFoto PRIMARY KEY,
    IdDetalle INT NOT NULL CONSTRAINT FK_Foto_Detalle REFERENCES dbo.AsignacionDetalle(IdDetalle),
    NombreArchivo NVARCHAR(260) NOT NULL,            -- relativo a la carpeta de evidencias
    TipoContenido NVARCHAR(50) NOT NULL,
    TamanoBytes INT NOT NULL,
    HashSha256 CHAR(64) NOT NULL,
    FechaRegistro DATETIME2 NOT NULL CONSTRAINT DF_Foto_Fecha DEFAULT (SYSDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Foto_Detalle' AND object_id = OBJECT_ID(N'dbo.EvidenciaFoto'))
    CREATE INDEX IX_Foto_Detalle ON dbo.EvidenciaFoto(IdDetalle);
GO

/* ---------- Posiciones GPS del personal (monitoreo) ---------- */
IF OBJECT_ID(N'dbo.PosicionTrabajador', N'U') IS NULL
CREATE TABLE dbo.PosicionTrabajador(
    IdPosicion BIGINT IDENTITY CONSTRAINT PK_PosicionTrabajador PRIMARY KEY,
    IdTrabajador INT NOT NULL CONSTRAINT FK_Posicion_Trabajador REFERENCES dbo.Usuarios(IdUsuario),
    IdAsignacion INT NULL CONSTRAINT FK_Posicion_Asignacion REFERENCES dbo.Asignacion(IdAsignacion),
    FechaHora DATETIME2 NOT NULL,                    -- hora del celular (hora local de Bolivia)
    FechaRecepcion DATETIME2 NOT NULL CONSTRAINT DF_Posicion_Recepcion DEFAULT (SYSDATETIME()),
    Latitud FLOAT NOT NULL CONSTRAINT CK_Posicion_Lat CHECK (Latitud BETWEEN -90 AND 90),
    Longitud FLOAT NOT NULL CONSTRAINT CK_Posicion_Lon CHECK (Longitud BETWEEN -180 AND 180),
    PrecisionMetros FLOAT NULL,
    VelocidadMs FLOAT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Posicion_Trabajador_Fecha' AND object_id = OBJECT_ID(N'dbo.PosicionTrabajador'))
    CREATE INDEX IX_Posicion_Trabajador_Fecha ON dbo.PosicionTrabajador(IdTrabajador, FechaHora)
        INCLUDE (Latitud, Longitud, PrecisionMetros, IdAsignacion);
GO

/* ---------- Menú de la segunda parte ----------
   Mínimo pedido por el docente: Dashboard general, Asignaciones, Visualización
   general, Monitoreo, Información geográfica, usuario y cerrar sesión; se agrega
   Ejecución, como en la aplicación de referencia. Las opciones de la parte 1
   que cambian de lugar quedan inactivas (Estado = 0), sin borrarse. */
MERGE dbo.MenuOpciones AS d
USING (VALUES
 (N'Dashboard',              N'bi-speedometer2',    1),
 (N'Asignaciones',           N'bi-signpost-split',  2),
 (N'Ejecución',              N'bi-clipboard-check', 3),
 (N'Monitoreo',              N'bi-broadcast',       4),
 (N'Información geográfica', N'bi-map',             5),
 (N'Administración',         N'bi-gear',            6),
 (N'Cuenta',                 N'bi-person',          7),
 (N'Ayuda',                  N'bi-question-circle', 8)
) AS o(NombreMenu, Icono, Orden)
ON d.IdMenuPadre IS NULL AND d.NombreMenu = o.NombreMenu
WHEN MATCHED THEN UPDATE SET Nivel = 1, Icono = o.Icono, Orden = o.Orden, Estado = 1, Url = NULL
WHEN NOT MATCHED THEN
 INSERT(IdMenuPadre, Nivel, NombreMenu, Url, Icono, Orden, Estado)
 VALUES(NULL, 1, o.NombreMenu, NULL, o.Icono, o.Orden, 1);
GO

DECLARE @padres TABLE(Nombre NVARCHAR(100) PRIMARY KEY, IdMenu INT);
INSERT @padres SELECT NombreMenu, IdMenu FROM dbo.MenuOpciones
 WHERE IdMenuPadre IS NULL
   AND NombreMenu IN (N'Dashboard', N'Asignaciones', N'Ejecución', N'Monitoreo',
                      N'Información geográfica', N'Administración', N'Cuenta', N'Ayuda');

DECLARE @hijos TABLE(Padre NVARCHAR(100), NombreMenu NVARCHAR(100), Url NVARCHAR(200), Icono NVARCHAR(50), Orden INT);
INSERT @hijos VALUES
  (N'Dashboard',              N'Dashboard general',          N'/',                           N'bi-pie-chart',        1),
  (N'Dashboard',              N'Dashboard técnico',          N'/Dashboard/Tecnico',          N'bi-bar-chart-line',   2),
  (N'Dashboard',              N'Visualización general',      N'/Mapa',                       N'bi-badge-3d',         3),
  (N'Asignaciones',           N'Asignación de cortes',       N'/Asignaciones/Cortes',        N'bi-scissors',         1),
  (N'Asignaciones',           N'Asignación de reconexión',   N'/Asignaciones/Reconexion',    N'bi-plug',             2),
  (N'Ejecución',              N'Ejecución de cortes',        N'/Ejecucion/Cortes',           N'bi-check2-square',    1),
  (N'Ejecución',              N'Ejecución de reconexión',    N'/Ejecucion/Reconexion',       N'bi-check2-circle',    2),
  (N'Monitoreo',              N'Monitoreo de personal',      N'/Monitoreo',                  N'bi-person-walking',   1),
  (N'Información geográfica', N'Visor 2D y capas',           N'/Visor',                      N'bi-globe-americas',   1),
  (N'Información geográfica', N'Consultas y filtros',        N'/Consultas',                  N'bi-search',           2),
  (N'Administración',         N'Usuarios',                   N'/Administracion/Usuarios',    N'bi-people',           1),
  (N'Administración',         N'Perfiles y permisos',        N'/Administracion/Permisos',    N'bi-shield-lock',      2),
  (N'Administración',         N'Configuración operativa',    N'/Administracion/Configuracion', N'bi-sliders',        3),
  (N'Administración',         N'Catálogo de capas',          N'/Administracion/Capas',       N'bi-stack',            4),
  (N'Administración',         N'Historial de migraciones',   N'/Administracion/Migraciones', N'bi-clock-history',    5),
  (N'Administración',         N'Bitácora del sistema',       N'/Administracion/Bitacora',    N'bi-journal-text',     6),
  (N'Cuenta',                 N'Mi perfil',                  N'/Cuenta/Perfil',              N'bi-person-badge',     1),
  (N'Cuenta',                 N'Cerrar sesión',              N'/Cuenta/Salir',               N'bi-box-arrow-right',  2),
  (N'Ayuda',                  N'Manual de usuario',          N'/Ayuda/Manual',               N'bi-book',             1),
  (N'Ayuda',                  N'Acerca del sistema',         N'/Ayuda/AcercaDe',             N'bi-info-circle',      2);

MERGE dbo.MenuOpciones AS d
USING (SELECT p.IdMenu, h.NombreMenu, h.Url, h.Icono, h.Orden FROM @hijos h JOIN @padres p ON p.Nombre = h.Padre) AS o
ON d.IdMenuPadre = o.IdMenu AND d.NombreMenu = o.NombreMenu
WHEN MATCHED THEN UPDATE SET Nivel = 2, Url = o.Url, Icono = o.Icono, Orden = o.Orden, Estado = 1
WHEN NOT MATCHED THEN
 INSERT(IdMenuPadre, Nivel, NombreMenu, Url, Icono, Orden, Estado)
 VALUES(o.IdMenu, 2, o.NombreMenu, o.Url, o.Icono, o.Orden, 1);

-- Lo que no está en el menú nuevo queda inactivo (padres e hijos de la parte 1 y del script 08).
UPDATE m SET Estado = 0
  FROM dbo.MenuOpciones m
  LEFT JOIN dbo.MenuOpciones p ON p.IdMenu = m.IdMenuPadre
 WHERE NOT EXISTS (SELECT 1 FROM @padres x WHERE m.IdMenuPadre IS NULL AND x.Nombre = m.NombreMenu)
   AND NOT EXISTS (SELECT 1 FROM @hijos h WHERE h.Padre = p.NombreMenu AND h.NombreMenu = m.NombreMenu);
GO

/* ---------- Permisos de menú por usuario (misma regla que la API al crear usuarios) ----------
   Administrador: todas las opciones activas, con todos los permisos.
   Resto (Consultor y roles operativos): Dashboard general, Visualización general,
   Información geográfica, Cuenta y Ayuda. Los cortadores y reconectadores trabajan
   desde la aplicación móvil. */
DELETE dbo.UsuarioMenu;
INSERT dbo.UsuarioMenu(IdUsuario, IdMenu, PuedeVer, PuedeCrear, PuedeEditar, PuedeEliminar)
SELECT u.IdUsuario, m.IdMenu, 1, r.EsAdmin, r.EsAdmin, r.EsAdmin
  FROM dbo.Usuarios u
 CROSS APPLY (SELECT CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.UsuariosRoles ur JOIN dbo.Roles ro ON ro.IdRol = ur.IdRol
         WHERE ur.IdUsuario = u.IdUsuario AND ro.NombreRol = N'Administrador') THEN 1 ELSE 0 END AS BIT) AS EsAdmin) r
  JOIN dbo.MenuOpciones m ON m.Estado = 1
  LEFT JOIN dbo.MenuOpciones p ON p.IdMenu = m.IdMenuPadre
 WHERE r.EsAdmin = 1
    OR (COALESCE(p.NombreMenu, m.NombreMenu) IN (N'Dashboard', N'Información geográfica', N'Cuenta', N'Ayuda')
        AND m.NombreMenu <> N'Dashboard técnico');
GO

/* ---------- Permisos de base de datos ---------- */
IF DATABASE_PRINCIPAL_ID(N'rol_visor_web') IS NULL CREATE ROLE rol_visor_web;
GO
GRANT SELECT, UPDATE ON dbo.ConfiguracionOperativa TO rol_visor_web;
GRANT SELECT, INSERT, UPDATE ON dbo.Asignacion TO rol_visor_web;
GRANT SELECT, INSERT, UPDATE ON dbo.AsignacionDetalle TO rol_visor_web;
GRANT SELECT, INSERT ON dbo.EvidenciaFoto TO rol_visor_web;
GRANT SELECT, INSERT ON dbo.PosicionTrabajador TO rol_visor_web;
GRANT UPDATE (Estado) ON dbo.CodigosFijos TO rol_visor_web;     -- la ejecución cambia el estado del servicio
GO

/* ---------- Verificación ---------- */
SELECT N'ConfiguracionOperativa' AS Tabla, COUNT(*) AS Filas FROM dbo.ConfiguracionOperativa
UNION ALL SELECT N'Asignacion', COUNT(*) FROM dbo.Asignacion
UNION ALL SELECT N'AsignacionDetalle', COUNT(*) FROM dbo.AsignacionDetalle
UNION ALL SELECT N'EvidenciaFoto', COUNT(*) FROM dbo.EvidenciaFoto
UNION ALL SELECT N'PosicionTrabajador', COUNT(*) FROM dbo.PosicionTrabajador
UNION ALL SELECT N'Opciones de menú activas', COUNT(*) FROM dbo.MenuOpciones WHERE Estado = 1;
PRINT N'07_Operaciones.sql: tablas operativas, menú de la parte 2 y permisos creados/verificados.';
GO
