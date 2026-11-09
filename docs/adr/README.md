# Architecture decision records

Short records of the decisions that shape Shorty.NET: the context, what was decided, and what it costs.

| # | Decision | Status |
|---|---|---|
| [0001](0001-redis-in-a-container.md) | Redis as a container, not a managed service | Accepted |
| [0002](0002-short-code-generation.md) | Short code generation: block allocation with a keyed permutation | Accepted |
| [0003](0003-cache-aside.md) | Cache-aside with memory and Redis layers | Accepted |
| [0004](0004-rate-limiting.md) | Rate limiting: which algorithm where, and what it does not do | Accepted |
| [0005](0005-click-analytics.md) | Click analytics through a broker, with a worker | Accepted |
| [0006](0006-azure-deployment.md) | Azure deployment: Container Apps, private PostgreSQL, identities instead of secrets | Accepted (not yet deployed) |
