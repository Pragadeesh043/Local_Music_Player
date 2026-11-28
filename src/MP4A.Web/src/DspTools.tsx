import { Plus, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { Plot } from './Plot'
import type { Dsp, DspAction, DspView, EqBand } from './desktop'
import { Player, useResource } from './player'

const shapes = ['Peak', 'Low shelf', 'High shelf', 'Low pass', 'High pass', 'Notch', 'Band pass', 'All pass']
const centers = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]
const levels = [
  ['Preamp (dB)', 'preamp', -24, 6, .5], ['Bass shelf (dB)', 'bass', -12, 12, .5],
  ['Stereo width (%)', 'width', 0, 200, 1], ['Crossfeed (%)', 'crossfeed', 0, 100, 1],
  ['Balance', 'balance', -1, 1, .01], ['Headroom (dB)', 'headroom', 0, 12, .5],
] as const
const modes = [
  ['Normalization', 'normalization', ['Off', 'ReplayGain track', 'ReplayGain album', 'R128 track', 'R128 album']],
  ['Dither · exclusive DSP', 'dither', ['Off', 'TPDF', 'Noise-shaped']],
] as const

export function DspTools({ player }: { player: Player }) {
  const load = useCallback(() => player.bridge.request('dsp', {}), [player])
  const { data, error } = useResource(load)
  return data ? <DspForm initial={data} player={player} /> : <p role="status">{error ?? 'Reading DSP settings…'}</p>
}

