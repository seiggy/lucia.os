import type { Route } from './dashboard.js'

export type AreaId = 'overview' | 'lab' | 'ai' | 'settings'
export interface Destination {
  page: Exclude<Route['page'], 'not-found'>
  area: AreaId
  label: string
  description: string
  href: string
  ownerOnly?: boolean
  keywords: string
}
export const areas: { id: AreaId; label: string; description: string }[] = [
  { id: 'overview', label: 'Overview', description: 'Your host and a place to begin' },
  { id: 'lab', label: 'Your lab', description: 'Hardware and installation work' },
  { id: 'ai', label: 'Local AI', description: 'Conversations, models, and app access' },
  { id: 'settings', label: 'Settings', description: 'Appearance, connections, and your domain' },
]
export const destinations: Destination[] = [
  { page: 'home', area: 'overview', label: 'Home', description: 'Spark health and available tools', href: '#/', keywords: 'dashboard metrics health gpu cpu memory' },
  { page: 'devices', area: 'lab', label: 'Devices', description: 'Discover and review hardware', href: '#/devices', ownerOnly: true, keywords: 'pxe servers onboarding hardware' },
  { page: 'tasks', area: 'lab', label: 'Installation tasks', description: 'Hardware approvals and observations', href: '#/tasks', ownerOnly: true, keywords: 'progress approvals jobs installation' },
  { page: 'ai', area: 'ai', label: 'Playground', description: 'Talk to your hosted model', href: '#/ai', keywords: 'chat conversation llm inference responses endpoint' },
  { page: 'ai-models', area: 'ai', label: 'Models', description: 'Your library, downloads, and Hugging Face', href: '#/ai/models', ownerOnly: true, keywords: 'llm embeddings quantization context hugging face token download load unload' },
  { page: 'ai-keys', area: 'ai', label: 'API keys', description: 'Give an application inference access', href: '#/ai/keys', ownerOnly: true, keywords: 'credentials client token revoke expiry connect app' },
  { page: 'settings', area: 'settings', label: 'Appearance', description: 'Color mode, themes, and custom accent', href: '#/settings', keywords: 'preferences light dark system theme' },
  { page: 'adguard-settings', area: 'settings', label: 'AdGuard', description: 'Connect your existing local DNS server', href: '#/settings/adguard', ownerOnly: true, keywords: 'dns rewrite network connection password' },
  { page: 'domain-settings', area: 'settings', label: 'Domains', description: 'Cloudflare DNS and HTTPS certificates', href: '#/settings/domains', ownerOnly: true, keywords: 'ssl tls lets encrypt certbot wildcard subdomain urls names cloudflare' },
]

export function visibleDestinations(isOwner: boolean): Destination[] {
  return destinations.filter(item => !item.ownerOnly || isOwner)
}
export function routeDestination(route: Route): Destination | undefined {
  return destinations.find(item => item.page === route.page)
}
export function findDestinations(query: string, isOwner: boolean): Destination[] {
  const terms = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean)
  return visibleDestinations(isOwner).filter(item =>
    terms.every(term => `${item.label} ${item.description} ${item.keywords}`.toLocaleLowerCase().includes(term)))
}
