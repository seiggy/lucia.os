import type { Route } from './dashboard.js'

export type AreaId = 'lab' | 'apps' | 'network' | 'ai' | 'assistant' | 'settings'

export interface Area {
  id: AreaId
  label: string
  description: string
  href: string
}

export interface Destination {
  id: string
  label: string
  description: string
  area?: AreaId
  href: string
  page: Route['page']
  keywords: string[]
}

export interface Breadcrumb {
  label: string
  href?: string
}

export interface WorkspaceLink {
  id: string
  label: string
  href: string
  current?: 'page' | 'location'
}

export const areas: Area[] = [
  { id: 'lab', label: 'Lab', description: 'Servers, installation and storage', href: '#/devices' },
  { id: 'apps', label: 'Apps', description: 'Installed apps and their backups', href: '#/apps' },
  { id: 'network', label: 'Network', description: 'DNS, gateways and internet access', href: '#/map' },
  { id: 'ai', label: 'Local AI', description: 'Playground, models and API keys', href: '#/ai' },
  { id: 'assistant', label: 'Assistant', description: 'Jobs, skills and model connections', href: '#/settings/assistant' },
  { id: 'settings', label: 'Settings', description: 'Appearance, people and sign-in', href: '#/settings' },
]

export const destinations: Destination[] = [
  { id: 'home', label: 'Home', description: 'Your lab at a glance', href: '#/', page: 'home', keywords: ['overview', 'dashboard', 'gpu', 'spark health metrics cpu memory available tools'] },
  { id: 'devices', label: 'Devices', description: 'See the servers in your lab', area: 'lab', href: '#/devices', page: 'devices', keywords: ['servers', 'nodes', 'machines', 'discover review pxe onboarding hardware'] },
  { id: 'install', label: 'Installation tasks', description: 'Prepare and install a server', area: 'lab', href: '#/tasks', page: 'tasks', keywords: ['install', 'pxe', 'provision', 'hardware approvals observations progress jobs installation'] },
  { id: 'spark-updates', label: 'Spark updates', description: 'Review and apply Spark updates', area: 'lab', href: '#/updates', page: 'updates', keywords: ['update', 'upgrade', 'updates', 'history', 'kernel', 'reboot', 'apt', 'spark', 'os platform packages security nvidia cuda driver restart schedule maintenance'] },
  { id: 'storage', label: 'Network storage', description: 'Connect shared folders from your NAS', area: 'lab', href: '#/settings/storage', page: 'storage-settings', keywords: ['nas', 'shares', 'nfs', 'smb', 'cifs share mount unas synology media'] },
  { id: 'apps', label: 'Apps', description: 'Install and manage apps', area: 'apps', href: '#/apps', page: 'apps', keywords: ['catalog', 'containers', 'backups', 'run servers stacks docker compose services self-hosted logs ports listening'] },
  { id: 'registries', label: 'Container registries', description: 'Manage private image sources', area: 'apps', href: '#/settings/registries', page: 'registry-settings', keywords: ['docker', 'ghcr', 'images', 'hub private image registry login sign in token pull credentials rate limit'] },
  { id: 'network-map', label: 'Map', description: 'See devices and traffic on your network', area: 'network', href: '#/map', page: 'map', keywords: ['map', 'topology', 'traffic', 'unifi', 'servers apps live world vlan switch access point wallboard diagram clients'] },
  { id: 'adguard', label: 'DNS', description: 'Connect AdGuard Home', area: 'network', href: '#/settings/adguard', page: 'adguard-settings', keywords: ['adguard', 'dns', 'existing local server rewrite network connection password'] },
  { id: 'unifi', label: 'UniFi gateway', description: 'Connect UniFi Network', area: 'network', href: '#/settings/unifi', page: 'unifi-settings', keywords: ['unifi', 'gateway', 'network', 'reserve node addresses dhcp reservation fixed ip address router connection api key'] },
  { id: 'domains', label: 'Domains & HTTPS', description: 'Manage internet names and certificates', area: 'network', href: '#/settings/domains', page: 'domain-settings', keywords: ['domain', 'https', 'certificates', 'cloudflare', 'dns ssl tls lets encrypt certbot wildcard subdomain urls names'] },
  { id: 'gateway-protection', label: 'Gateway protection', description: 'Review and manage gateway security', area: 'network', href: '#/security', page: 'security', keywords: ['crowdsec', 'security', 'bans', 'unban', 'trusted', 'addresses', 'alerts waf appsec firewall ban block attack scanner intrusion bouncer console enroll safe harbor allowlist'] },
  { id: 'playground', label: 'Playground', description: 'Chat with a local model', area: 'ai', href: '#/ai', page: 'ai', keywords: ['chat', 'local ai', 'talk hosted conversation llm inference responses endpoint'] },
  { id: 'ai-models', label: 'Local models', description: 'Find, download and manage models', area: 'ai', href: '#/ai/models', page: 'ai-models', keywords: ['models', 'download', 'inference', 'hugging face', 'library llm embeddings quantization context token load unload'] },
  { id: 'ai-keys', label: 'Inference API keys', description: 'Give an app access to Local AI', area: 'ai', href: '#/ai/keys', page: 'ai-keys', keywords: ['api', 'keys', 'tokens', 'inference', 'application credentials client token revoke expiry connect app'] },
  { id: 'assistant-jobs', label: 'Scheduled jobs', description: 'Review the assistant’s scheduled work', area: 'assistant', href: '#/ai/jobs', page: 'jobs', keywords: ['jobs', 'scheduled', 'automation', 'cron schedule tasks recurring unattended agent prompts run history'] },
  { id: 'assistant-skills', label: 'Skills', description: 'Choose what the assistant can do', area: 'assistant', href: '#/settings/assistant/skills', page: 'assistant-skills', keywords: ['skills', 'tools', 'instructions scripts python javascript typescript bash dotnet csharp sandbox hyperlight spark editor monaco agent package zip github import'] },
  { id: 'assistant-model-sources', label: 'Model connections', description: 'Connect models from Heimdall and other providers', area: 'assistant', href: '#/settings/assistant/models', page: 'assistant-model-sources', keywords: ['models', 'heimdall', 'openrouter', 'byok', 'connections', 'sources gateway openai credentials api key enable disable litellm local available'] },
  { id: 'assistant-settings', label: 'Permissions & approvals', description: 'Choose what needs your approval', area: 'assistant', href: '#/settings/assistant', page: 'assistant-settings', keywords: ['permissions', 'approvals', 'safety', 'asking ai chat tools approval automatic allowed sites web copilot agent'] },
  { id: 'appearance', label: 'Appearance', description: 'Choose how Lucia looks', area: 'settings', href: '#/settings', page: 'settings', keywords: ['theme', 'display', 'light', 'dark', 'preferences system color mode custom accent'] },
  { id: 'notifications', label: 'Notifications', description: 'Set up browser alerts', area: 'settings', href: '#/settings/notifications', page: 'notification-settings', keywords: ['push', 'alerts', 'assistant browser phone devices notify jobs approvals'] },
  { id: 'people', label: 'People & access', description: 'Manage accounts and groups', area: 'settings', href: '#/settings/people', page: 'people-settings', keywords: ['users', 'accounts', 'groups', 'access', 'reset', 'password', 'members authentik ldap directory owners disable app permissions sign in'] },
  { id: 'ssh-keys', label: 'SSH keys', description: 'Manage keys for signing in to servers', area: 'settings', href: '#/settings/ssh-keys', page: 'ssh-key-settings', keywords: ['ssh', 'servers', 'login', 'managed public key authorized_keys server github ed25519 passwordless'] },
]

