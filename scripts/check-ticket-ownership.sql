BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

WITH groups AS MATERIALIZED (
    SELECT id, uuid, tickets FROM public.memory_group
), entries AS MATERIALIZED (
    SELECT g.id, g.uuid, t.value, t.ordinality
    FROM groups g
    CROSS JOIN LATERAL jsonb_array_elements(
        CASE WHEN jsonb_typeof(g.tickets) = 'array' THEN g.tickets ELSE '[]'::jsonb END
    ) WITH ORDINALITY AS t(value, ordinality)
), malformed AS (
    SELECT uuid, NULL::bigint AS position, 'tickets must be a JSON array' AS reason
    FROM groups WHERE jsonb_typeof(tickets) IS DISTINCT FROM 'array'
    UNION ALL
    SELECT uuid, ordinality, 'ticket must be an object with string provider and key'
    FROM entries
    WHERE jsonb_typeof(value) IS DISTINCT FROM 'object'
       OR jsonb_typeof(value->'provider') IS DISTINCT FROM 'string'
       OR jsonb_typeof(value->'key') IS DISTINCT FROM 'string'
), duplicates AS (
    SELECT value->>'provider' COLLATE "C" AS provider,
           value->>'key' COLLATE "C" AS key,
           array_agg(DISTINCT uuid ORDER BY uuid) AS group_uuids
    FROM entries
    WHERE jsonb_typeof(value->'provider') = 'string'
      AND jsonb_typeof(value->'key') = 'string'
    GROUP BY 1, 2
    HAVING count(DISTINCT id) > 1
)
SELECT jsonb_build_object(
    'duplicates', COALESCE((SELECT jsonb_agg(jsonb_build_object(
        'provider', provider, 'key', key, 'groupUuids', group_uuids
    ) ORDER BY provider, key) FROM duplicates), '[]'::jsonb),
    'malformed', COALESCE((SELECT jsonb_agg(jsonb_build_object(
        'groupUuid', uuid, 'position', position, 'reason', reason
    ) ORDER BY uuid, position) FROM malformed), '[]'::jsonb)
) AS report,
EXISTS (SELECT 1 FROM malformed) AS has_malformed,
EXISTS (SELECT 1 FROM duplicates) AS has_duplicates
\gset

ROLLBACK;
\echo :report
\if :has_malformed
    \echo CHECK_EXIT=2
\elif :has_duplicates
    \echo CHECK_EXIT=1
\else
    \echo CHECK_EXIT=0
\endif
