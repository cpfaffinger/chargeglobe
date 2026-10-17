-- Builds the region polygons from the Overpass answer (psql variable "file": path readable by the server)
-- and prints them as one GeoJSON FeatureCollection. Run by scripts/regions/build-regions.sh with the psql variables
-- "expected" (number of states), "codes" (text for the attribution) and "script" (name of the calling script).
\set ON_ERROR_STOP on
\pset tuples_only on
\pset format unaligned
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TEMP TABLE osm AS SELECT pg_read_file(:'file')::jsonb AS doc;
-- psql does not substitute variables inside the DO block below; it reads them from this table.
CREATE TEMP TABLE param AS SELECT :expected::int AS expected;

-- Outer and inner ways of every state relation; a border way belongs to both neighbouring states.
CREATE TEMP TABLE member AS
SELECT rel -> 'tags' ->> 'ISO3166-2' AS code,
       rel -> 'tags' ->> 'name' AS name,
       (member ->> 'ref')::bigint AS way_id,
       member -> 'geometry' AS geometry
FROM osm,
     jsonb_array_elements(doc -> 'elements') AS rel,
     jsonb_array_elements(rel -> 'members') AS member
WHERE rel ->> 'type' = 'relation'
  AND member ->> 'type' = 'way'
  AND member ->> 'role' IN ('outer', 'inner');

-- Each way is simplified once (about 50 m tolerance, end points stay), so neighbouring states keep identical
-- borders: the simplified states neither overlap nor leave gaps between each other.
CREATE TEMP TABLE way AS
SELECT w.way_id,
       ST_SimplifyPreserveTopology(
           ST_SetSRID(ST_MakeLine(array_agg(ST_MakePoint((point ->> 'lon')::float8, (point ->> 'lat')::float8) ORDER BY point_no)), 4326),
           0.0005) AS geom,
       ST_SetSRID(ST_MakeLine(array_agg(ST_MakePoint((point ->> 'lon')::float8, (point ->> 'lat')::float8) ORDER BY point_no)), 4326) AS original
FROM (SELECT DISTINCT ON (way_id) way_id, geometry FROM member ORDER BY way_id) AS w,
     jsonb_array_elements(w.geometry) WITH ORDINALITY AS g(point, point_no)
GROUP BY w.way_id;

-- A few simplified border ways cross each other near enclaves (Saxony, Thuringia); such a way keeps its original
-- shape, so that the rings of every state still close and neighbours still share their borders.
CREATE INDEX way_geom_idx ON way USING gist (geom);
UPDATE way SET geom = original
WHERE way_id IN (SELECT a.way_id FROM way AS a JOIN way AS b ON a.way_id <> b.way_id AND a.geom && b.geom AND ST_Crosses(a.geom, b.geom));

-- Rings from the lines; inner ways become holes (Vienna is a hole of Lower Austria).
CREATE TEMP TABLE state AS
SELECT m.code, m.name,
       ST_Multi(ST_BuildArea(ST_Collect(w.geom))) AS geom,
       ST_Multi(ST_BuildArea(ST_Collect(w.original))) AS original
FROM member AS m
JOIN way AS w USING (way_id)
GROUP BY m.code, m.name;

DO $check$
DECLARE
    problems text;
BEGIN
    SELECT string_agg(code || ': ' || coalesce(ST_IsValidReason(geom), 'no area'), ', ') INTO problems
    FROM state WHERE geom IS NULL OR NOT ST_IsValid(geom);
    IF problems IS NOT NULL OR (SELECT count(*) FROM state) <> (SELECT expected FROM param) THEN
        RAISE EXCEPTION 'expected % valid state polygons: % (count %)', (SELECT expected FROM param), problems, (SELECT count(*) FROM state);
    END IF;
    -- Simplification may change an area by at most 0.5 %.
    SELECT string_agg(code, ', ') INTO problems
    FROM state WHERE abs(ST_Area(geom::geography) / ST_Area(original::geography) - 1) > 0.005;
    IF problems IS NOT NULL THEN
        RAISE EXCEPTION 'simplification changed the area too much: %', problems;
    END IF;
    -- Neighbours share their border: overlaps of more than 1 m² mean broken topology.
    SELECT string_agg(a.code || '/' || b.code || ' (' || round(ST_Area(ST_Intersection(a.geom, b.geom)::geography)) || ' m²)', ', ') INTO problems
    FROM state AS a JOIN state AS b ON a.code < b.code AND ST_Intersects(a.geom, b.geom)
    WHERE ST_Area(ST_Intersection(a.geom, b.geom)::geography) > 1;
    IF problems IS NOT NULL THEN
        RAISE EXCEPTION 'simplified states overlap: %', problems;
    END IF;
END
$check$;

SELECT json_build_object(
    'type', 'FeatureCollection',
    'attribution', '© OpenStreetMap contributors',
    'license', 'ODbL-1.0 (https://opendatacommons.org/licenses/odbl/1-0/)',
    'source', 'OpenStreetMap boundary relations admin_level=4 with ISO3166-2 ' || :'codes' || ' via Overpass API, data as of '
        || (SELECT doc -> 'osm3s' ->> 'timestamp_osm_base' FROM osm)
        || ', border ways simplified with ST_SimplifyPreserveTopology (0.0005°), built by scripts/regions/' || :'script',
    'features', json_agg(json_build_object(
        'type', 'Feature',
        'properties', json_build_object('code', code, 'name', name),
        'geometry', ST_AsGeoJSON(geom, 5)::json) ORDER BY code))
FROM state;
