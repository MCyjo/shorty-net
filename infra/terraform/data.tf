resource "azurerm_postgresql_flexible_server" "main" {
  name                = "psql-${local.name}-${local.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  version             = "17"
  sku_name            = var.postgres_sku
  storage_mb          = 32768
  zone                = "1"

  backup_retention_days         = 7
  public_network_access_enabled = false
  delegated_subnet_id           = azurerm_subnet.postgres.id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id

  # A password kept in the state (which is reachable only through Entra ID) and in a Container Apps secret. A production setup
  # would use Entra authentication for the application as well, so that no database password exists; see ADR 0006.
  administrator_login    = "shorty"
  administrator_password = random_password.postgres.result

  tags = local.tags

  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]

  lifecycle {
    ignore_changes = [zone] # Azure may choose another zone later; that must not make Terraform try to move the server
  }
}

resource "azurerm_postgresql_flexible_server_database" "shorty" {
  name      = "shorty"
  server_id = azurerm_postgresql_flexible_server.main.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# Checkpoints of the analytics worker: where it has read up to in each partition. Entra ID only, no account keys.
resource "azurerm_storage_account" "checkpoints" {
  name                     = "st${local.compact_name}"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  allow_nested_items_to_be_public = false

  tags = local.tags
}

resource "azurerm_storage_container" "checkpoints" {
  name                  = "shorty-checkpoints"
  storage_account_id    = azurerm_storage_account.checkpoints.id # through the management API, so no storage key is needed
  container_access_type = "private"
}
