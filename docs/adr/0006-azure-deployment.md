# 0006. Azure deployment: Container Apps, private PostgreSQL, identities instead of secrets

Status: Accepted (written before the first deployment; nothing in it has run in Azure yet)

## Context

The system should run on Azure the way it would for real, but as a demonstration that costs little: it is created, shown and destroyed,
like the earlier PayFlow project. The decisions below are about what to run where, and about keeping secrets out of the picture.

## Decision

| Part | Choice | Why |
|---|---|---|
| API and worker | Azure Container Apps (consumption) | Containers without a cluster to run; HTTP scaling; cheap when small. AKS is more than this needs. |
| PostgreSQL | Flexible Server, Burstable B1ms, **private access only** | No public address: reachable from the virtual network and nowhere else. Burstable is the cheapest tier. |
| Redis | A container app, internal ingress only ([ADR 0001](0001-redis-in-a-container.md)) | A cache that may be lost. A managed Redis is the production answer and costs more than the rest together. |
| Event Hubs | Namespace with **shared access keys disabled**, 4 partitions | Applications sign in with their managed identities; no connection string exists. |
| Checkpoints | Storage account with **shared keys disabled** | Same: Entra identities only. |
| Images | Azure Container Registry (Basic), admin user off | Pulled by the apps' managed identities (AcrPull). |
| Network | One virtual network; the apps' subnet and the database's subnet are delegated | The database's private DNS name resolves only inside it. |
| Infrastructure | Terraform; state in Azure Storage, Entra access only | Same approach as PayFlow. |
| Deploy | GitHub Actions, **manual**, OIDC sign-in | No stored secret. Manual because the environment does not stay up between demonstrations. |

Each application has its own managed identity with only the roles it needs: the API may send to the event hub; the worker may receive from
it and write blobs; both may pull images. The GitHub identity may push images and change apps in this one resource group, and nothing else
(it does not run Terraform, so it has no access to the subscription or the state).

**Two-step apply.** An app cannot start before its image exists. The first apply (`deploy_apps = false`) creates everything except the apps; the
deploy workflow then pushes the images; a second apply (`deploy_apps = true`) starts them. After that the workflow updates the image in place.

**The platform health probes use `/health/live` only.** `/health/ready` also reports Redis and PostgreSQL, which is right for a person or a monitor, but a readiness probe that fails takes the replica
out of rotation. A Redis outage would then stop every redirect, and the design is that it must not ([ADR 0003](0003-cache-aside.md)).

**Connection pools.** Instances times pool size must stay under the server's `max_connections` (error 53300 was reproduced earlier). A B1ms allows few: the API is capped at 3
replicas of 8 connections, the worker at 2 of 3, 30 in total. The real limit is to be read with `SHOW max_connections` after the first deployment.

## Cost

Estimates from memory of Azure's prices, **not checked against the pricing calculator**, for leaving everything running: PostgreSQL B1ms roughly 12 to 15 USD a month, Event Hubs Basic about 11,
the registry about 5, Container Apps (an always-on API, worker and Redis) roughly 15 to 30, Log Analytics capped at 0.5 GB a day. That is well above a 15 USD monthly budget, so this
is meant to run for hours, not weeks: create, demonstrate, destroy.

## What this does not do, and what is not verified

- **Nothing has been applied.** The Terraform validates offline; whether Azure accepts it (regions that refuse new customers, provider registration, the private DNS and delegation details) is found out at the first apply.
- **Identity sign-in is untested.** Locally the code uses connection strings (emulator). In Azure it uses `DefaultAzureCredential` with the app's managed identity; only the choice between the two is covered by tests.
- **`Trust Server Certificate=true`** is set on the database connection for the first deployment, so the server's certificate is not validated. The traffic never leaves the virtual network; moving to full validation is a later step.
- **The database password** exists in the Terraform state and in a Container Apps secret. With Entra authentication for the application there would be none.
- **Redis is reached by the app's name and port 6379** over internal TCP ingress. That this works as expected is checked at the first deployment.
- **Redis from Docker Hub** is pulled anonymously, which Docker rate-limits; importing the image into the registry would avoid that.
- **Basic Event Hubs** has only the default consumer group and one day of retention.
- **Migrations run at startup** of the API (safe with several replicas through an advisory lock); a separate deployment step would be cleaner.
- **No metrics reach Azure Monitor yet.** The OpenTelemetry output goes to an OTLP endpoint if one is set, and none is in Azure; logs go to Log Analytics.
- **The load tests have not run in Azure** (nor locally in full, see the README).
