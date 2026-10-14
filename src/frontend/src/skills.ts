export type SkillExecution = 'node' | 'spark' | 'sandbox'
export type SkillTier = 'read' | 'change' | 'destructive'
export type SkillLanguage = 'javascript' | 'typescript' | 'python' | 'shell' | 'csharp' | 'markdown' | 'json' | 'yaml' | 'text'
export type SkillAuthor = 'owner' | 'assistant' | 'lucia'
export type SkillFileKind = 'script' | 'reference' | 'asset'

export type SkillSummary = {
  name: string; description: string; execution: SkillExecution; tier: SkillTier; enabled: boolean; hash: string; updated: string
  source: string | null; scripts: number; problem: string | null; builtIn: boolean
  author: SkillAuthor; needsReview: boolean; rationale: string | null; sourceChats: string[]; warnings: string[]
}
export type SkillFile = { path: string; kind: SkillFileKind; size: number; language: SkillLanguage | null; editable: boolean }
export type SkillNetwork = 'none' | 'bridge'
export type SkillDetail = SkillSummary & { network: SkillNetwork; allowedDomains: string[]; license: string | null; compatibility: string | null; body: string; files: SkillFile[]; nodes: SkillNodeState[] }
export type SkillNodeState = { node: string; synced: boolean }
export type SkillDiagnostic = { line: number; column: number; endLine?: number; endColumn?: number; severity: 'error' | 'warning'; message: string }
export type SkillCheck = { ok: boolean; diagnostics: SkillDiagnostic[]; checkedOn: string | null; problem: string | null }
export type SkillFileContent = { path: string; content: string; language: SkillLanguage | null; size: number; editable: boolean }

export const allSkills = '*'
export const maxNameLength = 64
export const maxDescriptionLength = 1024
const namePattern = /^[a-z0-9]+(-[a-z0-9]+)*$/

export function validateSkillName(name: string): string | null {
  if (!name) return 'Give the skill a name.'
  if (name.length > maxNameLength) return `Use at most ${maxNameLength} characters.`
  return namePattern.test(name) ? null : 'Use lowercase letters, numbers and single hyphens, like check-zfs-health.'
}

export const executions: { value: SkillExecution; label: string; hint: string }[] = [
  { value: 'node', label: 'Run on server', hint: 'Runs on the server the assistant is working on.' },
  { value: 'spark', label: 'Spark', hint: 'Runs on the Spark, in a throwaway container.' },
  { value: 'sandbox', label: 'Hyperlight sandbox', hint: 'Runs in an isolated micro-VM. JavaScript and Python only.' },
]
export const tiers: { value: SkillTier; label: string; hint: string }[] = [
  { value: 'read', label: 'Read', hint: 'Only looks; runs without asking.' },
  { value: 'change', label: 'Change', hint: 'Changes things; asks first.' },
  { value: 'destructive', label: 'Risky', hint: 'Can remove or break things; asks first.' },
]
export const executionLabel = (value: SkillExecution) => executions.find(item => item.value === value)?.label ?? value
export const tierLabel = (value: SkillTier) => tiers.find(item => item.value === value)?.label ?? value

export type ScriptLanguage = { id: SkillLanguage; label: string; extension: string; sandbox: boolean }
export const scriptLanguages: ScriptLanguage[] = [
  { id: 'javascript', label: 'JavaScript', extension: '.js', sandbox: true },
  { id: 'typescript', label: 'TypeScript', extension: '.ts', sandbox: false },
  { id: 'python', label: 'Python 3', extension: '.py', sandbox: true },
  { id: 'shell', label: 'Bash', extension: '.sh', sandbox: false },
  { id: 'csharp', label: 'C# (.NET 10)', extension: '.cs', sandbox: false },
]
export const languageChoices = (execution: SkillExecution) => scriptLanguages.filter(item => execution !== 'sandbox' || item.sandbox)

const extensions: Record<string, SkillLanguage> = {
  js: 'javascript', mjs: 'javascript', ts: 'typescript', mts: 'typescript', py: 'python', sh: 'shell', cs: 'csharp',
  md: 'markdown', json: 'json', yaml: 'yaml', yml: 'yaml',
}
export function languageOf(path: string): SkillLanguage {
  const dot = path.lastIndexOf('.')
  return (dot > path.lastIndexOf('/') && extensions[path.slice(dot + 1).toLowerCase()]) || 'text'
}
export const isSandboxScript = (path: string) => /\.(js|mjs|py)$/i.test(path)
export const languageLabel = (language: SkillLanguage | null) => scriptLanguages.find(item => item.id === language)?.label
  ?? (language === 'markdown' ? 'Markdown' : language === 'json' ? 'JSON' : language === 'yaml' ? 'YAML' : 'Text')

