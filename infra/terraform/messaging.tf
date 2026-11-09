resource "azurerm_eventhub_namespace" "main" {
  name                = "evhns-${local.name}-${local.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = var.eventhubs_sku
  capacity            = 1

  minimum_tls_version = "1.2"
  # No shared access keys exist: the applications sign in with their managed identities.
  local_authentication_enabled = false

  tags = local.tags
}

resource "azurerm_eventhub" "clicks" {
  name              = "clicks"
  namespace_id      = azurerm_eventhub_namespace.main.id
  partition_count   = 4
  message_retention = 1 # days; the most Basic allows. A worker that is down for longer loses clicks (ADR 0005).
}

# Standard has room for a consumer group of our own; Basic only has the default one.
resource "azurerm_eventhub_consumer_group" "analytics" {
  count = var.eventhubs_sku == "Basic" ? 0 : 1

  name                = "analytics"
  namespace_name      = azurerm_eventhub_namespace.main.name
  eventhub_name       = azurerm_eventhub.clicks.name
  resource_group_name = azurerm_resource_group.main.name
}
