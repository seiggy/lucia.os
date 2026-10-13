import fs from 'node:fs'; import path from 'node:path'
import { JSDOM } from 'jsdom'
const dom = new JSDOM('<!doctype html><body></body>'); globalThis.window = dom.window; globalThis.document = dom.window.document; Object.defineProperty(globalThis, 'navigator', { value: dom.window.navigator, configurable: true })
const { default: mermaid } = await import('mermaid')
const files = []; (function walk(d){ for (const e of fs.readdirSync(d,{withFileTypes:true})) { const p=path.join(d,e.name); e.isDirectory()?walk(p):/\.mdx?$/.test(e.name)&&files.push(p) } })('docs')
let n=0, bad=0
for (const f of files) {
  const t = fs.readFileSync(f,'utf8'); const re=/```mermaid\r?\n([\s\S]*?)```/g; let m
  while ((m=re.exec(t))) { n++; try { await mermaid.parse(m[1]) } catch(e) { bad++; console.log(f, 'block', n, String(e.message).split('\n').slice(0,4).join(' | ')) } }
}
console.log(`${n} diagrams, ${bad} invalid`)

