import { Activity, ChevronDown, ChevronUp, Headphones, ListMusic, Maximize2, Pause, Play, Shuffle, SkipBack, SkipForward, SlidersHorizontal, Volume2, VolumeX, X } from 'lucide-react'
import { AnimatePresence, motion } from 'motion/react'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { CSSProperties, ReactNode } from 'react'
import { Ambient, useAmbient } from './Ambient'
import { Artwork } from './Artwork'
import type { Lyric, Track } from './bridge'
import { LyricsSpectrum, SignalChain, Telemetry, trackFormat, trackRate } from './LiveTelemetry'
import type { OpenAudioTool } from './LiveTelemetry'
import { Player, timeLabel, usePlayer, useResource } from './player'

export const spring = { type: 'spring', stiffness: 350, damping: 32, mass: 0.85 } as const
export type Notify = (operation: Promise<unknown>) => void

export function IconButton({ label, children, action, active, disabled = false }: {
  label: string
  children: ReactNode
  action: () => void
  active?: boolean
  disabled?: boolean
}) {
  return <motion.button type="button" className="icon-button" onClick={action} disabled={disabled}
    title={label} aria-label={label} aria-pressed={active} whileTap={{ scale: 0.89 }} transition={spring}>
    {children}
  </motion.button>
}

export function Dock({ player, notify, expand, active, queueVisible, toggleQueue, openTool }: {
  player: Player
  notify: Notify
  expand: () => void
  active: boolean
  queueVisible?: boolean
  toggleQueue: () => void
  openTool: OpenAudioTool
}) {
  const state = usePlayer(player)
  const format = trackFormat(state.track)
  const hiRes = (state.track?.rate ?? 0) > 48000 || (state.track?.bits ?? 0) > 16
  return <section className="dock glass" aria-label="Playback controls">
    <div className="dock-transport">
      {state.desktop && <IconButton label="Shuffle" active={state.desktop.shuffled}
        action={() => notify(player.send('desktop', { action: 'shuffle' }))}><Shuffle size={17} /></IconButton>}
      <Transport player={player} notify={notify} />
    </div>
    <button className="dock-track" onClick={() => expand()} disabled={!state.track} aria-label="Expand Now Playing">
      <motion.span className="dock-art" layoutId="player-cover" transition={spring}><Artwork src={state.track?.artwork} preview={state.track?.artworkPreview} loading="eager" /></motion.span>
      <span className="dock-track-text"><span className="dock-title"><strong>{state.track?.title ?? 'Nothing playing'}</strong>
        {format && <span className="format-badge" title={[format, state.track?.bits ? state.track.bits + '-bit' : '', trackRate(state.track)].filter(Boolean).join(' · ')}>
          {hiRes ? 'HI-RES · ' : ''}{format}</span>}</span><small>{state.track?.artist ?? 'Choose a song to begin'}</small></span>
    </button>
    <Scrubber key={state.track?.id} player={player} notify={notify} active={active} />
    <div className="dock-actions">
      <Volume player={player} notify={notify} />
      {state.desktop && <div className="dock-tools" role="group" aria-label="Audio tools">
        <IconButton label="DSP" action={() => openTool('DSP')}><SlidersHorizontal size={17} /></IconButton>
        <IconButton label="Output device" action={() => openTool('Output settings')}><Headphones size={18} /></IconButton>
        <IconButton label="Signal inspector" action={() => openTool('Signal inspector')}><Activity size={18} /></IconButton>
      </div>}
      <IconButton label={queueVisible ? 'Hide queue' : 'Open queue'} active={queueVisible}
        disabled={queueVisible === undefined && !state.track && !state.queue.length} action={toggleQueue}><ListMusic size={19} /></IconButton>
      <IconButton label="Expand player" disabled={!state.track} action={() => expand()}><ChevronUp size={20} /></IconButton>
    </div>
  </section>
}

