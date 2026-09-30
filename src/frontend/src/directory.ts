export interface DirectoryUser { username: string; name: string; email: string | null; active: boolean; protected: boolean; synced: boolean; groups: string[] }
export interface DirectoryGroup { name: string; description: string; kind: 'directory' | 'lucia'; protected: boolean; members: string[] }
export interface DirectoryApp { slug: string; name: string; launchUrl: string | null; fixed: boolean; groups: string[] }
export interface DirectoryChange { id: string; action: string | null; target: string | null; state: 'pending' | 'done' | 'failed'; message: string | null; requestedAt: string }
export interface Directory {
  ready: boolean; checkedAt: string | null; passwordChange: boolean; actor: string | null
  users: DirectoryUser[]; groups: DirectoryGroup[]; apps: DirectoryApp[]; changes: DirectoryChange[]
}
export type DirectoryAction = 'createUser' | 'updateUser' | 'setUserGroups' | 'setPassword' | 'setUserActive' | 'deleteUser'
  | 'createGroup' | 'updateGroup' | 'setGroupMembers' | 'deleteGroup' | 'setAppGroups'
export interface DirectoryRequest {
  action: DirectoryAction; username?: string; name?: string; email?: string; password?: string; groups?: string[]
  active?: boolean; group?: string; description?: string; members?: string[]; app?: string
}

export const OWNERS = 'lucia-owners'
export const MEMBERS = 'lucia-users'
export const NAME_PATTERN = /^[a-z][a-z0-9_-]{0,31}$/

const text = (value: unknown): value is string => typeof value === 'string'
const names = (value: unknown): value is string[] => Array.isArray(value) && value.length <= 500 && value.every(text)
const invalid = (): never => { throw new Error('The directory information was incomplete. Refresh and try again.') }
const record = (value: unknown) => (value && typeof value === 'object' ? value : invalid()) as Record<string, unknown>

export function parseDirectory(data: unknown): Directory {
  const value = record(data)
  if (typeof value.ready !== 'boolean' || typeof value.passwordChange !== 'boolean' || !(value.checkedAt === null || text(value.checkedAt))
    || !(value.actor === null || text(value.actor)) || !Array.isArray(value.users) || !Array.isArray(value.groups)
    || !Array.isArray(value.apps) || !Array.isArray(value.changes)) invalid()
  const users = (value.users as unknown[]).map(item => {
    const user = record(item)
    if (!text(user.username) || !text(user.name) || !(user.email === null || text(user.email)) || typeof user.active !== 'boolean'
      || typeof user.protected !== 'boolean' || typeof user.synced !== 'boolean' || !names(user.groups)) invalid()
    return user as unknown as DirectoryUser
  })
  const groups = (value.groups as unknown[]).map(item => {
    const group = record(item)
    if (!text(group.name) || !text(group.description) || !(group.kind === 'directory' || group.kind === 'lucia')
      || typeof group.protected !== 'boolean' || !names(group.members)) invalid()
    return group as unknown as DirectoryGroup
  })
  const apps = (value.apps as unknown[]).map(item => {
    const app = record(item)
    if (!text(app.slug) || !text(app.name) || !(app.launchUrl === null || text(app.launchUrl)) || typeof app.fixed !== 'boolean' || !names(app.groups)) invalid()
    return app as unknown as DirectoryApp
  })
  const changes = (value.changes as unknown[]).map(item => {
    const change = record(item)
    if (!text(change.id) || !(change.action === null || text(change.action)) || !(change.target === null || text(change.target))
      || !(change.state === 'pending' || change.state === 'done' || change.state === 'failed')
      || !(change.message === null || text(change.message)) || !text(change.requestedAt)) invalid()
    return change as unknown as DirectoryChange
  })
  return { ready: value.ready as boolean, checkedAt: value.checkedAt as string | null, passwordChange: value.passwordChange as boolean,
    actor: value.actor as string | null, users, groups, apps, changes }
}

export function parseChange(data: unknown): DirectoryChange {
  return parseDirectory({ ready: false, checkedAt: null, passwordChange: false, actor: null, users: [], groups: [], apps: [], changes: [data] }).changes[0]
}

const UPPER = 'ABCDEFGHJKLMNPQRSTUVWXYZ', LOWER = 'abcdefghijkmnopqrstuvwxyz', DIGITS = '23456789', SYMBOLS = '-_.!@#%+='
const SYMBOL_SET = '!"#$%&\'()*+,-./:;<=>?@[\\]^_`{|}~'

function pick(alphabet: string, random: (limit: number) => number) { return alphabet[random(alphabet.length)] }

/** Unbiased index below limit from the platform's secure generator. */
export function secureIndex(limit: number): number {
  const values = new Uint32Array(1)
  const ceiling = Math.floor(0x100000000 / limit) * limit
  do crypto.getRandomValues(values); while (values[0] >= ceiling)
  return values[0] % limit
}

/** A 20-character password that meets the directory policy, without look-alike characters. */
export function generatePassword(random: (limit: number) => number = secureIndex): string {
  const all = UPPER + LOWER + DIGITS + SYMBOLS
  const chars = [pick(UPPER, random), pick(LOWER, random), pick(DIGITS, random), pick(SYMBOLS, random)]
  while (chars.length < 20) chars.push(pick(all, random))
  for (let index = chars.length - 1; index > 0; index--) {
    const other = random(index + 1);
    [chars[index], chars[other]] = [chars[other], chars[index]]
  }
  return chars.join('')
}

/** What the directory's password policy still needs, or an empty list when it passes. */
export function passwordProblems(value: string): string[] {
  const problems: string[] = []
  if (value.length < 14) problems.push('14 or more characters')
  if (!/[A-Z]/.test(value)) problems.push('an uppercase letter')
  if (!/[a-z]/.test(value)) problems.push('a lowercase letter')
  if (!/[0-9]/.test(value)) problems.push('a digit')
  if (![...value].some(char => SYMBOL_SET.includes(char))) problems.push('a symbol')
  return problems
}

const verbs: Record<string, string> = {
  createUser: 'Add', updateUser: 'Update', setUserGroups: 'Change groups for', setPassword: 'Set a password for',
  setUserActive: 'Change access for', deleteUser: 'Delete', createGroup: 'Create group', updateGroup: 'Update group',
  setGroupMembers: 'Change members of', deleteGroup: 'Delete group', setAppGroups: 'Change access to',
}
export function describeChange(change: Pick<DirectoryChange, 'action' | 'target'>, apps: DirectoryApp[] = []): string {
  const target = change.action === 'setAppGroups' ? apps.find(app => app.slug === change.target)?.name ?? change.target : change.target
  return `${verbs[change.action ?? ''] ?? 'Change'} ${target ?? 'the directory'}`
}

/** Pending changes for a person, group or app, so its row can show that it's saving. */
export function pendingFor(changes: DirectoryChange[], target: string): boolean {
  return changes.some(change => change.state === 'pending' && change.target === target)
}

export function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean)
  return ((words[0]?.[0] ?? '?') + (words.length > 1 ? words[words.length - 1][0] : '')).toLocaleUpperCase()
}
