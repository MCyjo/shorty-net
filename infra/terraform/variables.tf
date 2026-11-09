variable "subscription_id" {
  description = "The Azure subscription to deploy into."
  type        = string
}

variable "location" {
  description = "One region for everything: the database is reached through the virtual network, so it has to be where the network is."
  type        = string
  default     = "northeurope"
}

variable "environment" {
  description = "Short name used in resource names."
  type        = string
  default     = "dev"
}

variable "deploy_apps" {
  description = "false: create the infrastructure only. true: also start the apps, which needs images in the registry (see the deploy workflow). Two steps because an app cannot start before its image exists."
  type        = bool
  default     = false
}

variable "image_tag" {
  description = "Tag of the shorty-api and shorty-analytics images in the registry. Used when deploy_apps is true."
  type        = string
  default     = "latest"
}

variable "github_actions_principal_id" {
  description = "Object id of the identity GitHub Actions signs in as (created by scripts/setup-github-oidc.ps1). It may push images and update the apps. Leave empty to skip the role assignments."
  type        = string
  default     = ""
}

variable "postgres_sku" {
  description = "Burstable is the cheapest tier. Its max_connections is small, which is why the connection pools are capped in apps.tf."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "eventhubs_sku" {
  description = "Basic is the cheapest, and has only the $Default consumer group and one day of retention. Standard allows a dedicated consumer group."
  type        = string
  default     = "Basic"

  validation {
    condition     = contains(["Basic", "Standard"], var.eventhubs_sku)
    error_message = "Use Basic or Standard."
  }
}

variable "api_max_replicas" {
  description = "How far the API scales out. Replicas x the API's connection pool size must stay under the database's max_connections."
  type        = number
  default     = 3
}
