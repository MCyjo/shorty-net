locals {
  name = "shorty-${var.environment}"

  # Names that must be unique across Azure get a short random suffix; hyphens are not allowed in some of them.
  suffix       = random_string.suffix.result
  compact_name = "shorty${var.environment}${local.suffix}"

  # With Basic there is only the default consumer group; with Standard the worker gets its own.
  consumer_group = var.eventhubs_sku == "Basic" ? "$Default" : "analytics"

  tags = {
    project     = "shorty-net"
    environment = var.environment
    managed_by  = "terraform"
  }
}

resource "random_string" "suffix" {
  length  = 4
  upper   = false
  special = false
}

resource "random_password" "postgres" {
  length  = 32
  special = false # keeps the connection string free of characters that need escaping
}

# The key of the short-code permutation. Obfuscation, not encryption (see ADR 0002), but it should not be guessable, and it must
# not change while the data exists: it lives in the state exactly as long as the database does.
resource "random_integer" "code_seed" {
  min = 1000000000000000
  max = 9000000000000000
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.name}"
  location = var.location
  tags     = local.tags
}