const areaById = new Map(areas.map((area) => [area.id, area]))

export function visibleAreas(isOwner: boolean): Area[] {
  return isOwner ? areas : areas.filter((area) => area.id === 'ai' || area.id === 'settings')
}

export function visibleDestinations(isOwner: boolean): Destination[] {
  return destinations.filter((destination) => isOwner || destination.id === 'home' || destination.id === 'playground' || destination.id === 'appearance')
}

export function routeDestination(route: Route): Destination | undefined {
  return destinations.find((destination) => destination.page === route.page)
}

const appSections = [
  { id: 'apps', label: 'Your apps', href: '#/apps', view: 'list', children: ['app', 'spark'] },
  { id: 'app-catalog', label: 'Catalog', href: '#/apps/catalog', view: 'catalog', children: ['new', 'install'] },
  { id: 'app-containers', label: 'Containers & ports', href: '#/apps/containers', view: 'containers', children: [] },
  { id: 'app-backups', label: 'Backups', href: '#/apps/backups', view: 'backups', children: [] },
] as const

export function workspaceLinks(area: AreaId, route: Route, isOwner: boolean): WorkspaceLink[] {
  return visibleDestinations(isOwner).filter(item => item.area === area).flatMap(item => {
    if (item.page === 'apps') return appSections.map(section => ({
      id: section.id,
      label: section.label,
      href: section.href,
      current: route.page !== 'apps' ? undefined : route.view === section.view ? 'page' as const
        : section.children.some(view => view === route.view) ? 'location' as const : undefined,
    }))
    const nested = (route.page === 'people-settings' && route.view !== 'people')
      || (route.page === 'ai-models' && (!!route.server || route.view === 'find'))
    return [{ id: item.id, label: item.label, href: item.href,
      current: route.page === item.page ? nested ? 'location' as const : 'page' as const : undefined }]
  })
}

