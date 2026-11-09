terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
  }

  # State lives in Azure Storage (created once by scripts/bootstrap-state.ps1), reached through Entra ID only.
  # Supply the details at init time: terraform init -backend-config=backend.hcl
  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id     = var.subscription_id
  storage_use_azuread = true

  # A new subscription has most resource providers unregistered, and the provider only registers a core set by default.
  resource_provider_registrations = "core"
  resource_providers_to_register = [
    "Microsoft.App",
    "Microsoft.ContainerRegistry",
    "Microsoft.DBforPostgreSQL",
    "Microsoft.EventHub",
    "Microsoft.ManagedIdentity",
    "Microsoft.Network",
    "Microsoft.OperationalInsights",
    "Microsoft.Storage",
  ]

  features {
    resource_group {
      # The demo resource group is disposable; do not block destroy on stray resources.
      prevent_deletion_if_contains_resources = false
    }
  }
}