function Transport({ player, notify }: { player: Player; notify: Notify }) {
  const { playing, pausing, track, queueIndex, queue, desktop } = usePlayer(player)
  const canPause = playing && !pausing
  return <div className="transport">
    <IconButton label="Previous or restart" disabled={!track} action={() => notify(player.send('previous', {}))}><SkipBack size={17} fill="currentColor" /></IconButton>
    <motion.button className="play-button" title={canPause ? 'Pause' : 'Play'} disabled={!track}
      aria-label={canPause ? 'Pause' : 'Play'} whileTap={{ scale: 0.9 }} transition={spring}
      onClick={() => notify(player.togglePlayback())}>
      {canPause ? <Pause size={18} fill="currentColor" /> : <Play size={18} fill="currentColor" />}
    </motion.button>
    <IconButton label="Next track" disabled={queueIndex >= queue.length - 1 && !(desktop && track)} action={() => notify(player.send('next', {}))}><SkipForward size={17} fill="currentColor" /></IconButton>
  </div>
}

function Scrubber({ player, notify, active = true }: { player: Player; notify: Notify; active?: boolean }) {
  const { track, playing, position } = usePlayer(player)
  const duration = track?.duration ?? 0
  const range = useRef<HTMLInputElement>(null)
  const fill = useRef<HTMLSpanElement>(null)
  const elapsed = useRef<HTMLTimeElement>(null)
  const dragging = useRef(false)
  const lastSecond = useRef(-1)
  const pending = useRef<Promise<null> | null>(null)

  const paint = useCallback((time: number) => {
    const input = range.current
    if (!input) return
    if (!dragging.current && Math.abs(input.valueAsNumber - time) >= 0.025) input.value = String(time)
    if (fill.current) fill.current.style.transform = `scaleX(${duration ? time / duration : 0})`
    const second = Math.floor(time)
    if (lastSecond.current !== second && elapsed.current) {
      lastSecond.current = second
      elapsed.current.textContent = timeLabel(time)
      input.setAttribute('aria-valuetext', timeLabel(time) + ' of ' + timeLabel(duration))
    }
  }, [duration])

  useEffect(() => {
    if (!active) return
    let frame = 0
    const update = () => {
      if (document.hidden) return
      if (!dragging.current && pending.current === null) paint(player.time())
      if (playing) frame = requestAnimationFrame(update)
    }
    const restart = () => { cancelAnimationFrame(frame); update() }
    restart()
    document.addEventListener('visibilitychange', restart)
    return () => {
      cancelAnimationFrame(frame)
      document.removeEventListener('visibilitychange', restart)
    }
  }, [active, player, playing, position, paint])

  function commit(value: number) {
    if (!track) return
    const operation = player.seek(track.id, value)
    pending.current = operation
    paint(value)
    notify(operation.finally(() => {
      if (pending.current !== operation) return
      pending.current = null
      if (!dragging.current) paint(player.time())
    }))
  }

  return <div className="scrubber">
    <time ref={elapsed}>{timeLabel(position)}</time>
    <div className="scrubber-slider">
      <span className="scrubber-rail" aria-hidden="true">
        <span ref={fill} className="scrubber-fill" data-animating={active && playing} />
      </span>
      <input ref={range} className="range" type="range" aria-label="Playback position" disabled={!duration}
        min={0} max={duration || 1} step={0.05} defaultValue={position}
        onChange={event => {
          const value = event.currentTarget.valueAsNumber
          if (dragging.current) paint(value)
          else commit(value)
        }}
        onPointerDown={event => {
          dragging.current = true
          event.currentTarget.setPointerCapture(event.pointerId)
        }}
        onPointerUp={event => {
          if (!dragging.current) return
          dragging.current = false
          commit(event.currentTarget.valueAsNumber)
        }}
        onLostPointerCapture={() => {
          if (!dragging.current) return
          dragging.current = false
          paint(player.time())
        }}
        onPointerCancel={() => { dragging.current = false; paint(player.time()) }} />
    </div>
    <time>{timeLabel(duration)}</time>
  </div>
}

