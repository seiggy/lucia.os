export type JobStatus = 'running' | 'succeeded' | 'stopped' | 'failed' | 'unanswered'
export type JobRun = { id: string; sessionId: string; trigger: 'schedule' | 'manual'; started: string; finished?: string; status: JobStatus; error?: string }
export type Job = {
  id: string; name: string; prompts: string[]; cron: string; timeZone: string; model?: string; enabled: boolean
  tools: string[]; hosts: string[]; next?: string; last?: JobRun; running: boolean; waiting?: string
}
export type JobDraft = Pick<Job, 'name' | 'prompts' | 'cron' | 'timeZone' | 'model' | 'enabled' | 'tools' | 'hosts'>
export type PushDevice = { id: string; name: string; endpoint: string; added: string; lastUsed?: string }

const idPattern = /^[a-f0-9]{32}$/
const statuses: readonly string[] = ['running', 'succeeded', 'stopped', 'failed', 'unanswered']
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)
const unexpected = () => new Error('Lucia returned an unexpected jobs response.')
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')
const text = (value: unknown) => typeof value === 'string' && value ? value : undefined

export type Frequency = 'hourly' | 'six-hours' | 'daily' | 'weekdays' | 'weekly' | 'custom'
export type Schedule = { frequency: Frequency; time: string; day: number }
export const frequencies: { value: Frequency; label: string }[] = [
  { value: 'hourly', label: 'Every hour' }, { value: 'six-hours', label: 'Every 6 hours' }, { value: 'daily', label: 'Every day' },
  { value: 'weekdays', label: 'Weekdays' }, { value: 'weekly', label: 'Once a week' }, { value: 'custom', label: 'Custom (cron)' },
]
export const weekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

// Reads the schedules the editor writes back into its controls; anything else stays a custom cron expression.
export function scheduleOf(cron: string): Schedule {
  const fallback = { time: '07:00', day: 1 }
  if (cron === '0 * * * *') return { frequency: 'hourly', ...fallback }
  if (cron === '0 */6 * * *') return { frequency: 'six-hours', ...fallback }
  const match = /^(\d{1,2}) (\d{1,2}) \* \* (\*|1-5|[0-6])$/.exec(cron)
  if (!match || Number(match[1]) > 59 || Number(match[2]) > 23) return { frequency: 'custom', ...fallback }
  const time = `${match[2].padStart(2, '0')}:${match[1].padStart(2, '0')}`
  return match[3] === '*' ? { frequency: 'daily', time, day: 1 } : match[3] === '1-5' ? { frequency: 'weekdays', time, day: 1 }
    : { frequency: 'weekly', time, day: Number(match[3]) }
}

export function cronOf({ frequency, time, day }: Schedule): string | undefined {
  if (frequency === 'hourly') return '0 * * * *'
  if (frequency === 'six-hours') return '0 */6 * * *'
  const match = /^(\d{2}):(\d{2})$/.exec(time)
  if (frequency === 'custom' || !match) return undefined
  const at = `${Number(match[2])} ${Number(match[1])} * *`
  return frequency === 'daily' ? `${at} *` : frequency === 'weekdays' ? `${at} 1-5` : `${at} ${day}`
}

export function scheduleText(cron: string): string {
  const schedule = scheduleOf(cron)
  const time = schedule.time.replace(/^0(\d)/, '$1')
  return schedule.frequency === 'custom' ? cron : schedule.frequency === 'daily' ? `Every day at ${time}`
    : schedule.frequency === 'weekdays' ? `Weekdays at ${time}` : schedule.frequency === 'weekly' ? `${weekdays[schedule.day]}s at ${time}`
      : frequencies.find(item => item.value === schedule.frequency)!.label
}

// What a job may do with no one watching, for the list.
export function scopeText(job: Pick<Job, 'tools' | 'hosts'>, isRisky: (tool: string) => boolean): string {
  const risky = job.tools.filter(isRisky).length
  const parts = [job.tools.length ? `${job.tools.length} ${job.tools.length === 1 ? 'change' : 'changes'} without asking` : 'Asks before every change',
    ...(risky ? [`${risky} risky`] : []), ...(job.hosts.length ? [`${job.hosts.length} ${job.hosts.length === 1 ? 'site' : 'sites'}`] : [])]
  return parts.join(' · ')
}

