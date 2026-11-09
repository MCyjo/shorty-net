output "resource_group" {
  value = azurerm_resource_group.main.name
}

output "registry_name" {
  description = "The container registry the deploy workflow pushes to."
  value       = azurerm_container_registry.main.name
}

output "registry_login_server" {
  value = azurerm_container_registry.main.login_server
}

output "api_app_name" {
  value = "ca-${local.name}-api"
}

output "analytics_app_name" {
  value = "ca-${local.name}-analytics"
}

output "api_url" {
  description = "Where the API answers once deploy_apps is true and the images are pushed."
  value       = local.api_url
}

output "postgres_server" {
  description = "Reachable only from inside the virtual network."
  value       = azurerm_postgresql_flexible_server.main.fqdn
}
