terraform {
  required_version = ">= 1.7"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  backend "azurerm" {
    subscription_id      = "e9146d88-3a1a-4ce4-916e-4cd4f6649f0d"
    resource_group_name  = "rg-boxplan"
    storage_account_name = "saboxplanprod"
    container_name       = "tfstate"
    key                  = "boxplan.tfstate"
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {}
}

resource "azurerm_resource_group" "main" {
  name     = "rg-boxplan-${var.environment}"
  location = var.location
}

resource "azurerm_static_web_app" "main" {
  name                = "stapp-boxplan-${var.environment}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku_tier            = "Free"
  sku_size            = "Free"
}
