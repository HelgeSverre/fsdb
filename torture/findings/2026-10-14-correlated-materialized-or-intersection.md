# Intersecting candidates inside correlated materialized OR branches

MySQL 8.4.11 returns a count of two for each outer key in the focused query:

```sql
SELECT u.id,
       (SELECT COUNT(*)
        FROM (SELECT id+0 AS id, user_id+0 AS user_id
              FROM candidates LIMIT 100) o
        WHERE (o.user_id=u.id AND o.id BETWEEN u.id AND u.id+2)
           OR (o.user_id=u.id+1 AND o.id BETWEEN u.id+10 AND u.id+12))
FROM outer_keys u ORDER BY u.id;
```

The fixture has outer keys 1 and 2 and 100 candidate rows. Candidate `id`
runs from 1 through 100; `user_id` cycles from 1 through 10. The same result
holds when the derived source is a statement-stable CTE.

FSDB's materialized-source planner already built statement-local equality and
range lookups, but within each OR branch it selected only the shorter list.
Each branch here has ten equality candidates, three range candidates, and one
row in their intersection. The old plan therefore passed twelve rows through
the residual predicate across the two outer keys. A regression with `TOUCH`
before the OR predicate observed 15 calls including expression setup.

The planner now intersects all compatible candidate lists within each branch
by materialized row identity before unioning the branches. The same regression
observed seven calls for the derived-table form, and both the derived-table and
CTE forms keep their MySQL row counts with fewer than nine residual calls.
Duplicate materialized rows remain distinct because intersection uses row-array
identity; the final predicate still decides membership.

This is an execution-efficiency check, not a claim that MySQL uses the same
physical plan. Branches without a complete usable lookup still fall back to
general evaluation.