// Scripts that a sandbox skill can't run, for the warning before switching a skill to the sandbox.
export const sandboxBlockers = (files: SkillFile[]) => files.filter(file => file.kind === 'script' && !isSandboxScript(file.path)).map(file => file.path)

export const fileName = (path: string) => path.slice(path.lastIndexOf('/') + 1)
export const fileGroups: { kind: SkillFileKind; label: string; folder: string }[] = [
  { kind: 'script', label: 'Scripts', folder: 'scripts/' },
  { kind: 'reference', label: 'References', folder: 'references/' },
  { kind: 'asset', label: 'Assets', folder: 'assets/' },
]
export const validFileName = (name: string) => /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/.test(name) && !name.includes('..')

// Picking "all enabled skills" replaces any single ones, and picking a single one drops "all".
export function pickSkills(before: string[], after: string[]): string[] {
  if (after.includes(allSkills) && !before.includes(allSkills)) return [allSkills]
  return after.filter(name => name !== allSkills)
}

const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)
const unexpected = () => new Error('Lucia returned an unexpected skills response.')
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')
const optionalText = (value: unknown) => typeof value === 'string' ? value : null
const executionValues: readonly string[] = ['node', 'spark', 'sandbox']
const tierValues: readonly string[] = ['read', 'change', 'destructive']
const kindValues: readonly string[] = ['script', 'reference', 'asset']
const languageValues: readonly string[] = ['javascript', 'typescript', 'python', 'shell', 'csharp', 'markdown', 'json', 'yaml', 'text']

export function parseSkill(value: unknown): SkillSummary {
  if (!record(value) || typeof value.name !== 'string' || typeof value.description !== 'string' || typeof value.execution !== 'string'
    || !executionValues.includes(value.execution) || typeof value.tier !== 'string' || !tierValues.includes(value.tier)
    || typeof value.enabled !== 'boolean' || typeof value.hash !== 'string' || typeof value.updated !== 'string') throw unexpected()
  return { name: value.name, description: value.description, execution: value.execution as SkillExecution, tier: value.tier as SkillTier,
    enabled: value.enabled, hash: value.hash, updated: value.updated, source: optionalText(value.source),
    scripts: typeof value.scripts === 'number' ? value.scripts : 0, problem: optionalText(value.problem), builtIn: value.builtIn === true,
    author: value.author === 'assistant' || value.author === 'lucia' ? value.author : value.builtIn === true ? 'lucia' : 'owner',
    needsReview: value.needsReview === true, rationale: optionalText(value.rationale)?.trim() || null,
    sourceChats: strings(value.sourceChats) ? value.sourceChats : [], warnings: strings(value.warnings) ? value.warnings : [] }
}

// Built-in skills come first, then the owner's by name.
export const sortSkills = <T extends { name: string; builtIn: boolean }>(skills: T[]): T[] =>
  [...skills].sort((a, b) => Number(b.builtIn) - Number(a.builtIn) || a.name.localeCompare(b.name))

// A free name for a copy of a skill: "my-" in front, then a number if that is taken.
export function copyName(name: string, taken: string[]): string {
  const fit = (text: string) => text.slice(0, maxNameLength).replace(/-+$/, '')
  const base = fit(`my-${name}`)
  if (!taken.includes(base)) return base
  for (let n = 2; n < 1000; n++) {
    const suffix = `-${n}`
    const candidate = fit(base.slice(0, maxNameLength - suffix.length)) + suffix
    if (!taken.includes(candidate)) return candidate
  }
  return base
}

export function parseSkillList(value: unknown): { skills: SkillSummary[]; kvmNodes: string[]; needsReview: number } {
  if (!record(value) || !Array.isArray(value.skills)) throw unexpected()
  const skills = sortSkills(value.skills.map(parseSkill))
  // An older host sends no count, so count the drafts.
  const needsReview = typeof value.needsReview === 'number' && value.needsReview >= 0 ? Math.floor(value.needsReview) : skills.filter(skill => skill.needsReview).length
  return { skills, kvmNodes: strings(value.kvmNodes) ? value.kvmNodes : [], needsReview }
}

// Drafts the assistant wrote wait in their own group; the rest keep their order.
export const splitSkills = <T extends { needsReview: boolean }>(skills: T[]): { review: T[]; rest: T[] } =>
  ({ review: skills.filter(skill => skill.needsReview), rest: skills.filter(skill => !skill.needsReview) })

// The skills page announces changes so the navigation badge can refresh.
export const skillsChangedEvent = 'lucia:skills-changed'

