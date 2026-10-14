import assert from 'node:assert/strict'
import { allSkills, copyName, languageChoices, languageOf, isSandboxScript, parseSkillCheck, parseSkillDetail, parseSkillList, pickSkills, sandboxBlockers,
  shortHash, sizeText, sortSkills, starterScript, validateSkillName, validFileName } from '../.checks/skills.js'
import { parseRoute } from '../.checks/dashboard.js'

const hash = 'ab'.repeat(32)
const skill = { name: 'check-zfs-health', description: 'Checks ZFS.', execution: 'node', tier: 'read', enabled: false, hash, updated: '2025-01-01T00:00:00Z',
  source: null, scripts: 1, problem: null, builtIn: false }
const file = { path: 'scripts/check.py', kind: 'script', size: 40, language: 'python', editable: true }

assert.equal(validateSkillName('check-zfs-health'), null)
assert.equal(validateSkillName('a'), null)
for (const bad of ['', 'Check', 'a--b', '-a', 'a-', 'a_b', 'a'.repeat(65)]) assert.notEqual(validateSkillName(bad), null, bad)
assert.equal(validFileName('check.py'), true)
assert.equal(validFileName('../x.py'), false)
assert.equal(validFileName('a/b.py'), false)

assert.equal(languageOf('scripts/a.mjs'), 'javascript')
assert.equal(languageOf('scripts/a.mts'), 'typescript')
assert.equal(languageOf('scripts/run.sh'), 'shell')
assert.equal(languageOf('references/n.YML'), 'yaml')
assert.equal(languageOf('assets/data.csv'), 'text')
assert.equal(languageOf('scripts/noext'), 'text')
assert.deepEqual(languageChoices('sandbox').map(item => item.id), ['javascript', 'python'])
assert.deepEqual(languageChoices('node').map(item => item.id), ['javascript', 'typescript', 'python', 'shell', 'csharp'])
assert.equal(isSandboxScript('scripts/a.js'), true)
assert.equal(isSandboxScript('scripts/a.ts'), false)
assert.deepEqual(sandboxBlockers([file, { ...file, path: 'scripts/a.sh' }, { ...file, kind: 'reference', path: 'references/a.md' }]), ['scripts/a.sh'])
assert.ok(starterScript('python').includes('LUCIA_SKILL_ARGS'))
assert.equal(starterScript('markdown'), '')

assert.deepEqual(pickSkills([], ['a']), ['a'])
assert.deepEqual(pickSkills(['a'], ['a', allSkills]), [allSkills])
assert.deepEqual(pickSkills([allSkills], [allSkills, 'b']), ['b'])

const list = parseSkillList({ skills: [skill], kvmNodes: [] })
assert.equal(list.skills[0].name, 'check-zfs-health')
assert.deepEqual(list.kvmNodes, [])
assert.equal(list.skills[0].builtIn, false)
const builtIn = { ...skill, name: 'app-troubleshooting', enabled: true, source: 'built-in', scripts: 0, builtIn: true }
const mixed = parseSkillList({ skills: [skill, { ...skill, name: 'aaa-mine' }, builtIn, { ...builtIn, name: 'os-security-patching' }] })
assert.deepEqual(mixed.skills.map(item => item.name), ['app-troubleshooting', 'os-security-patching', 'aaa-mine', 'check-zfs-health'])
assert.deepEqual(mixed.skills.map(item => item.builtIn), [true, true, false, false])
assert.equal(parseSkillDetail({ ...builtIn, body: '# Guide', files: [] }).builtIn, true)
assert.equal(parseSkillDetail({ ...skill, builtIn: 'yes', body: '', files: [] }).builtIn, false)
assert.deepEqual(sortSkills([{ name: 'b', builtIn: false }, { name: 'c', builtIn: true }, { name: 'a', builtIn: false }]).map(item => item.name), ['c', 'a', 'b'])
assert.equal(copyName('app-troubleshooting', []), 'my-app-troubleshooting')
assert.equal(copyName('app-troubleshooting', ['my-app-troubleshooting', 'my-app-troubleshooting-2']), 'my-app-troubleshooting-3')
assert.equal(validateSkillName(copyName('a'.repeat(64), [])), null)
assert.equal(validateSkillName(copyName('a'.repeat(64), ['m' + 'y-' + 'a'.repeat(62)])), null)
assert.throws(() => parseSkillList({ skills: [{ ...skill, execution: 'cloud' }] }))
assert.throws(() => parseSkillList({}))
const detail = parseSkillDetail({ ...skill, allowedDomains: ['*.example.com'], license: null, compatibility: null, body: '# Hi', files: [file] })
assert.equal(detail.files[0].language, 'python')
assert.equal(detail.body, '# Hi')
assert.throws(() => parseSkillDetail({ ...skill, files: [] }))
assert.throws(() => parseSkillDetail({ ...skill, body: '', files: [{ ...file, kind: 'binary' }] }))
const check = parseSkillCheck({ ok: false, diagnostics: [{ line: 0, column: 3, severity: 'warning', message: 'x' }, { line: 2, column: 1, message: 'y' }], checkedOn: 'lucialab01', problem: null })
assert.equal(check.diagnostics[0].line, 1)
assert.equal(check.diagnostics[0].severity, 'warning')
assert.equal(check.diagnostics[1].severity, 'error')
assert.throws(() => parseSkillCheck({ ok: true }))
assert.equal(shortHash(hash), 'abababab')
assert.equal(sizeText(512), '512 B')
assert.equal(sizeText(2048), '2.0 KB')
assert.deepEqual(parseRoute('#/settings/assistant/skills'), { page: 'assistant-skills' })
console.log('Skill checks passed: names, languages, sandbox limits, pickers, parsers and routing.')
