# Shorty.NET

A high-performance URL shortener built to show system design with measurable results: cache-aside with Redis,
collision-free code generation across many instances, rate limiting, asynchronous click analytics, and load tests
with k6 whose numbers are published together with how they were measured.

> **Status: design phase.** Nothing is implemented yet. This repository was just created; decisions and results will
> be recorded here as the project is built.

## Intended API

```
POST /api/links        { "url": "https://example.com/very/long/url" }  ->  { "shortUrl": "https://.../X7ad91" }
GET  /X7ad91           ->  302 to the original URL
```

## Intended shape

```
Client -> load balancer -> ASP.NET Core API -> Redis (hot links) -> PostgreSQL
                                   \-> ClickRecorded -> message broker -> analytics worker -> analytics store
```
