output "static_web_app_url" {
  description = "Public URL of the deployed BoxPlan web app"
  value       = "https://${azurerm_static_web_app.main.default_host_name}"
}

output "deployment_token" {
  description = "API token for GitHub Actions deployment — store as AZURE_STATIC_WEB_APPS_API_TOKEN secret"
  value       = azurerm_static_web_app.main.api_key
  sensitive   = true
}