function Volume({ player, notify }: { player: Player; notify: Notify }) {
  const { volume, desktop } = usePlayer(player)
  const [preview, setPreview] = useState<number | null>(null)
  const dragging = useRef(false)
  const value = preview ?? volume

  const commit = (value: number) => {
    setPreview(value)
    notify(player.send('volume', { value }).finally(() => setPreview(current => current === value ? null : current)))
  }

  return <div className="volume">
    <IconButton label={desktop?.volumeEnabled === false ? 'Fixed or external volume' : value ? 'Mute' : 'Unmute'}
      disabled={desktop?.volumeEnabled === false} action={() => notify(player.toggleMute())}>
      {value ? <Volume2 size={17} /> : <VolumeX size={17} />}</IconButton>
    <input className="range" type="range" min={0} max={1} step={0.01} value={value} aria-label="Volume"
      disabled={desktop?.volumeEnabled === false}
      aria-valuetext={Math.round(value * 100) + '%'} style={{ '--fill': value * 100 + '%' } as CSSProperties}
      onPointerDown={event => { dragging.current = true; event.currentTarget.setPointerCapture(event.pointerId) }}
      onChange={event => {
        const value = event.currentTarget.valueAsNumber
        if (dragging.current) setPreview(value)
        else commit(value)
      }}
      onPointerUp={event => {
        if (!dragging.current) return
        dragging.current = false
        commit(event.currentTarget.valueAsNumber)
      }}
      onLostPointerCapture={() => { if (dragging.current) { dragging.current = false; setPreview(null) } }}
      onPointerCancel={() => { dragging.current = false; setPreview(null) }} />
  </div>
}

export function NowPlaying({ player, ambient, notify, close, queueOpen, setQueueOpen, inspect, openTool }: {
  player: Player
  ambient: ReturnType<typeof useAmbient>
  notify: Notify
  close: () => void
  queueOpen: boolean
  setQueueOpen: (open: boolean) => void
  inspect?: () => void
  openTool: OpenAudioTool
}) {
  const { track } = usePlayer(player)
  const panel = useRef<HTMLElement>(null)
  const queueToggle = useRef<HTMLDivElement>(null)
  const queuePanel = useRef<HTMLElement>(null)

  useEffect(() => { panel.current?.focus() }, [])
  useEffect(() => {
    if (queueOpen) queuePanel.current?.focus()
  }, [queueOpen])

  const closeQueue = () => {
    setQueueOpen(false)
    queueToggle.current?.querySelector('button')?.focus()
  }

  return <motion.section ref={panel} className="full-player" role="dialog" aria-modal="true" aria-label="Now Playing" tabIndex={-1}
    initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: 0.25 }}
    onKeyDown={event => {
      if (event.defaultPrevented || event.nativeEvent.isComposing) return
      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        if (queueOpen) closeQueue()
        else close()
      }
      if (event.key !== 'Tab') return
      const nodes = [...(panel.current?.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), [tabindex="0"]') ?? [])]
        .filter(node => node.tabIndex >= 0 && !node.closest('[inert]') && node.getClientRects().length > 0)
      const first = nodes[0]
      const last = nodes.at(-1)
      if (document.activeElement === panel.current) {
        event.preventDefault()
        const target = event.shiftKey ? last : first
        target?.focus()
      } else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
    }}>
    <Ambient layer={ambient} />
    <header className="full-header">
      <div className="full-brand"><IconButton label="Collapse Now Playing" action={close}><ChevronDown size={23} /></IconButton><strong>MP4A</strong></div>
      <span>Now Playing</span>
      <div className="tool-actions" ref={queueToggle}>
        <IconButton label="Toggle queue" active={queueOpen} action={() => queueOpen ? closeQueue() : setQueueOpen(true)}><ListMusic size={22} /></IconButton>
        {inspect && <IconButton label="Signal inspector" action={() => openTool('Signal inspector')}><Activity size={20} /></IconButton>}
      </div>
    </header>
    <div className="full-content">
      <div className="full-transport">
        <motion.div className="full-art" layoutId="player-cover" transition={spring}><Artwork src={track?.artwork} preview={track?.artworkPreview} loading="eager" /></motion.div>
        <div className="full-title"><h1>{track?.title ?? 'Nothing playing'}</h1><p>{track?.artist}</p></div>
        <Scrubber key={track?.id} player={player} notify={notify} />
        <Transport player={player} notify={notify} />
        <Volume player={player} notify={notify} />
        <SignalChain player={player} openTool={openTool} />
      </div>
      <PlayerTabs key={track?.id} player={player} track={track} notify={notify} inspect={inspect} />
    </div>
    <AnimatePresence>
      {queueOpen && <Queue player={player} notify={notify} close={closeQueue} panel={queuePanel} />}
    </AnimatePresence>
  </motion.section>
}

