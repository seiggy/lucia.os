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
  { id: 'lab', label: 'Your lab', description: 'Hardware, apps, and installation work' },
  { id: 'ai', label: 'Local AI', description: 'Conversations, models, and app access' },
  { id: 'settings', label: 'Settings', description: 'Appearance, connections, and your domain' },
]
export const destinations: Destination[] = [
  { page: 'home', area: 'overview', label: 'Home', description: 'Spark health and available tools', href: '#/', keywords: 'dashboard metrics health gpu cpu memory' },
  { page: 'devices', area: 'lab', label: 'Devices', description: 'Discover and review hardware', href: '#/devices', ownerOnly: true, keywords: 'pxe servers onboarding hardware' },
  { page: 'tasks', area: 'lab', label: 'Installation tasks', description: 'Hardware approvals and observations', href: '#/tasks', ownerOnly: true, keywords: 'progress approvals jobs installation' },
  { page: 'apps', area: 'lab', label: 'Apps', description: 'Run container apps on your servers', href: '#/apps', ownerOnly: true, keywords: 'stacks docker compose containers services self-hosted logs ports listening' },
  { page: 'updates', area: 'lab', label: 'Spark updates', description: 'OS and platform package updates', href: '#/updates', ownerOnly: true, keywords: 'apt packages security kernel nvidia cuda driver reboot restart schedule maintenance upgrade' },
  { page: 'ai', area: 'ai', label: 'Playground', description: 'Talk to your hosted model', href: '#/ai', keywords: 'chat conversation llm inference responses endpoint' },
  { page: 'ai-models', area: 'ai', label: 'Models', description: 'Your library, downloads, and Hugging Face', href: '#/ai/models', ownerOnly: true, keywords: 'llm embeddings quantization context hugging face token download load unload' },
  { page: 'ai-keys', area: 'ai', label: 'API keys', description: 'Give an application inference access', href: '#/ai/keys', ownerOnly: true, keywords: 'credentials client token revoke expiry connect app' },
    { page: 'jobs', area: 'ai', label: 'Assistant jobs', description: 'Prompts the assistant runs on a schedule', href: '#/ai/jobs', ownerOnly: true, keywords: 'cron schedule automation scheduled tasks recurring unattended agent prompts run history' },  { page: 'settings', area: 'settings', label: 'Appearance', description: 'Color mode, themes, and custom accent', href: '#/settings', keywords: 'preferences light dark system theme' },
  { page: 'adguard-settings', area: 'settings', label: 'AdGuard', description: 'Connect your existing local DNS server', href: '#/settings/adguard', ownerOnly: true, keywords: 'dns rewrite network connection password' },
  { page: 'unifi-settings', area: 'settings', label: 'UniFi Network', description: 'Reserve node addresses on your UniFi gateway', href: '#/settings/unifi', ownerOnly: true, keywords: 'dhcp reservation fixed ip address gateway router network connection api key' },
  { page: 'storage-settings', area: 'settings', label: 'Storage', description: 'Mount NAS shares on every server', href: '#/settings/storage', ownerOnly: true, keywords: 'nas nfs smb cifs share mount network storage unas synology media' },
  { page: 'registry-settings', area: 'settings', label: 'Registries', description: 'Pull private images from Docker Hub and others', href: '#/settings/registries', ownerOnly: true, keywords: 'docker hub private image registry login sign in token ghcr container pull credentials rate limit' },
  { page: 'ssh-key-settings', area: 'settings', label: 'SSH keys', description: 'Sign in to managed servers with your keys', href: '#/settings/ssh-keys', ownerOnly: true, keywords: 'ssh public key authorized_keys login server github ed25519 passwordless' },
  { page: 'assistant-settings', area: 'settings', label: 'Assistant', description: 'What the assistant may do without asking', href: '#/settings/assistant', ownerOnly: true, keywords: 'ai chat tools approval automatic allowed sites web copilot agent permissions' },
  { page: 'notification-settings', area: 'settings', label: 'Notifications', description: 'Devices the assistant can notify', href: '#/settings/notifications', ownerOnly: true, keywords: 'push browser phone alerts devices notify jobs approvals' },
  { page: 'people-settings', area: 'settings', label: 'People', description: 'Accounts, groups, and app access', href: '#/settings/people', ownerOnly: true, keywords: 'users accounts groups members authentik ldap directory owners password reset disable app access permissions sign in' },
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
