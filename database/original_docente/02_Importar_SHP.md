# Importación de las capas SHP

## Preparación en QGIS

1. Cargue cada archivo SHP.
2. Para las capas UTM asigne/verifique `EPSG:32720`.
3. Utilice la capa corregida ubicada en `Datos/Exp_MapaBase_MZA`.
4. Exporte todas las capas a `EPSG:4326`.
5. Importe a SQL Server mediante el complemento **MSSQL** o mediante
   `ogr2ogr`.

La capa corregida de manzanas fue verificada con los siguientes datos:

- Registros: 863.
- Geometría: PolygonZ.
- Extensión X: 714920,702 a 721350,099.
- Extensión Y: 8185008,529 a 8190311,166.
- Sistema de coordenadas de origen: EPSG:32720.

## Correspondencia

| SHP | Tabla SQL | Geometría |
|---|---|---|
| Exp_CodigoFijo | CodigosFijos | Geom |
| Exp_MapaBase_LOTES | Lotes | Geom |
| Exp_MapaBase_MZA | Manzanas | Geom |
| Exp_MapaBase_VIAS | Vias | Geom |

Todas las geometrías almacenadas deben tener SRID 4326:

```sql
SELECT TOP 10 Geom.STSrid, Geom.STIsValid(), Geom.STAsText()
FROM dbo.Manzanas;
```

Después de importar lotes y manzanas, establezca la relación espacial:

```sql
UPDATE l
   SET IdManzana=m.IdManzana
FROM dbo.Lotes l
JOIN dbo.Manzanas m
  ON l.Geom.STCentroid().STIntersects(m.Geom)=1
WHERE l.Geom IS NOT NULL AND m.Geom IS NOT NULL;
```

## Validaciones

```sql
SELECT 'CodigosFijos',COUNT(*) FROM dbo.CodigosFijos
UNION ALL SELECT 'Lotes',COUNT(*) FROM dbo.Lotes
UNION ALL SELECT 'Manzanas',COUNT(*) FROM dbo.Manzanas
UNION ALL SELECT 'Vias',COUNT(*) FROM dbo.Vias;

SELECT IdManzana FROM dbo.Manzanas
WHERE Geom IS NULL OR Geom.STIsValid()=0;
```

El conteo esperado para `Manzanas` después de la importación es **863**.
