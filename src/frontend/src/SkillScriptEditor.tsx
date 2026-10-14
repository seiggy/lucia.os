import { useEffect, useRef } from 'react'
import { applyTheme, configureMonaco, markerOwner, monaco, monacoLanguage, setCheckJs } from './monacoSetup'
import type { SkillDiagnostic, SkillLanguage } from './skills'
import './SkillScriptEditor.css'

type Props = {
  // Names the file; also the model's address, so the language services treat each file on its own.
  modelKey: string
  // The text to open. Changing it replaces the model, so keep it fixed while the user types and read edits through onChange.
  value: string
  language: SkillLanguage | null
  label: string
  readOnly?: boolean
  checkJs?: boolean
  diagnostics?: SkillDiagnostic[]
  onChange?: (value: string) => void
  onSave?: () => void
}

// Monaco, loaded on demand: its own chunk, with workers served from this host.
export default function SkillScriptEditor({ modelKey, value, language, label, readOnly = false, checkJs = false, diagnostics, onChange, onSave }: Props) {
  const host = useRef<HTMLDivElement>(null)
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)
  const callbacks = useRef({ onChange, onSave })
  useEffect(() => { callbacks.current = { onChange, onSave } })

  useEffect(() => {
    configureMonaco()
    applyTheme()
    const instance = monaco.editor.create(host.current!, {
      model: null,
      automaticLayout: true,
      minimap: { enabled: false },
      fontFamily: 'ui-monospace, SFMono-Regular, Consolas, monospace',
      fontSize: 14,
      scrollBeyondLastLine: false,
      fixedOverflowWidgets: true,
      tabSize: 2,
      padding: { top: 12, bottom: 12 },
    })
    editor.current = instance
    instance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => callbacks.current.onSave?.())
    // The console sets its colour mode and accent on <html>; follow them so the editor never drifts.
    let frame = 0
    const observer = new MutationObserver(() => { cancelAnimationFrame(frame); frame = requestAnimationFrame(applyTheme) })
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-scheme', 'style'] })
    return () => {
      observer.disconnect()
      cancelAnimationFrame(frame)
      instance.dispose()
      editor.current = null
    }
  }, [])

  useEffect(() => { editor.current?.updateOptions({ ariaLabel: label, readOnly, domReadOnly: readOnly }) }, [label, readOnly])
  useEffect(() => { setCheckJs(checkJs) }, [checkJs])

  useEffect(() => {
    const instance = editor.current
    if (!instance) return
    const uri = monaco.Uri.parse(`file:///skill/${modelKey}`)
    monaco.editor.getModel(uri)?.dispose()
    const model = monaco.editor.createModel(value, monacoLanguage(language), uri)
    instance.setModel(model)
    const subscription = model.onDidChangeContent(() => callbacks.current.onChange?.(model.getValue()))
    return () => { subscription.dispose(); instance.setModel(null); model.dispose() }
  }, [modelKey, value, language])

  useEffect(() => {
    const model = editor.current?.getModel()
    if (!model) return
    // The check runs remotely while the owner keeps editing, so its lines may no longer exist; Monaco throws on those.
    const lines = model.getLineCount()
    monaco.editor.setModelMarkers(model, markerOwner, (diagnostics ?? []).map(item => {
      const line = Math.min(Math.max(item.line, 1), lines)
      const endLine = Math.min(Math.max(item.endLine ?? line, line), lines)
      return {
        severity: item.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Error,
        message: item.message,
        startLineNumber: line,
        startColumn: Math.max(item.column, 1),
        endLineNumber: endLine,
        endColumn: item.endColumn ?? (item.endLine ? Math.max(item.column, 1) : model.getLineMaxColumn(line)),
      }
    }))
  }, [diagnostics, modelKey, value, language])

  return <div className="skill-editor" ref={host} />
}
