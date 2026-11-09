locals {
  # Instances x pool size must stay under the server's max_connections (a Burstable B1ms allows few). With the defaults:
  # API 3 replicas x 8 + worker 2 replicas x 3 = 30. Check "SHOW max_connections" after the first deploy.
  postgres_host   = azurerm_postgresql_flexible_server.main.fqdn
  postgres_api    = "Host=${local.postgres_host};Database=shorty;Username=shorty;Password=${random_password.postgres.result};SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=8;Timeout=5"
  postgres_worker = "Host=${local.postgres_host};Database=shorty;Username=shorty;Password=${random_password.postgres.result};SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=3;Timeout=5"

  eventhubs_namespace_address = "${azurerm_eventhub_namespace.main.name}.servicebus.windows.net"
  registry                    = azurerm_container_registry.main.login_server

  api_url = "https://ca-${local.name}-api.${azurerm_container_app_environment.main.default_domain}"
}

# Redis as a container (ADR 0001): reachable only from inside the environment, no persistence, bounded memory.
resource "azurerm_container_app" "redis" {
  count = var.deploy_apps ? 1 : 0

  name                         = "ca-${local.name}-redis"
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = local.tags

  template {
    min_replicas = 1
    max_replicas = 1 # one cache; a second replica would be a second, separate cache

    container {
      name    = "redis"
      image   = "docker.io/library/redis:7.2-alpine"
      cpu     = 0.25
      memory  = "0.5Gi"
      command = ["redis-server"]
      args    = ["--save", "", "--appendonly", "no", "--maxmemory", "200mb", "--maxmemory-policy", "allkeys-lru"]
    }
  }

  ingress {
    external_enabled = false
    target_port      = 6379
    exposed_port     = 6379
    transport        = "tcp"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

resource "azurerm_container_app" "api" {
  count = var.deploy_apps ? 1 : 0

  name                         = "ca-${local.name}-api"
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.api.id]
  }

  registry {
    server   = local.registry
    identity = azurerm_user_assigned_identity.api.id
  }

  secret {
    name  = "postgres"
    value = local.postgres_api
  }

  template {
    min_replicas = 1 # a redirect should not wait for a cold start
    max_replicas = var.api_max_replicas

    http_scale_rule {
      name                = "http"
      concurrent_requests = "100"
    }

    container {
      name   = "api"
      image  = "${local.registry}/shorty-api:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      # Only liveness is wired to the platform. /health/ready also reports Redis and PostgreSQL, and a readiness probe that fails
      # takes the replica out of rotation: a Redis outage would then stop every redirect, which the design is meant to survive.
      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"
      }

      startup_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name        = "ConnectionStrings__Postgres"
        secret_name = "postgres"
      }
      env {
        name  = "ConnectionStrings__Redis"
        value = "ca-${local.name}-redis:6379,abortConnect=false,connectTimeout=2000"
      }
      env {
        name  = "ConnectionStrings__EventHubs"
        value = local.eventhubs_namespace_address # an address, not a connection string: the managed identity signs in
      }
      env {
        name  = "Analytics__EventHubName"
        value = azurerm_eventhub.clicks.name
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.api.client_id
      }
      env {
        name  = "CodeGeneration__Seed"
        value = tostring(random_integer.code_seed.result)
      }
      env {
        name  = "Database__MigrateOnStartup"
        value = "true" # safe with several replicas (an advisory lock serialises them); a separate deployment step would be cleaner
      }
      env {
        name  = "Shorty__BaseUrl"
        value = local.api_url
      }
      env {
        # Behind the Container Apps ingress every request comes from the ingress, so the client address is in X-Forwarded-For.
        name  = "RateLimiting__TrustForwardedHeaders"
        value = "true"
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image] # the deploy workflow updates the image; Terraform must not undo it
  }

  depends_on = [time_sleep.roles_settle, azurerm_postgresql_flexible_server_database.shorty, azurerm_container_app.redis]
}

resource "azurerm_container_app" "analytics" {
  count = var.deploy_apps ? 1 : 0

  name                         = "ca-${local.name}-analytics"
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.analytics.id]
  }

  registry {
    server   = local.registry
    identity = azurerm_user_assigned_identity.analytics.id
  }

  secret {
    name  = "postgres"
    value = local.postgres_worker
  }

  template {
    min_replicas = 1
    max_replicas = 2 # more replicas than partitions (4) would sit idle; two is enough to show the partitions being shared

    container {
      name   = "analytics"
      image  = "${local.registry}/shorty-analytics:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name        = "ConnectionStrings__Postgres"
        secret_name = "postgres"
      }
      env {
        name  = "ConnectionStrings__EventHubs"
        value = local.eventhubs_namespace_address
      }
      env {
        name  = "ConnectionStrings__Checkpoints"
        value = trimsuffix(azurerm_storage_account.checkpoints.primary_blob_endpoint, "/")
      }
      env {
        name  = "Analytics__EventHubName"
        value = azurerm_eventhub.clicks.name
      }
      env {
        name  = "Analytics__ConsumerGroup"
        value = local.consumer_group
      }
      env {
        name  = "Analytics__CheckpointContainer"
        value = azurerm_storage_container.checkpoints.name
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.analytics.client_id
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }

  depends_on = [time_sleep.roles_settle, azurerm_postgresql_flexible_server_database.shorty]
}
