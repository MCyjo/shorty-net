resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${local.name}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.5 # a cap on what logging can cost; once reached, ingestion stops until the next day
  tags                = local.tags
}

resource "azurerm_container_registry" "main" {
  name                = "acr${local.compact_name}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = false # images are pulled and pushed with Entra identities, not with a shared password
  tags                = local.tags
}

# One identity per application, so each gets only the access it needs.
resource "azurerm_user_assigned_identity" "api" {
  name                = "id-${local.name}-api"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "analytics" {
  name                = "id-${local.name}-analytics"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tags                = local.tags
}

resource "azurerm_role_assignment" "api_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.api.principal_id
}

resource "azurerm_role_assignment" "analytics_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.analytics.principal_id
}

resource "azurerm_role_assignment" "api_send" {
  scope                = azurerm_eventhub.clicks.id
  role_definition_name = "Azure Event Hubs Data Sender"
  principal_id         = azurerm_user_assigned_identity.api.principal_id
}

resource "azurerm_role_assignment" "analytics_receive" {
  scope                = azurerm_eventhub.clicks.id
  role_definition_name = "Azure Event Hubs Data Receiver"
  principal_id         = azurerm_user_assigned_identity.analytics.principal_id
}

# The worker keeps its positions in blobs, and creates the container if it is missing.
resource "azurerm_role_assignment" "analytics_checkpoints" {
  scope                = azurerm_storage_account.checkpoints.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.analytics.principal_id
}

# GitHub Actions: push images, and change the apps. Scoped to this resource group, not the subscription.
resource "azurerm_role_assignment" "github_push" {
  count = var.github_actions_principal_id == "" ? 0 : 1

  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPush"
  principal_id         = var.github_actions_principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "github_deploy" {
  count = var.github_actions_principal_id == "" ? 0 : 1

  scope                = azurerm_resource_group.main.id
  role_definition_name = "Contributor"
  principal_id         = var.github_actions_principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${local.name}"
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  infrastructure_subnet_id   = azurerm_subnet.apps.id
  tags                       = local.tags
}

# A new role assignment takes a while to reach the registry; an app that starts before then cannot pull its image.
resource "time_sleep" "roles_settle" {
  create_duration = "90s"

  depends_on = [
    azurerm_role_assignment.api_pull,
    azurerm_role_assignment.analytics_pull,
    azurerm_role_assignment.api_send,
    azurerm_role_assignment.analytics_receive,
    azurerm_role_assignment.analytics_checkpoints,
  ]
}