const playerTabs = ['Lyrics', 'Live Signal', 'Track Info'] as const

function PlayerTabs({ player, track, notify, inspect }: { player: Player; track: Track | null; notify: Notify; inspect?: () => void }) {
  const id = track?.id
  const load = useCallback(() => id ? player.bridge.request('lyrics', { id }) : Promise.resolve([]), [id, player])
  const { data, error } = useResource(load)
  const [selection, setSelection] = useState<number>()
  const current = selection ?? (error || data?.length === 0 ? 1 : 0)
  const tabs = useRef<HTMLDivElement>(null)
  return <section className="player-tabs">
    <div className="player-tab-list" role="tablist" aria-label="Now Playing views" ref={tabs} onKeyDown={event => {
      const length = playerTabs.length
      const index = { ArrowLeft: (current - 1 + length) % length, ArrowRight: (current + 1) % length, Home: 0, End: length - 1 }[event.key]
      if (index === undefined) return
      event.preventDefault()
      setSelection(index)
      tabs.current?.querySelectorAll('button')[index]?.focus()
    }}>
      {playerTabs.map((label, index) => <button key={label} id={`player-tab-${index}`} type="button" role="tab"
        aria-selected={current === index} aria-controls="player-tab-panel" tabIndex={current === index ? 0 : -1}
        onClick={() => setSelection(index)}>{label}</button>)}
    </div>
    <div className="player-tab-panel" id="player-tab-panel" role="tabpanel" aria-labelledby={`player-tab-${current}`} tabIndex={0}>
      {current === 0 && <>
        <Lyrics key={id} player={player} id={id} notify={notify} data={data} error={error} />
        <LyricsSpectrum player={player} />
      </>}
      {current === 1 && <Telemetry player={player} />}
      {current === 2 && <TrackInfo track={track} inspect={inspect} />}
    </div>
  </section>
}

