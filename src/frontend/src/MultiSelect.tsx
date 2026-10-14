import { useId, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { Icon } from './Icon'
import './MultiSelect.css'

export type MultiOption = { value: string; label: string }
type Props = { options: MultiOption[]; selected: string[]; onChange: (selected: string[]) => void; labelledBy: string; placeholder?: string; disabled?: boolean; pillLabels?: Record<string, string> }

// A box that shows what's chosen as removable pills, with a list to pick from that narrows as you type.
export function MultiSelect({ options, selected, onChange, labelledBy, placeholder = 'Select…', disabled, pillLabels }: Props) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const root = useRef<HTMLDivElement>(null)
  const input = useRef<HTMLInputElement>(null)
  const listId = useId()
  const shown = options.filter(option => `${option.label} ${option.value}`.toLowerCase().includes(query.trim().toLowerCase()))
  const toggle = (value: string) => onChange(selected.includes(value) ? selected.filter(item => item !== value) : [...selected, value])
  const optionId = (index: number) => `${listId}-${index}`

  function onKeyDown(event: KeyboardEvent) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!open) { setOpen(true); return }
      setActive(current => (current + (event.key === 'ArrowDown' ? 1 : -1) + shown.length) % Math.max(shown.length, 1))
    } else if (event.key === 'Enter' && open) {
      event.preventDefault()
      if (shown[active]) toggle(shown[active].value)
    } else if (event.key === 'Escape' && open) {
      event.stopPropagation()
      setOpen(false)
    } else if (event.key === 'Backspace' && !query && selected.length) {
      onChange(selected.slice(0, -1))
    }
  }

  return <div className="multi-select" ref={root} onBlur={event => { if (!root.current?.contains(event.relatedTarget)) setOpen(false) }}>
    <div className={`multi-control${disabled ? ' disabled' : ''}`} onClick={() => { if (!disabled) { input.current?.focus(); setOpen(true) } }}>
      {selected.map(value => <span key={value}       className="multi-pill">{pillLabels?.[value] ?? value}
              <button type="button" disabled={disabled} aria-label={`Remove ${pillLabels?.[value] ?? value}`}
          onClick={event => { event.stopPropagation(); toggle(value); input.current?.focus() }}><Icon name="close" /></button></span>)}
      <input ref={input} className="multi-input" role="combobox" aria-expanded={open} aria-controls={listId} aria-labelledby={labelledBy} aria-autocomplete="list"
        aria-activedescendant={open && shown[active] ? optionId(active) : undefined} value={query} disabled={disabled} autoComplete="off" spellCheck={false}
        placeholder={selected.length ? '' : placeholder}
        onChange={event => { setQuery(event.target.value); setActive(0); setOpen(true) }} onFocus={() => setOpen(true)} onKeyDown={onKeyDown} />
      <span className="multi-chevron" aria-hidden="true"><Icon name="chevron" /></span>
    </div>
    {open && <ul id={listId} role="listbox" aria-multiselectable="true" aria-labelledby={labelledBy} className="multi-list">
      {shown.length === 0 ? <li className="multi-empty" role="presentation">Nothing matches.</li>
        : shown.map((option, index) => {
          const on = selected.includes(option.value)
          return <li key={option.value} id={optionId(index)} role="option" aria-selected={on} className={`multi-option${index === active ? ' active' : ''}`}
            onMouseDown={event => event.preventDefault()} onClick={() => toggle(option.value)} onMouseEnter={() => setActive(index)}>
            <span className="multi-check" aria-hidden="true">{on && <Icon name="check" />}</span>{option.label}</li>
        })}
    </ul>}
  </div>
}