// The skill a `?skill=` link in the address opens.
export function skillFromHash(hash: string): string | null {
  const name = new URLSearchParams(hash.split('?')[1] ?? '').get('skill')
  return name && validateSkillName(name) === null ? name : null
}

export function parseSkillFile(value: unknown): SkillFile {
  if (!record(value) || typeof value.path !== 'string' || typeof value.kind !== 'string' || !kindValues.includes(value.kind)
    || typeof value.size !== 'number') throw unexpected()
  return { path: value.path, kind: value.kind as SkillFileKind, size: value.size,
    language: typeof value.language === 'string' && languageValues.includes(value.language) ? value.language as SkillLanguage : languageOf(value.path),
    editable: value.editable === true }
}

function parseNodeStates(value: unknown): SkillNodeState[] {
  if (!Array.isArray(value)) return []
  return value.flatMap(item => typeof item === 'object' && item !== null && typeof (item as { node?: unknown }).node === 'string'
    ? [{ node: (item as { node: string }).node, synced: (item as { synced?: unknown }).synced === true }] : [])
}

export function parseSkillDetail(value: unknown): SkillDetail {
  const summary = parseSkill(value)
  const detail = value as Record<string, unknown>
  if (typeof detail.body !== 'string' || !Array.isArray(detail.files)) throw unexpected()
  return { ...summary, network: detail.network === 'bridge' ? 'bridge' : 'none', allowedDomains: strings(detail.allowedDomains) ? detail.allowedDomains : [], license: optionalText(detail.license),
    compatibility: optionalText(detail.compatibility), body: detail.body, files: detail.files.map(parseSkillFile), nodes: parseNodeStates(detail.nodes) }
}

export function parseSkillFileContent(value: unknown): SkillFileContent {
  if (!record(value) || typeof value.path !== 'string' || typeof value.content !== 'string') throw unexpected()
  return { path: value.path, content: value.content, size: typeof value.size === 'number' ? value.size : value.content.length,
    language: typeof value.language === 'string' && languageValues.includes(value.language) ? value.language as SkillLanguage : languageOf(value.path),
    editable: value.editable === true }
}

export function parseSkillCheck(value: unknown): SkillCheck {
  if (!record(value) || typeof value.ok !== 'boolean' || !Array.isArray(value.diagnostics)) throw unexpected()
  const diagnostics = value.diagnostics.map((item): SkillDiagnostic => {
    if (!record(item) || typeof item.message !== 'string' || typeof item.line !== 'number') throw unexpected()
    const number = (field: unknown) => typeof field === 'number' ? field : undefined
    return { line: Math.max(1, item.line), column: Math.max(1, number(item.column) ?? 1), endLine: number(item.endLine), endColumn: number(item.endColumn),
      severity: item.severity === 'warning' ? 'warning' : 'error', message: item.message }
  })
  return { ok: value.ok, diagnostics, checkedOn: optionalText(value.checkedOn), problem: optionalText(value.problem) }
}

// A few lines that read the arguments, so a new script starts from something that runs.
export function starterScript(language: SkillLanguage): string {
  switch (language) {
    case 'javascript': return `// Arguments arrive as JSON in LUCIA_SKILL_ARGS.\nconst args = JSON.parse(process.env.LUCIA_SKILL_ARGS ?? '{}')\nconsole.log(JSON.stringify({ ok: true, args }))\n`
    case 'typescript': return `// Arguments arrive as JSON in LUCIA_SKILL_ARGS.\nconst args: Record<string, unknown> = JSON.parse(process.env.LUCIA_SKILL_ARGS ?? '{}')\nconsole.log(JSON.stringify({ ok: true, args }))\n`
    case 'python': return `import json\nimport os\n\n# Arguments arrive as JSON in LUCIA_SKILL_ARGS.\nargs = json.loads(os.environ.get("LUCIA_SKILL_ARGS", "{}"))\nprint(json.dumps({"ok": True, "args": args}))\n`
    case 'shell': return `#!/usr/bin/env bash\nset -euo pipefail\n\n# Arguments arrive as JSON in LUCIA_SKILL_ARGS.\necho "\${LUCIA_SKILL_ARGS:-{}}"\n`
    case 'csharp': return `// A .NET 10 file-based app. Arguments arrive as JSON in LUCIA_SKILL_ARGS.\nvar args = Environment.GetEnvironmentVariable("LUCIA_SKILL_ARGS") ?? "{}";\nConsole.WriteLine(args);\n`
    default: return ''
  }
}

// The short form of a content hash, enough to tell versions apart.
export const shortHash = (hash: string) => hash.slice(0, 8)
export const sizeText = (bytes: number) => bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(bytes < 10240 ? 1 : 0)} KB`
