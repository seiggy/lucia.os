import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js'
import 'monaco-editor/esm/vs/editor/edcore.main.js'
import 'monaco-editor/esm/vs/basic-languages/python/python.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/javascript/javascript.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/typescript/typescript.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/shell/shell.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/markdown/markdown.contribution.js'
import 'monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution.js'
import 'monaco-editor/esm/vs/language/json/monaco.contribution.js'
import * as typescript from 'monaco-editor/esm/vs/language/typescript/monaco.contribution.js'
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker.js?worker'
import JsonWorker from 'monaco-editor/esm/vs/language/json/json.worker.js?worker'
import TypeScriptWorker from 'monaco-editor/esm/vs/language/typescript/ts.worker.js?worker'
import type { SkillLanguage } from './skills'

// Monaco's language services run in same-origin workers that Vite bundles, so nothing loads from a CDN.
self.MonacoEnvironment = {
  getWorker: (_id, label) => label === 'typescript' || label === 'javascript' ? new TypeScriptWorker()
    : label === 'json' ? new JsonWorker() : new EditorWorker(),
}

export { monaco }
export const themeName = 'lucia'
export const markerOwner = 'lucia-check'

// What a skill script can rely on when it runs under Node, so `process`, `console` and `require` don't read as errors.
const nodeDeclarations = `
declare const process: {
  argv: string[]; env: Record<string, string | undefined>; platform: string; exitCode?: number;
  cwd(): string; exit(code?: number): never;
  stdout: { write(text: string): boolean }; stderr: { write(text: string): boolean };
  stdin: { setEncoding(encoding: string): void; on(event: string, listener: (...args: any[]) => void): void; [Symbol.asyncIterator](): AsyncIterableIterator<any> };
};
declare const console: { log(...data: any[]): void; error(...data: any[]): void; warn(...data: any[]): void; info(...data: any[]): void; debug(...data: any[]): void };
declare const Buffer: any;
declare const module: any;
declare const exports: any;
declare const __dirname: string;
declare const __filename: string;
declare function require(id: string): any;
declare function setTimeout(handler: (...args: any[]) => void, timeout?: number, ...args: any[]): number;
declare function clearTimeout(id: number | undefined): void;
declare function setInterval(handler: (...args: any[]) => void, timeout?: number, ...args: any[]): number;
declare function clearInterval(id: number | undefined): void;
declare function fetch(input: string, init?: { method?: string; headers?: Record<string, string>; body?: string }): Promise<{ ok: boolean; status: number; text(): Promise<string>; json(): Promise<any> }>;
declare module 'node:*';
declare module 'fs'; declare module 'fs/promises'; declare module 'path'; declare module 'os'; declare module 'child_process';
declare module 'util'; declare module 'crypto'; declare module 'http'; declare module 'https'; declare module 'url'; declare module 'readline';
`

const compilerOptions: typescript.CompilerOptions = {
  target: typescript.ScriptTarget.ESNext,
  module: typescript.ModuleKind.ESNext,
  moduleResolution: typescript.ModuleResolutionKind.NodeJs,
  lib: ['esnext'],
  allowJs: true,
  allowNonTsExtensions: true,
  checkJs: false,
  noEmit: true,
  esModuleInterop: true,
  skipLibCheck: true,
}

let configured = false
export function configureMonaco() {
  if (configured) return
  configured = true
  for (const defaults of [typescript.typescriptDefaults, typescript.javascriptDefaults]) {
    defaults.setCompilerOptions(compilerOptions)
    defaults.setDiagnosticsOptions({ noSemanticValidation: false, noSyntaxValidation: false })
    defaults.setEagerModelSync(true)
    defaults.addExtraLib(nodeDeclarations, 'file:///lucia/node-globals.d.ts')
  }
}

// Type-checks JavaScript too when asked; off by default so plain scripts stay quiet.
export function setCheckJs(on: boolean) {
  typescript.javascriptDefaults.setCompilerOptions({ ...compilerOptions, checkJs: on })
}

export const monacoLanguage = (language: SkillLanguage | null) => language === 'shell' ? 'shell' : language === 'text' || !language ? 'plaintext' : language

// Resolves any CSS colour (including color-mix) to the #rrggbb[aa] form Monaco themes need.
function hex(value: string, fallback: string): string {
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = 1
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context || !value) return fallback
  context.clearRect(0, 0, 1, 1)
  context.fillStyle = fallback
  context.fillStyle = value
  context.fillRect(0, 0, 1, 1)
  const [r, g, b, a] = context.getImageData(0, 0, 1, 1).data
  return `#${[r, g, b, ...(a < 255 ? [a] : [])].map(part => part.toString(16).padStart(2, '0')).join('')}`
}

// Builds the editor theme from the console's CSS variables, so the tokens stay the single source of colour.
export function applyTheme() {
  const root = document.documentElement
  const style = getComputedStyle(root)
  const dark = root.dataset.scheme === 'dark'
  const token = (name: string, fallback: string) => hex(style.getPropertyValue(name).trim(), fallback)
  const fallbackText = dark ? 'white' : 'black', fallbackSurface = dark ? 'black' : 'white'
  const surface = token('--surface', fallbackSurface), text = token('--text', fallbackText), muted = token('--muted', fallbackText)
  const line = token('--line', fallbackText), subtle = token('--surface-subtle', fallbackSurface), accent = token('--accent', fallbackText)
  const ink = token('--accent-ink', accent), soft = token('--accent-soft', subtle)
  const green = token('--green-ink', text), amber = token('--amber-ink', text), plum = token('--plum-ink', text), failed = token('--failed-ink', text)
  const bare = (color: string) => color.slice(1, 7)
  monaco.editor.defineTheme(themeName, {
    base: dark ? 'vs-dark' : 'vs',
    inherit: true,
    rules: [
      { token: '', foreground: bare(text) },
      { token: 'comment', foreground: bare(muted), fontStyle: 'italic' },
      { token: 'keyword', foreground: bare(plum) },
      { token: 'string', foreground: bare(green) },
      { token: 'number', foreground: bare(amber) },
      { token: 'regexp', foreground: bare(amber) },
      { token: 'type', foreground: bare(ink) },
      { token: 'tag', foreground: bare(ink) },
      { token: 'attribute.name', foreground: bare(amber) },
      { token: 'variable', foreground: bare(text) },
      { token: 'invalid', foreground: bare(failed) },
    ],
    colors: {
      'editor.background': surface,
      'editor.foreground': text,
      'editorLineNumber.foreground': muted,
      'editorLineNumber.activeForeground': text,
      'editor.lineHighlightBackground': subtle,
      'editor.lineHighlightBorder': subtle,
      'editor.selectionBackground': soft,
      'editor.inactiveSelectionBackground': soft,
      'editorCursor.foreground': accent,
      'editorIndentGuide.background1': line,
      'editorWidget.background': surface,
      'editorWidget.border': line,
      'editorSuggestWidget.background': surface,
      'editorSuggestWidget.border': line,
      'editorSuggestWidget.selectedBackground': subtle,
      'editorHoverWidget.background': surface,
      'editorHoverWidget.border': line,
      'editorGutter.background': surface,
      'scrollbarSlider.background': `#${bare(muted)}55`,
      'focusBorder': ink,
    },
  })
  monaco.editor.setTheme(themeName)
}
