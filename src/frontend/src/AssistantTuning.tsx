import { useId } from 'react'
import { DropdownMenu, Popover } from 'radix-ui'
import { CheckIcon, ListChecksIcon, PlayIcon } from 'lucide-react'
import { chosenEffort, contextFill, currentTier, effortLabel, effortShort, tokenText } from './assistant'
import type { AssistantMode, AssistantModel, ContextUse } from './assistant'

// One button holds the answer mode and swaps it on click, so the composer shows the state rather than two choices.
export function ModeButton({ mode, onMode }: { mode: AssistantMode; onMode: (mode: AssistantMode) => void }) {
  const plan = mode === 'plan'
  return <button type="button" className="assistant-state" data-mode={mode}
    aria-label={`Answer mode: ${plan ? 'Plan' : 'Execute'}. Switch to ${plan ? 'Execute' : 'Plan'}.`}
    title={plan ? 'Plan: looks things up and plans without changing anything. Click for Execute.'
      : 'Execute: carries out changes, asking first unless you’ve allowed them. Click for Plan.'}
    onClick={() => onMode(plan ? 'execute' : 'plan')}>
    {plan ? <ListChecksIcon /> : <PlayIcon />}{plan ? 'Plan' : 'Execute'}</button>
}

type ReasoningProps = { model?: AssistantModel; saved?: string; onChange: (effort: string | undefined) => void }

// A small button that shows the effort by its short name and opens the efforts the model accepts. Models without any show nothing.
export function ReasoningMenu({ model, saved, onChange }: ReasoningProps) {
  const efforts = model?.reasoning
  if (!model || !efforts?.length) return null
  const chosen = chosenEffort(model, saved)
  const shown = chosen ?? model.defaultReasoning
  return <DropdownMenu.Root>
    <DropdownMenu.Trigger asChild>
      <button type="button" className="assistant-chip" aria-label={`Reasoning effort: ${shown ? effortLabel(shown) : 'the model’s default'}`}
        title="Reasoning effort">{shown ? effortShort(shown) : 'A'}</button>
    </DropdownMenu.Trigger>
    <DropdownMenu.Portal>
      <DropdownMenu.Content className="assistant assistant-menu" side="top" align="start" sideOffset={6} collisionPadding={16}>
        <DropdownMenu.Label className="assistant-menu-label">Reasoning effort</DropdownMenu.Label>
        <DropdownMenu.RadioGroup value={chosen ?? ''} onValueChange={value => onChange(value || undefined)}>
          <DropdownMenu.RadioItem value="" className="assistant-menu-item">
            <span className="assistant-menu-short">A</span>
            <span>Model default{model.defaultReasoning ? ` (${effortLabel(model.defaultReasoning)})` : ''}</span>
            <DropdownMenu.ItemIndicator className="assistant-menu-check"><CheckIcon /></DropdownMenu.ItemIndicator>
          </DropdownMenu.RadioItem>
          {efforts.map(effort => <DropdownMenu.RadioItem key={effort} value={effort} className="assistant-menu-item">
            <span className="assistant-menu-short">{effortShort(effort)}</span><span>{effortLabel(effort)}</span>
            <DropdownMenu.ItemIndicator className="assistant-menu-check"><CheckIcon /></DropdownMenu.ItemIndicator>
          </DropdownMenu.RadioItem>)}
        </DropdownMenu.RadioGroup>
        <p className="assistant-menu-note">Less reasoning answers faster and uses fewer tokens. Harder tasks may need more.</p>
      </DropdownMenu.Content>
    </DropdownMenu.Portal>
  </DropdownMenu.Root>
}

function Donut({ share }: { share?: number }) {
  const radius = 7.5, circumference = 2 * Math.PI * radius
  return <svg className="assistant-donut" viewBox="0 0 20 20" aria-hidden="true" data-tone={share === undefined ? undefined : share >= 0.9 ? 'high' : share >= 0.7 ? 'mid' : undefined}>
    <circle className="track" cx="10" cy="10" r={radius} />
    {share !== undefined && share > 0 && <circle className="arc" cx="10" cy="10" r={radius} transform="rotate(-90 10 10)"
      strokeDasharray={`${Math.max(share * circumference, 1.5)} ${circumference}`} />}
  </svg>
}

type GaugeProps = { model?: AssistantModel; tier?: string; last?: ContextUse; onTier: (tier: string) => void }

// A donut showing how full the context window is. It opens the numbers, and for models with a choice of windows, the choice.
export function ContextGauge({ model, tier: saved, last, onTier }: GaugeProps) {
  const tiersLabel = useId()
  const tier = currentTier(model, saved)
  const { used, limit, share } = contextFill(model, tier, last)
  const percent = share === undefined ? undefined : Math.round(share * 100)
  const parts = [['System prompt', last?.system], ['Tools', last?.tools], ['Conversation', last?.conversation]] as const
  return <Popover.Root>
    <Popover.Trigger asChild>
      <button type="button" className="assistant-gauge" aria-label={percent === undefined ? 'Context window' : `Context window: ${percent}% full`} title="Context window">
        <Donut share={share} /></button>
    </Popover.Trigger>
    <Popover.Portal>
      <Popover.Content className="assistant assistant-popover" side="top" align="start" sideOffset={6} collisionPadding={16}>
        <h3>Context window</h3>
        {limit ? <>
          <p className="assistant-context-total"><strong>{percent}%</strong> full</p>
          <div className="assistant-context-bar" role="progressbar" aria-label="Context used" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}>
            <span style={{ width: `${percent}%` }} data-tone={share! >= 0.9 ? 'high' : share! >= 0.7 ? 'mid' : undefined} /></div>
          <p className="assistant-context-count">{used.toLocaleString()} of {limit.toLocaleString()} tokens</p>
        </> : <p className="assistant-context-count">{last ? `${used.toLocaleString()} tokens used. This model doesn’t report its limit.` : 'This model doesn’t report its limit yet.'}</p>}
        {parts.some(([, tokens]) => tokens) && <dl className="assistant-context-parts">
          {parts.filter(([, tokens]) => tokens).map(([name, tokens]) => <div key={name}><dt>{name}</dt><dd>{tokens!.toLocaleString()}</dd></div>)}
        </dl>}
        {share !== undefined && share >= 0.9 && <p className="assistant-menu-note">Nearly full. A new chat keeps answers sharp and cheaper.</p>}
        {model?.tiers ? <div className="assistant-context-tiers" role="radiogroup" aria-labelledby={tiersLabel}>
          <span id={tiersLabel} className="assistant-menu-label">Window size</span>
          {model.tiers.map(item => <label key={item.id}>
            <input type="radio" name={tiersLabel} checked={tier?.id === item.id} onChange={() => onTier(item.id)} />
            <span>{item.name}{item.tokens ? ` · ${tokenText(item.tokens)}` : ''}</span></label>)}
          <p className="assistant-menu-note">Applies from your next message. A longer window can use more of your allowance.</p>
        </div> : <p className="assistant-menu-note">{model?.context ? `This model’s window is fixed at ${tokenText(model.context)} tokens.` : 'This model’s window size is fixed.'}</p>}
      </Popover.Content>
    </Popover.Portal>
  </Popover.Root>
}
