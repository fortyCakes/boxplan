variable "subscription_id" {
  description = "Azure subscription ID"
  type        = string
  default = "e9146d88-3a1a-4ce4-916e-4cd4f6649f0d"
}

variable "location" {
  description = "Azure region for all resources"
  type        = string
  default     = "West Europe"
}

variable "environment" {
  description = "Deployment environment suffix (e.g. prod, staging)"
  type        = string
  default     = "prod"
}
