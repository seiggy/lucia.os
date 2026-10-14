// Monaco ships types only for its package root, so the pieces the skill editor imports one by one are typed here.
declare module 'monaco-editor/esm/vs/editor/edcore.main.js'
declare module 'monaco-editor/esm/vs/basic-languages/*'
declare module 'monaco-editor/esm/vs/language/json/monaco.contribution.js'
declare module 'monaco-editor/esm/vs/language/typescript/monaco.contribution.js' {
  type Api = typeof import('monaco-editor').typescript
  export type CompilerOptions = import('monaco-editor').typescript.CompilerOptions
  export const typescriptDefaults: Api['typescriptDefaults']
  export const javascriptDefaults: Api['javascriptDefaults']
  export const ScriptTarget: Api['ScriptTarget']
  export const ModuleKind: Api['ModuleKind']
  export const ModuleResolutionKind: Api['ModuleResolutionKind']
}
