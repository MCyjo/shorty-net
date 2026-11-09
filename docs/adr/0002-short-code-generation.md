# 0002. Short code generation: block allocation with a keyed permutation

**Status:** Accepted

## Context
Every link needs a short, unique code, and many application instances create links at the same time. The code has to be
unique without making every request wait for a shared lock, short enough to be worth sharing (the target is seven
characters), and ideally not a counter that anyone can walk through to list every link ever created.

The code space with seven Base62 characters is 62^7 = 3,521,614,606,208 (about 3.5 trillion).

## Options considered

| Strategy | Collisions | Coordination per link | Length | Guessable | Verdict |
|---|---|---|---|---|---|
| Random Base62, retry on conflict | possible; a new code collides with probability n / 62^7 | none (the unique index decides) | 7 | no | works, but collides from the start |
| GUID / UUID | none in practice | none | 22 in Base64 | no | too long to share |
| Counter from the database, Base62 | none | one round trip per link | 1 to 7 | **yes**: sequential, reveals volume | rejected |
| Snowflake-style (time, node id, sequence) | none | none, but every instance needs its own node id | 11 | partly | needs node-id assignment |
| **Id blocks + a keyed permutation** | **none by construction** | **one round trip per 1,000 links** | **7** | **not sequential** | **chosen** |

How often random codes collide, as a plain calculation: with n links stored, the next random code is already taken with
probability n / 62^7. That is 0.00003% at one million links, 0.03% at one billion, and 2.8% at 100 billion. By the birthday
bound the first collision somewhere is expected around 2.3 million links. So random codes are workable but need the retry
path from day one and use it more as the table grows. The unique index stays the final judge in every strategy.

## Decision
1. **Unique ids by block allocation.** Each instance reserves a block of 1,000 ids with one atomic `UPDATE ... RETURNING` on a
   counter row, then hands out ids from memory. Two instances can never receive the same block, so ids are unique across
   the fleet without any per-request coordination.
2. **A keyed permutation turns the id into a code.** The id is passed through a 4-round Feistel network over 42 bits, with
   cycle walking to stay inside 62^7 (a format-preserving-encryption technique for domains that are not a power of two),
   then written in seven Base62 characters. A permutation never maps two ids to one code, so unique ids stay unique, but
   consecutive ids look unrelated.
3. **Custom aliases** are stored as the code, and the unique index on `code` decides who wins.

## Consequences
- **No collisions between instances, and the system can prove it.** `shorty.code.collisions` counts a generated code that
  was already taken. It must stay at zero; the tests assert it for three instances creating 3,000 links concurrently.
- **Cheap:** the database is asked for an id once per 1,000 links. A test checks it: 2,500 links on one instance reserve
  exactly three blocks. At 1,000 links per second the counter row is updated once per second, so it is not a bottleneck.
- **Gaps:** a process that stops forgets the rest of its block. Ids (and so codes) are unique, not consecutive. Nothing needs
  them to be consecutive.
- **Capacity:** 3.5 trillion codes, which is more than a century at 1,000 new links per second. Running out is reported as an
  error, not wrapped around.
- **The permutation hides order and volume; it is not encryption.** The round function is a fast integer mixer. Do not treat a
  code as a secret, and do not treat the scrambling as protection against a determined attacker. Where unguessable links
  matter, long random codes are the right tool.
- **The seed is part of the data.** Changing it changes the mapping, so ids issued later could produce codes that earlier ids
  already use. The unique index would still prevent duplicates, but the retries would multiply. It has to stay the same for the
  life of the data (in Azure it belongs in Key Vault, not in a settings file).
- **Custom aliases can still collide with a generated code.** That is rare and handled the same way as any collision: the
  generated code is retried with the next id (up to five attempts).

## How it is tested
- The permutation is a bijection (one million consecutive ids give one million distinct codes; it can be undone), consecutive codes
  differ in more than 6 of 7 positions on average (random codes would differ in about 6.9, a counter in 1), and the first character is
  spread evenly over the alphabet.
- The allocator hands out unique ids to 16 callers on one instance and to 8 instances sharing one store.
- On real PostgreSQL, three application instances start together against an empty database (the advisory lock serialises
  the migration) and create 3,000 links in parallel: 3,000 distinct codes, zero collisions.
- Each of these properties was verified by breaking the code on purpose (one Feistel round, no cycle walking, every block starting
  at zero, no migration lock, no alias-conflict handling) and watching the corresponding test fail.

## Not measured yet
Creation throughput and latency. The correctness of the scheme is tested; its speed is not, and no number about it will be
published before a load test measures it.