function DspForm({ initial, player }: { initial: DspView; player: Player }) {
  const [view, setView] = useState(initial)
  const [draft, setDraft] = useState(initial.settings)
  const [name, setName] = useState('')
  const [preset, setPreset] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [preview, setPreview] = useState<{ draft: Dsp; response: DspView }>()
  const modified = JSON.stringify(draft) !== JSON.stringify(view.settings)
  const selectedCurve = view.curvePresets?.find(curve => curve.id === draft.graphicPreset ||
    (curve.isFlat && draft.graphic.every(gain => gain === 0)))
  const response = modified && preview?.draft === draft ? preview.response : view

  useEffect(() => {
    if (!modified || busy) return
    let current = true
    const timer = window.setTimeout(() => {
      void player.bridge.request('editDsp', { action: 'preview', settings: draft }).then(result => {
        if (current) setPreview({ draft, response: result })
      }, () => {})
    }, 90)
    return () => { current = false; clearTimeout(timer) }
  }, [draft, modified, busy, player])

  function update<K extends keyof Dsp>(key: K, value: Dsp[K]) {
    setDraft(current => ({ ...current, [key]: value }))
    setMessage('')
  }

  function editBand(index: number, change: Partial<EqBand>) {
    update('parametric', draft.parametric.map((band, i) => i === index ? { ...band, ...change } : band))
  }

  function run(action: DspAction) {
    setBusy(true)
    setMessage('')
    void (async () => {
      if (modified && (action === 'save' || action === 'impulse' || action === 'clearImpulse')) {
        const applied = await player.send('editDsp', { action: 'apply', settings: draft })
        setView(applied)
      }
      const result = await player.send('editDsp', { action, settings: action === 'apply' ? draft : undefined,
        name: action === 'load' ? preset : name })
      setView(result)
      setDraft(result.settings)
      if (action === 'apply') setMessage(result.bypassed
        ? 'DSP settings applied. The current output route bypasses DSP.'
        : 'DSP settings applied.')
      if (action === 'save') setMessage('DSP preset saved.')
    })().catch(error => setMessage(error.message)).finally(() => setBusy(false))
  }

  return <form className="tool-form" onSubmit={event => {
    event.preventDefault()
    if (modified && !busy) run('apply')
  }} onInvalid={event => {
    event.preventDefault()
    const input = event.target as HTMLInputElement
    setMessage(`${input.getAttribute('aria-label')}: ${input.validationMessage}`)
  }}>
    <Plot label="Left and right EQ response" points={[
      { values: response.right.map(value => (36 - Math.max(-36, Math.min(36, value))) / 72), secondary: true },
      { values: response.left.map(value => (36 - Math.max(-36, Math.min(36, value))) / 72) },
    ]} />
    <fieldset className="tool-form" disabled={busy}>
      <section className="tool-card">
        <h3>Processing controls</h3>
        <div className="tool-fields">
          {([['Enable DSP', 'enabled'], ['Invert polarity (180°)', 'invert'], ['Enable stereo peak limiter', 'limiter']] as const)
            .map(([label, key]) => <label key={key} className="tool-check"><input type="checkbox" checked={draft[key]}
              onChange={event => update(key, event.target.checked)} /> {label}</label>)}
          {levels.map(([label, key, min, max, step]) => <label key={key}>{label} · {draft[key].toFixed(key === 'balance' ? 2 : 1)}
            <input type="range" min={min} max={max} step={step} value={draft[key]} onChange={event => update(key, Number(event.target.value))} />
          </label>)}
          {modes.map(([label, key, options]) => <label key={key}>{label}<select value={draft[key]} onChange={event => update(key, Number(event.target.value))}>
            {options.map((option, index) => <option key={option} value={index}>{option}</option>)}
          </select></label>)}
        </div>
      </section>
      <details><summary>Parametric EQ</summary>
        <div className="tool-form tool-section">
          {draft.parametric.map((band, i) => <div className="eq-band" key={i}>
            <label className="tool-check"><input type="checkbox" checked={band.active} onChange={event => editBand(i, { active: event.target.checked })} /> Band {i + 1}</label>
            <label>Shape<select value={band.shape} onChange={event => editBand(i, { shape: Number(event.target.value) })}>
              {shapes.map((shape, index) => <option value={index} key={shape}>{shape}</option>)}</select></label>
            <label>Channel<select value={band.channel} onChange={event => editBand(i, { channel: Number(event.target.value) })}>
              {['Both', 'Left', 'Right'].map((channel, index) => <option value={index} key={channel}>{channel}</option>)}</select></label>
            {([['Hz', 'frequency', 20, 22000], ['dB', 'gain', -30, 24], ['Q', 'q', .05, 30]] as const)
              .map(([label, key, min, max]) => <label key={key}>{label}<input aria-label={'Band ' + (i + 1) + ' ' + label}
                type="number" min={min} max={max} step="any" value={band[key]} onChange={event => editBand(i, { [key]: Number(event.target.value) })} /></label>)}
            <button type="button" className="icon-button" aria-label={'Remove band ' + (i + 1)}
              onClick={() => update('parametric', draft.parametric.filter((_, index) => index !== i))}><Trash2 size={16} /></button>
          </div>)}
          <div className="tool-actions">
            <button type="button" className="load-more" disabled={draft.parametric.length >= 64}
              onClick={() => update('parametric', [...draft.parametric, { frequency: 1000, gain: 0, q: 1, shape: 0, channel: 0, active: true }])}>
              <Plus size={15} /> Add band</button>
            <button type="button" className="load-more" onClick={() => run('autoeq')}>Import AutoEQ</button>
          </div>
        </div>
      </details>
      <details><summary>10-band graphic EQ</summary><div className="tool-fields tool-section">
        <label>Curve preset<select value={selectedCurve?.id ?? ''} onChange={event => {
          const curve = view.curvePresets?.find(curve => curve.id === event.target.value)
          if (!curve) return
          setDraft(current => ({ ...current, enabled: curve.isFlat ? current.enabled : true,
            graphic: [...curve.gains], graphicPreset: curve.isFlat ? null : curve.id }))
          setMessage('')
        }}>
          <option value="" disabled>Custom</option>
          {view.curvePresets?.map(curve => <option key={curve.id} value={curve.id}>{curve.label}</option>)}
        </select></label>
        {selectedCurve && !selectedCurve.isFlat && <small>Preset trim {selectedCurve.trimDb.toFixed(1)} dB · Peak protection
          {selectedCurve.telephone && ' · 300 Hz–3.4 kHz bandpass'}</small>}
        {centers.map((frequency, i) => <label key={frequency}>{frequency} Hz · {draft.graphic[i].toFixed(1)} dB
          <input type="range" min={-12} max={12} step={.5} value={draft.graphic[i]} onChange={event => {
            setDraft({ ...draft, graphicPreset: null,
              graphic: draft.graphic.map((value, index) => i === index ? Number(event.target.value) : value) })
            setMessage('')
          }} /></label>)}
      </div></details>
      <section className="tool-card">
        <h3>FIR convolution</h3>
        <div className="tool-actions">
          <button type="button" className="load-more" onClick={() => run('impulse')}>Load FIR WAV</button>
          <button type="button" className="load-more" disabled={!view.impulse} onClick={() => run('clearImpulse')}>Clear FIR</button>
          {view.impulse && <span>{view.impulse}</span>}
        </div>
      </section>
      <div className="tool-fields">
        <label>DSP preset<select value={preset} onChange={event => setPreset(event.target.value)}>
          <option value="">Choose a preset</option>{view.presets.map(title => <option key={title}>{title}</option>)}
        </select></label>
        <label>Save preset as<input value={name} maxLength={100} onChange={event => setName(event.target.value)} /></label>
      </div>
      <div className="tool-actions">
        <button type="submit" className="primary-action" disabled={!modified}>Apply DSP</button>
        <button type="button" className="load-more" disabled={!preset} onClick={() => run('load')}>Load preset</button>
        <button type="button" className="load-more" disabled={!name.trim()} onClick={() => run('save')}>Save preset</button>
        <button type="button" className="load-more" onClick={() => run('reset')}>Reset</button>
        <span role="status">{busy ? 'Updating DSP…' : message}</span>
      </div>
    </fieldset>
  </form>
}