export const emptyJob = (timeZone: string): JobDraft =>
  ({ name: '', prompts: [''], cron: '0 7 * * *', timeZone, enabled: true, tools: [], hosts: [] })

export function parseRun(value: unknown): JobRun {
  if (!record(value) || typeof value.id !== 'string' || typeof value.sessionId !== 'string' || !idPattern.test(value.sessionId)
    || typeof value.started !== 'string' || typeof value.status !== 'string' || !statuses.includes(value.status)) throw unexpected()
  const finished = text(value.finished), error = text(value.error)
  return { id: value.id, sessionId: value.sessionId, trigger: value.trigger === 'manual' ? 'manual' : 'schedule', started: value.started,
    status: value.status as JobStatus, ...(finished ? { finished } : {}), ...(error ? { error: error.slice(0, 400) } : {}) }
}

export function parseJob(value: unknown): Job {
  if (!record(value) || typeof value.id !== 'string' || !idPattern.test(value.id) || typeof value.name !== 'string' || !strings(value.prompts)
    || typeof value.cron !== 'string' || typeof value.timeZone !== 'string' || typeof value.enabled !== 'boolean'
    || !strings(value.tools) || !strings(value.hosts) || typeof value.running !== 'boolean') throw unexpected()
  const model = text(value.model), next = text(value.next), waiting = text(value.waiting)
  return { id: value.id, name: value.name, prompts: value.prompts, cron: value.cron, timeZone: value.timeZone, enabled: value.enabled,
    tools: value.tools, hosts: value.hosts, running: value.running, ...(model ? { model } : {}), ...(next ? { next } : {}),
    ...(waiting ? { waiting } : {}), ...(value.last ? { last: parseRun(value.last) } : {}) }
}

export function parseJobs(value: unknown): Job[] {
  if (!Array.isArray(value)) throw unexpected()
  return value.map(parseJob)
}

export function parseRuns(value: unknown): JobRun[] {
  if (!Array.isArray(value)) throw unexpected()
  return value.map(parseRun)
}

export function parseNext(value: unknown): string[] {
  if (!record(value) || !strings(value.next)) throw unexpected()
  return value.next
}

export function parseDevices(value: unknown): PushDevice[] {
  if (!Array.isArray(value)) throw unexpected()
  return value.map(item => {
    if (!record(item) || typeof item.id !== 'string' || typeof item.name !== 'string' || typeof item.added !== 'string'
      || typeof item.endpoint !== 'string') throw unexpected()
    const lastUsed = text(item.lastUsed)
    return { id: item.id, name: item.name, endpoint: item.endpoint, added: item.added, ...(lastUsed ? { lastUsed } : {}) }
  })
}

// The browser's subscribe call takes the server's VAPID key as raw bytes; the host sends it base64url-encoded.
export function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(base64url.length / 4) * 4, '=')
  return Uint8Array.from(atob(base64), char => char.charCodeAt(0))
}

// Names a device from its browser and system, such as "Edge on Windows", so the list tells them apart.
export function deviceName(userAgent: string): string {
  const browser = /Edg\//.test(userAgent) ? 'Edge' : /Firefox\//.test(userAgent) ? 'Firefox' : /Chrome\//.test(userAgent) ? 'Chrome'
    : /Safari\//.test(userAgent) ? 'Safari' : 'Browser'
  const system = /iPhone|iPad/.test(userAgent) ? 'iOS' : /Android/.test(userAgent) ? 'Android' : /Windows/.test(userAgent) ? 'Windows'
    : /Mac OS X/.test(userAgent) ? 'macOS' : /Linux/.test(userAgent) ? 'Linux' : ''
  return system ? `${browser} on ${system}` : browser
}

export const runStatusText = (run: JobRun) => run.status === 'running' ? 'Running' : run.status === 'succeeded' ? 'Finished'
  : run.status === 'stopped' ? 'Stopped' : run.status === 'unanswered' ? 'Approval expired' : 'Failed'
