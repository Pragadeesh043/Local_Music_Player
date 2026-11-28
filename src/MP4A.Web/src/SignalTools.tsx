import { useEffect, useState } from 'react'
import type { Meter, SignalReport } from './desktop'
import { toDb as db } from './LiveTelemetry'
import { Plot } from './Plot'
import { Player, usePlayer, useSignal } from './player'

export function SignalTools({ player }: { player: Player }) {
  const [view, setView] = useState<'live' | 'path'>('live')
  const { desktop, track } = usePlayer(player)
  return <div className="tool-form">
    <nav className="tool-actions" aria-label="Signal views">
      <button className={view === 'live' ? 'primary-action' : 'load-more'} aria-pressed={view === 'live'} onClick={() => setView('live')}>Live meters</button>
      <button className={view === 'path' ? 'primary-action' : 'load-more'} aria-pressed={view === 'path'} onClick={() => setView('path')}>Signal path</button>
    </nav>
    {view === 'live' ? <Live key={track?.id} player={player} /> : desktop?.signalPath === null
      ? <p role="status">Play a track to inspect its signal path.</p> : <PathReport key={track?.id} player={player} />}
  </div>
}

function Live({ player }: { player: Player }) {
  const { track } = usePlayer(player)
  const [channelError, setError] = useState('')
  const { sample, error } = useSignal(player)
  if (error || !sample || !track) return <p role="status">{error || 'Play a track to see live meters.'}</p>
  const selected = Math.min(sample.waveChannel, Math.max(0, sample.source.length - 1))
  const meter = sample.source[selected]
  const overs = sample.output.reduce((sum, item) => sum + item.clips, 0)
  const waveform = sample.waveChannel === selected ? sample.waveform.map(value => .5 - value * .46) : []
  const spectrum = sample.waveChannel === selected ? sample.spectrum.map(value => -(value / 90)) : []

  return <>
    <Meters title="Source channels" labels={sample.sourceChannels} values={sample.source} />
    <label className="tool-card">Inspect channel<select value={selected} onChange={event => {
      const index = Number(event.target.value)
      setError('')
      void player.bridge.request('inspectChannel', { channel: index }).catch(failure => setError(failure.message))
    }}>{sample.sourceChannels.map((label, index) => <option key={index} value={index}>{label}</option>)}</select></label>
    <div className="tool-fields">
      <Plot label="Waveform" points={[{ values: waveform }]} />
      <Plot label="Spectrum" points={[{ values: spectrum }]} />
      {sample.phase && <Plot label="Phase scope" points={[{ values: sample.phase.map(point => .5 - point.mid * .46),
        horizontal: sample.phase.map(point => .5 + point.side * .46) }]} />}
    </div>
    <Meters title="Output channels" labels={sample.outputChannels} values={sample.output} />
    <dl className="tool-details tool-card">
      <dt>LUFS-M</dt><dd>{sample.lufs?.toFixed(1) ?? '—'}</dd>
      <dt>Channel RMS</dt><dd>{meter ? db(meter.rms).toFixed(1) + ' dBFS' : '—'}</dd>
      <dt>True peak</dt><dd>{sample.truePeak == null ? '—' : db(sample.truePeak).toFixed(1) + ' dBTP'}</dd>
      <dt>Output overs</dt><dd>{overs}</dd>
      <dt>Phase correlation</dt><dd>{sample.correlation?.toFixed(2) ?? '—'}</dd>
      <dt>Limiter reduction</dt><dd>{sample.limiterReductionDb?.toFixed(1) ?? '—'}</dd>
    </dl>
    {channelError && <p role="alert">{channelError}</p>}
  </>
}

function Meters({ title, labels, values }: { title: string; labels: string[]; values: Meter[] }) {
  return <section className="tool-meter-bank tool-card"><h3>{title}</h3>
    {values.map((meter, index) => <div className="tool-meter" key={index}>
      <span>{labels[index] ?? String(index + 1)}</span>
      <meter min={-60} max={0} value={Math.max(-60, Math.min(0, db(meter.peak)))} title={db(meter.peak).toFixed(1) + ' dBFS'}
        aria-label={title + ' ' + (labels[index] ?? index + 1)} />
      <small>{db(meter.peak).toFixed(1)} dBFS{meter.clips ? ' · ' + meter.clips + ' overs' : ''}</small>
    </div>)}
  </section>
}

function PathReport({ player }: { player: Player }) {
  const [data, setData] = useState<SignalReport | null>(null)
  const [error, setError] = useState('')
  const [muting, setMuting] = useState(false)
  useEffect(() => {
    let active = true
    let timer = 0
    async function refresh() {
      try { if (active) { const next = await player.bridge.request('signalReport', {}); if (active) setData(next) } }
      catch (failure) { if (active) setError((failure as Error).message) }
      if (active) timer = window.setTimeout(refresh, 1500)
    }
    void refresh()
    return () => { active = false; clearTimeout(timer) }
  }, [player])
  if (!data) return <p role="status">{error || 'Play a track to inspect its signal path.'}</p>
  const { report, hardware } = data
  const muted = hardware?.muted

  return <>
    <section className="tool-card"><h3>{report.verdict}</h3><p>{report.outputSummary}</p><p>{report.explanation}</p>
      {data.notice && <p>{data.notice}</p>}</section>
    {report.stages.map((stage, index) => <details key={stage.name + index}>
      <summary>{stage.name}</summary><p className="tool-section"><strong>{stage.summary}</strong></p><p>{stage.detail}</p>
    </details>)}
    <section className="tool-card"><h3>Hardware readback</h3>
      {data.asio ? <p>ASIO driver sample rate · {data.rate == null ? 'Unavailable' : data.rate.toLocaleString() + ' Hz'}</p> :
        <><p>Physical clock unavailable through this Windows endpoint.</p>
          <p>{hardware?.decibels != null
            ? hardware.decibels.toFixed(1) + ' dB · range ' + hardware.minimumDb + ' to ' + hardware.maximumDb + ' dB · step ' + hardware.stepDb + ' dB'
            : 'Hardware volume unavailable.'}</p></>}
      {muted != null && <button className="load-more" disabled={muting} onClick={() => {
        setMuting(true)
        void player.send('hardwareMute', { muted: !muted }).then(() => player.bridge.request('signalReport', {}))
          .then(setData, failure => setError(failure.message)).finally(() => setMuting(false))
      }}>{muted ? 'Unmute hardware' : 'Mute hardware'}</button>}
    </section>
    {error && <p role="alert">{error}</p>}
  </>
}
