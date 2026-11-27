import { ArrowRight } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import type { Track } from './bridge'
import type { LiveSignal } from './desktop'
import { Plot } from './Plot'
import { Player, usePlayer, useSignal } from './player'

export type AudioTool = 'DSP' | 'Output settings' | 'Signal inspector'
export type OpenAudioTool = (tool: AudioTool) => void

export const toDb = (value: number) => 20 * Math.log10(Math.max(1e-9, value))
const peakLabel = (value: number) => value > 0 ? toDb(value).toFixed(1) : '−∞'

export function trackFormat(track?: Track | null) {
  const codec = track?.codec
  if (!codec) return ''
  return codec.startsWith('pcm_') ? 'PCM' : codec.startsWith('dsd_') ? 'DSD' : codec.toUpperCase()
}

export function trackRate(track?: Track | null) {
  return track?.rate ? `${track.rate / 1000} kHz` : ''
}

export function SignalChain({ player, openTool }: { player: Player; openTool: OpenAudioTool }) {
  const { track, desktop } = usePlayer(player)
  if (!desktop) return null
  const path = desktop.signalPath
  return <div className="signal-chain" role="group" aria-label="Signal chain">
    <span title="Source format">{[trackFormat(track), trackRate(track)].filter(Boolean).join(' ') || 'Source'}</span>
    <ArrowRight size={12} aria-hidden="true" />
    <button onClick={() => openTool('DSP')} aria-label="Open DSP settings">{path ? `DSP ${path.dsp ? 'ON' : 'OFF'}` : 'DSP'}</button>
    <ArrowRight size={12} aria-hidden="true" />
    <button onClick={() => openTool('Output settings')} title={desktop.output} aria-label="Open output settings">{path?.mode ?? 'Output'}</button>
  </div>
}

export function SignalPlots({ sample, spectrumOnly = false, phase = false }: {
  sample?: LiveSignal; spectrumOnly?: boolean; phase?: boolean
}) {
  return <div className="signal-plots">
    {!spectrumOnly && <Plot label="Waveform" points={[{ values: sample?.waveform.map(value => .5 - value * .46) ?? [] }]} />}
    <Plot label="Spectrum" bars points={[{ values: sample?.spectrum.map(value => -value / 90) ?? [] }]} />
    {phase && <Plot label="Phase scope" points={[{ values: sample?.phase?.map(point => .5 - point.mid * .46) ?? [],
      horizontal: sample?.phase?.map(point => .5 + point.side * .46) }]} />}
  </div>
}

function PeakMeter({ label, peak }: { label: string; peak?: number }) {
  const level = peak == null ? -60 : Math.max(-60, Math.min(0, toDb(peak)))
  const [held, setHeld] = useState(-60)
  const latest = useRef(-60)
  const ceiling = useRef(-60)
  const timer = useRef<number>(undefined)

  useEffect(() => {
    latest.current = level
    if (level < ceiling.current && timer.current !== undefined) return
    window.clearTimeout(timer.current)
    ceiling.current = level
    setHeld(level)
    timer.current = window.setTimeout(() => {
      timer.current = undefined
      ceiling.current = latest.current
      setHeld(latest.current)
    }, 650)
  }, [level])

  useEffect(() => () => window.clearTimeout(timer.current), [])

  return <span className="peak-meter">
    <meter min={-60} max={0} value={level} aria-label={label + ' output peak in dBFS'} />
    <span className="peak-hold" style={{ left: (held + 60) / .6 + '%', opacity: held > -60 ? 1 : 0 }} aria-hidden="true" />
  </span>
}

export function Telemetry({ player, compact = false }: { player: Player; compact?: boolean }) {
  const { desktop, playing, track, liveSignal } = usePlayer(player)
  const enabled = Boolean(desktop || liveSignal)
  const { sample, error } = useSignal(player, enabled)
  const output = sample?.output
  const channels = sample?.outputChannels
  const meters = output?.length ? output.slice(0, compact ? 2 : undefined).map((value, index) => ({
    label: channels?.[index] ?? String(index + 1), peak: value.peak,
  })) : [{ label: 'L' }, { label: 'R' }]
  return <div className={compact ? 'telemetry telemetry-compact' : 'telemetry'}>
    <SignalPlots sample={sample} phase={!compact} />
    <div className="telemetry-meters" aria-label="Output peak levels">
      {meters.map(({ label, peak }: { label: string; peak?: number }) => <div className="telemetry-meter" key={label}>
        <span>{label}</span>
        <PeakMeter key={track?.id} label={label} peak={peak} />
        <small>{peak == null ? '—' : peakLabel(peak)} <span>dBFS</span></small>
      </div>)}
    </div>
    <dl className="telemetry-readouts">
      <div><dt>LUFS-M</dt><dd>{sample?.lufs?.toFixed(1) ?? '—'}</dd></div>
      <div><dt>True Peak</dt><dd>{sample?.truePeak == null ? '—' : peakLabel(sample.truePeak) + ' dBTP'}</dd></div>
      {!compact && <div><dt>Correlation</dt><dd>{sample?.correlation?.toFixed(2) ?? '—'}</dd></div>}
    </dl>
    {!sample && <p className="signal-status" role="status">{error ?? (!enabled ? 'Live signal is unavailable' :
      playing ? 'Waiting for signal…' : track ? 'Playback paused' : 'Play a track to see live signal')}</p>}
  </div>
}

export function LyricsSpectrum({ player }: { player: Player }) {
  const { desktop, liveSignal } = usePlayer(player)
  const { sample } = useSignal(player, Boolean(desktop || liveSignal))
  return <div className="lyrics-spectrum"><SignalPlots sample={sample} spectrumOnly /></div>
}
