import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import type { OutputConfiguration, OutputProfile } from './bridge'
import type { Player } from './player'

const routes = ['Binaural headphones', 'Stereo downmix', '7.1 device / HeSuVi', 'Exclusive · direct PCM',
  'Exclusive · DSP / resampling', 'Exclusive · DSD over PCM', 'ASIO · native DSD']

export function OutputSettings({ player }: { player: Player }) {
  const [data, setData] = useState<OutputConfiguration>()
  const [draft, setDraft] = useState<OutputProfile>()
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')

  function load() {
    return player.bridge.request('outputSettings', {}).then(next => { setData(next); setDraft(next.profile) })
  }

  useEffect(() => {
    let active = true
    void player.bridge.request('outputSettings', {}).then(next => {
      if (active) { setData(next); setDraft(next.profile) }
    }, error => { if (active) setMessage(error.message) })
    return () => { active = false }
  }, [player])

  function apply(operation?: () => Promise<unknown>, success = '') {
    setBusy(true)
    setMessage('')
    const refresh = operation ? operation().then(load) : load()
    void refresh.then(() => setMessage(success), error => setMessage(error.message))
      .finally(() => setBusy(false))
  }

  if (!data || !draft) return <p role="status">{message || 'Reading audio outputs…'}</p>
  const active = data.devices.find(device => device.id === data.selected)

  function update<K extends keyof OutputProfile>(key: K, value: OutputProfile[K]) {
    setDraft(current => current && { ...current, [key]: value })
  }

  function save(event: FormEvent) {
    event.preventDefault()
    if (data?.selected && draft) apply(() => player.send('saveOutput', { id: data.selected!, profile: draft }), 'Output profile saved.')
  }

  return <form className="tool-form" onSubmit={save}>
    <label className="tool-card">Audio device
      <select value={data.followDefault ? '' : data.selected ?? ''} disabled={busy || !data.devices.length}
        onChange={event => { const id = event.target.value; apply(() => player.send('selectOutput', { id })) }}>
        <option value="">Follow Windows default</option>
        {data.devices.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}
      </select>
    </label>
    {!active && <p role="status">No audio device is available.</p>}
    <fieldset disabled={busy || !active} className="tool-form">
      <section className="tool-card">
        <h3>Routing and volume</h3>
        <div className="tool-fields">
          <label>Output mode<select value={draft.route} onChange={event => update('route', Number(event.target.value))}>
            {routes.map((route, index) => <option key={route} value={index}>{route}</option>)}
          </select></label>
          <label>Volume mode<select value={draft.volumeMode} onChange={event => update('volumeMode', Number(event.target.value))}>
            <option value={0}>Digital volume</option><option value={1}>Fixed line-out</option>
            {active?.hardwareVolume && <option value={2}>DAC hardware volume</option>}
          </select></label>
          <label>Native DSD ASIO driver<select value={draft.asioDriver ?? ''} onChange={event => update('asioDriver', event.target.value || null)}>
            <option value="">Choose a driver</option>{data.drivers.map(driver => <option key={driver}>{driver}</option>)}
          </select></label>
          <label className="tool-check"><input type="checkbox" checked={draft.dopConfirmed} onChange={event => update('dopConfirmed', event.target.checked)} /> Confirm this DAC supports DoP</label>
        </div>
      </section>
      <section className="tool-card">
        <h3>PCM processing</h3>
        <div className="tool-fields">
          <label>Exclusive DSP sample rate<select value={draft.targetRate} onChange={event => update('targetRate', Number(event.target.value))}>
            {data.rates.map(rate => <option value={rate} key={rate}>{rate ? rate.toLocaleString() + ' Hz' : 'Follow track'}</option>)}
          </select></label>
          <label>Exclusive DSP PCM depth<select value={draft.outputBits} onChange={event => update('outputBits', Number(event.target.value))}>
            {[16, 24, 32].map(bits => <option value={bits} key={bits}>{bits} bit</option>)}
          </select></label>
          <label>Resampler phase<select value={Number(draft.minimumPhase)} onChange={event => update('minimumPhase', event.target.value === '1')}>
            <option value={0}>Linear</option><option value={1}>Minimum</option>
          </select></label>
          <label className="tool-check"><input type="checkbox" checked={draft.sharper} onChange={event => update('sharper', event.target.checked)} /> Steeper resampling filter</label>
          <label>PCM pause and resume fade · {draft.pauseFadeMilliseconds} ms
            <input type="range" min={250} max={600} step={50} value={draft.pauseFadeMilliseconds}
              onChange={event => update('pauseFadeMilliseconds', Number(event.target.value))} />
          </label>
        </div>
      </section>
      <section className="tool-card">
        <h3>Memory playback</h3>
        <div className="tool-fields">
          <label className="tool-check"><input type="checkbox" checked={draft.memory} onChange={event => update('memory', event.target.checked)} /> Decode tracks to RAM</label>
          <label>Total RAM playback limit (MiB)<input type="number" min={data.minimumMemory} max={data.maximumMemory} step={1} required
            disabled={!draft.memory} value={draft.memoryLimitMiB} onChange={event => update('memoryLimitMiB', Number(event.target.value))} /></label>
        </div>
      </section>
    </fieldset>
    <div className="tool-actions"><button className="primary-action" type="submit" disabled={!active || busy}>Save for this device</button>
      <button type="button" className="load-more" disabled={busy} onClick={() => apply()}>Refresh</button>
      <span role="status">{busy ? 'Applying…' : message}</span>
    </div>
  </form>
}
