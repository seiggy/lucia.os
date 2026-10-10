export type Scheme = 'light' | 'dark'
export type Appearance = Scheme | 'system'
export type ThemeName = 'ocean' | 'meadow' | 'plum' | 'custom'

export interface Preferences {
  appearance: Appearance
  theme: ThemeName
  customAccent: string
}

export const defaultPreferences: Preferences = { appearance: 'system', theme: 'ocean', customAccent: '#285bdd' }

export const themes: { id: Exclude<ThemeName, 'custom'>; name: string; light: string; dark: string }[] = [
  { id: 'ocean', name: 'Ocean', light: '#285bdd', dark: '#97b3ff' },
  { id: 'meadow', name: 'Meadow', light: '#19714e', dark: '#80cfac' },
  { id: 'plum', name: 'Plum', light: '#7951a8', dark: '#c6a2ed' },
]

export type Route =
  | { page: 'home' }
  | { page: 'devices' }
  | { page: 'tasks' }
  | { page: 'updates' }
  | { page: 'map' }
  | { page: 'settings' }
  | { page: 'adguard-settings' }
  | { page: 'unifi-settings' }
  | { page: 'storage-settings' }
  | { page: 'registry-settings' }
  | { page: 'domain-settings' }
  | { page: 'ssh-key-settings' }
  | { page: 'assistant-settings' }
  | { page: 'notification-settings' }
  | { page: 'jobs' }
  | { page: 'people-settings'; view: 'people' | 'groups' | 'apps' }
  | { page: 'apps'; view: 'list' | 'containers' | 'catalog' | 'new' | 'app' | 'install' | 'backups' | 'spark'; name?: string; node?: string }
  | { page: 'ai' }
  | { page: 'ai-keys' }
  | { page: 'ai-models'; view?: 'library' | 'find'; server?: string }
  | { page: 'not-found' }

export function parseRoute(hash: string): Route {
  // A notification's link adds ?chat= to open that chat; the page itself ignores it.
  const parts = hash.replace(/^#\/?/, '').split('?')[0].split('/').filter(Boolean)
  if (parts.length === 0 || (parts.length === 1 && parts[0] === 'home')) return { page: 'home' }
  if (parts.length === 1 && parts[0] === 'settings') return { page: 'settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'adguard') return { page: 'adguard-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'unifi') return { page: 'unifi-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'storage') return { page: 'storage-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'registries') return { page: 'registry-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'domains') return { page: 'domain-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'ssh-keys') return { page: 'ssh-key-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'assistant') return { page: 'assistant-settings' }
  if (parts.length === 2 && parts[0] === 'settings' && parts[1] === 'notifications') return { page: 'notification-settings' }
  if (parts.length === 2 && parts[0] === 'ai' && parts[1] === 'jobs') return { page: 'jobs' }
  if (parts[0] === 'settings' && parts[1] === 'people' && parts.length <= 3) {
    if (parts.length === 2) return { page: 'people-settings', view: 'people' }
    if (parts[2] === 'groups' || parts[2] === 'apps') return { page: 'people-settings', view: parts[2] }
  }
  if (parts.length === 1 && parts[0] === 'ai') return { page: 'ai' }
  if (parts.length === 2 && parts[0] === 'ai' && parts[1] === 'keys') return { page: 'ai-keys' }
  if (parts.length === 2 && parts[0] === 'ai' && parts[1] === 'models') return { page: 'ai-models' }
  if (parts.length === 3 && parts[0] === 'ai' && parts[1] === 'models' && parts[2] === 'find') return { page: 'ai-models', view: 'find' }
  if ((parts.length === 4 || (parts.length === 5 && parts[4] === 'find')) && parts[0] === 'ai' && parts[1] === 'models' && parts[2] === 'on'
    && /^[A-Za-z0-9][A-Za-z0-9.-]{0,62}$/.test(parts[3])) return { page: 'ai-models', view: parts[4] ? 'find' : 'library', server: parts[3] }
  if (parts.length === 1 && parts[0] === 'devices') return { page: 'devices' }
  if (parts.length === 1 && parts[0] === 'tasks') return { page: 'tasks' }
  if (parts.length === 1 && parts[0] === 'updates') return { page: 'updates' }
  if (parts.length === 1 && parts[0] === 'map') return { page: 'map' }
  if (parts[0] === 'apps' && parts.length === 1) return { page: 'apps', view: 'list' }
  if (parts[0] === 'apps' && parts.length === 2) {
    if (parts[1] === 'containers') return { page: 'apps', view: 'containers' }
    if (parts[1] === 'new') return { page: 'apps', view: 'new' }
    if (parts[1] === 'catalog') return { page: 'apps', view: 'catalog' }
    if (parts[1] === 'backups') return { page: 'apps', view: 'backups' }
    if (parts[1] === 'spark-runner') return { page: 'apps', view: 'spark' }
    if (/^[a-z](?:[a-z0-9-]{0,38}[a-z0-9])?$/.test(parts[1])) return { page: 'apps', view: 'app', name: parts[1] }
  }
  if (parts[0] === 'apps' && parts[1] === 'install' && (parts.length === 3 || parts.length === 4) && /^[a-z][a-z0-9-]{0,39}$/.test(parts[2])
    && (parts.length === 3 || /^[A-Za-z0-9][A-Za-z0-9.-]{0,62}$/.test(parts[3])))
    return { page: 'apps', view: 'install', name: parts[2], ...(parts[3] ? { node: parts[3] } : {}) }
  return { page: 'not-found' }
}

export function validatePreferences(value: unknown): Preferences {
  if (!value || typeof value !== 'object') throw new Error('Saved appearance is not an object.')
  const data = value as Record<string, unknown>
  if (data.appearance !== 'light' && data.appearance !== 'dark' && data.appearance !== 'system') throw new Error('Unknown appearance mode.')
  if (data.theme !== 'ocean' && data.theme !== 'meadow' && data.theme !== 'plum' && data.theme !== 'custom') throw new Error('Unknown theme.')
  if (typeof data.customAccent !== 'string' || !/^#[0-9a-f]{6}$/i.test(data.customAccent)) throw new Error('Invalid accent color.')
  return { appearance: data.appearance, theme: data.theme, customAccent: data.customAccent }
}

function rgb(hex: string): number[] {
  if (!/^#[0-9a-f]{6}$/i.test(hex)) throw new Error('Expected a six-digit hex color.')
  return [1, 3, 5].map(start => parseInt(hex.slice(start, start + 2), 16))
}

function luminance(hex: string): number {
  const channels = rgb(hex).map(channel => {
    const value = channel / 255
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4
  })
  return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722
}

export function contrast(first: string, second: string): number {
  const a = luminance(first)
  const b = luminance(second)
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
}

export function themeColors(preferences: Preferences, scheme: Scheme): { accent: string; onAccent: string; accentInk: string } {
  const theme = themes.find(item => item.id === preferences.theme)
  const accent = preferences.theme === 'custom' ? preferences.customAccent : theme?.[scheme] ?? themes[0][scheme]
  const onAccent = contrast(accent, '#ffffff') >= contrast(accent, '#000000') ? '#ffffff' : '#000000'
  const background = scheme === 'light' ? '#f3f5f9' : '#242c3c'
  const toward = scheme === 'light' ? 0 : 255
  let channels = rgb(accent)
  let accentInk = accent
  while (contrast(accentInk, background) < 4.5) {
    channels = channels.map(channel => Math.round(channel * 0.8 + toward * 0.2))
    accentInk = '#' + channels.map(channel => channel.toString(16).padStart(2, '0')).join('')
  }
  return { accent, onAccent, accentInk }
}