export function findDestinations(query: string, isOwner: boolean): Destination[] {
  const terms = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (terms.length === 0) return []

  return visibleDestinations(isOwner)
    .map((destination) => {
      const area = destination.area ? areaById.get(destination.area) : undefined
      const haystack = [destination.label, destination.description, area?.label, ...destination.keywords]
        .join(' ')
        .toLowerCase()
      if (!terms.every((term) => haystack.includes(term))) return { destination, score: 0 }
      const score = terms.reduce((total, term) => {
        if (destination.label.toLowerCase().includes(term)) return total + 4
        if (area?.label.toLowerCase().includes(term)) return total + 3
        if (destination.description.toLowerCase().includes(term)) return total + 2
        if (haystack.includes(term)) return total + 1
        return total
      }, 0)
      return { destination, score }
    })
    .filter(({ score }) => score > 0)
    .sort((a, b) => b.score - a.score || a.destination.label.localeCompare(b.destination.label))
    .map(({ destination }) => destination)
}

function routeHref(route: Route, destination: Destination): string {
  if (route.page === 'apps') {
    if (route.view === 'catalog') return '#/apps/catalog'
    if (route.view === 'containers') return '#/apps/containers'
    if (route.view === 'backups') return '#/apps/backups'
    if (route.view === 'install') return `#/apps/install/${route.name}`
    if (route.view === 'new') return '#/apps/new'
    if (route.view === 'spark') return '#/apps/spark-runner'
    if (route.view === 'app') return `#/apps/${route.name}`
  }
  if (route.page === 'people-settings') {
    if (route.view === 'groups') return '#/settings/people/groups'
    if (route.view === 'apps') return '#/settings/people/apps'
  }
  if (route.page === 'ai-models') {
    if (route.server) return `#/ai/models/on/${route.server}${route.view === 'find' ? '/find' : ''}`
    if (route.view === 'find') return '#/ai/models/find'
  }
  return destination.href
}

export function breadcrumbs(route: Route): Breadcrumb[] {
  const destination = routeDestination(route)
  if (!destination) return []

  const area = destination.area ? areaById.get(destination.area) : undefined
  if (!area) return [{ label: destination.label }]

  const currentHref = routeHref(route, destination)
  if (currentHref === area.href) return [{ label: destination.label }]

  if (route.page === 'apps') {
    const appCrumb: Breadcrumb = { label: 'Apps', href: '#/apps' }
    if (route.view === 'catalog') return [appCrumb, { label: 'App catalog' }]
    if (route.view === 'containers') return [appCrumb, { label: 'Containers' }]
    if (route.view === 'backups') return [appCrumb, { label: 'Backups' }]
    if (route.view === 'install') return [appCrumb, { label: `Install ${route.name}` }]
    if (route.view === 'new') return [appCrumb, { label: 'Add an app' }]
    if (route.view === 'spark') return [appCrumb, { label: 'Spark runner' }]
    if (route.view === 'app') return [appCrumb, { label: route.name ?? 'App' }]
  }

  if (route.page === 'people-settings') {
    const peopleCrumb: Breadcrumb = { label: 'People & access', href: '#/settings/people' }
    if (route.view === 'groups') return [{ label: area.label, href: area.href }, peopleCrumb, { label: 'Groups' }]
    if (route.view === 'apps') return [{ label: area.label, href: area.href }, peopleCrumb, { label: 'App access' }]
    return [{ label: area.label, href: area.href }, { label: 'People & access' }]
  }

  if (route.page === 'ai-models') {
    const modelsCrumb: Breadcrumb = { label: 'Local models', href: '#/ai/models' }
    if (route.server) {
      const serverLabel = `${route.server} models`
      if (route.view === 'find') {
        return [
          { label: area.label, href: area.href },
          modelsCrumb,
          { label: serverLabel, href: `#/ai/models/on/${route.server}` },
          { label: 'Find & download' },
        ]
      }
      return [{ label: area.label, href: area.href }, modelsCrumb, { label: serverLabel }]
    }
    if (route.view === 'find') return [{ label: area.label, href: area.href }, modelsCrumb, { label: 'Find & download' }]
  }

  return [{ label: area.label, href: area.href }, { label: destination.label }]
}