function TrackInfo({ track, inspect }: { track: Track | null; inspect?: () => void }) {
  if (!track) return <p className="lyrics-empty">No track selected.</p>
  const rows = [
    ['Title', track.title], ['Artist', track.artist], ['Album', track.album], ['Duration', timeLabel(track.duration)],
    ['Format', trackFormat(track)], ['Sample rate', trackRate(track)], ['Bit depth', track.bits ? track.bits + ' bit' : ''],
    ['Channels', track.channels || ''], ['Bitrate', track.bitRate ? Math.round(track.bitRate / 1000) + ' kbps' : ''],
  ] as const
  return <div className="player-track-info">
    <dl className="tool-details">{rows.filter(([, value]) => value !== '').map(([label, value]) =>
      <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    {inspect && <button className="load-more" onClick={inspect}>Open file details</button>}
  </div>
}

function Lyrics({ player, id, notify, data, error }: {
  player: Player; id?: string; notify: Notify; data?: Lyric[]; error?: string
}) {
  const [active, setActive] = useState(-1)
  const scroller = useRef<HTMLDivElement>(null)
  const activeLine = useRef<HTMLButtonElement>(null)
  const { position, playing } = usePlayer(player)

  useEffect(() => {
    if (!data?.length) return
    const update = () => {
      const current = player.time()
      let low = 0
      let high = data.length
      while (low < high) {
        const middle = Math.floor((low + high) / 2)
        if (data[middle].at <= current) low = middle + 1
        else high = middle
      }
      setActive(low - 1)
    }
    update()
    if (!playing) return
    const interval = window.setInterval(() => { if (!document.hidden) update() }, 120)
    return () => window.clearInterval(interval)
  }, [data, player, playing, position])

  useEffect(() => {
    const line = activeLine.current
    const list = scroller.current
    if (!line || !list) return
    const top = line.offsetTop + line.offsetHeight / 2 - list.clientHeight * 0.38
    list.scrollTo({ top, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' })
  }, [active, data])

  function seek(line: Lyric) {
    if (id) notify(player.seek(id, line.at))
  }

  return <section className="lyrics" aria-label="Synced lyrics">
    <div ref={scroller} className="lyrics-scroll">
      {error || !data?.length ? <p className="lyrics-empty">{error ?? (data ? 'Synced lyrics are unavailable for this track.' : 'Loading lyrics…')}</p>
        : data.map((line, index) => <button key={index} ref={index === active ? activeLine : undefined}
          className="lyric-line" aria-current={index === active ? 'true' : undefined} onClick={() => seek(line)}>{line.text}</button>)}
    </div>
  </section>
}

export function QueueRail({ player, notify, openTool }: { player: Player; notify: Notify; openTool: OpenAudioTool }) {
  const { desktop } = usePlayer(player)
  const panel = useRef<HTMLElement>(null)
  return <aside className="queue-rail" ref={panel} tabIndex={-1} aria-label="Live telemetry and queue">
    <section className="rail-signal">
      <header><h2>Live Signal &amp; Path</h2>
        {desktop && <IconButton label="Expand Signal Inspector" action={() => openTool('Signal inspector')}><Maximize2 size={15} /></IconButton>}
      </header>
      <Telemetry player={player} compact />
      <SignalChain player={player} openTool={openTool} />
    </section>
    <h2>Up Next</h2>
    <div className="queue-scroll">
      <QueueTracks player={player} notify={notify} panel={panel} />
    </div>
  </aside>
}

function Queue({ player, notify, close, panel }: {
  player: Player
  notify: Notify
  close: () => void
  panel: React.RefObject<HTMLElement | null>
}) {
  const { track, desktop } = usePlayer(player)
  return <motion.aside ref={panel} tabIndex={-1} className="queue glass" aria-label="Playback queue"
    initial={{ x: '105%', opacity: 0 }} animate={{ x: 0, opacity: 1 }}
    exit={{ x: '105%', opacity: 0 }} transition={spring}>
    <header><h2>Queue</h2><IconButton label="Close queue" action={close}><X size={20} /></IconButton></header>
    <div className="queue-scroll">
      {track && <><h3>Now playing</h3><div className="queue-current"><Artwork src={track.artwork} preview={track.artworkPreview} /><span><strong>{track.title}</strong><small>{track.artist}</small></span></div></>}
      <h3>{desktop ? 'Manually queued' : 'Up next'}</h3>
      <QueueTracks player={player} notify={notify} panel={panel} />
    </div>
  </motion.aside>
}

function QueueTracks({ player, notify, panel }: { player: Player; notify: Notify; panel: React.RefObject<HTMLElement | null> }) {
  const { queue, queueIndex, desktop } = usePlayer(player)
  const upcoming = queue.slice(queueIndex + 1)
  function updateQueue(method: 'jump' | 'remove', index: number, id: string) {
    // The focused row can disappear when the engine publishes its next snapshot.
    panel.current?.focus()
    notify(player.send(method, { index, id }))
  }
  if (!upcoming.length) return <p className="lyrics-empty">{desktop
    ? 'Add tracks to play ahead of your library order.' : 'The queue is empty.'}</p>
  return upcoming.map((track, offset) => {
    const index = queueIndex + offset + 1
    return <div className="queue-row" key={track.id + ':' + index}>
      <button className="track-link" aria-label={'Play ' + track.title + ' from queue'} onClick={() => updateQueue('jump', index, track.id)}>
        <Artwork src={track.artwork} preview={track.artworkPreview} />
        <span><strong title={track.title}>{track.title}</strong><small>{track.artist}</small></span>
      </button>
      <time>{timeLabel(track.duration)}</time>
      <IconButton label={'Remove ' + track.title + ' from queue'} action={() => updateQueue('remove', index, track.id)}><X size={15} /></IconButton>
    </div>
  })
}
