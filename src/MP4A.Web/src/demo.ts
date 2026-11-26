import catalog from '../demo/catalog.json'
import type { Bridge, Command, Commands, LibraryQuery, Playback, PlayerEvent, Request, Track } from './bridge'
import type { LiveSignal } from './desktop'

const tracks = catalog.albums.flatMap(album => album.tracks)
const byId = new Map(tracks.map(track => [track.id, track]))
const albums = catalog.albums.map(({ tracks, ...album }) => ({ ...album, firstTrackId: tracks[0].id }))
const playlists = catalog.playlists.map(({ id, title, description }) => ({ id, title, description }))

function library(args: LibraryQuery) {
  const query = args.query?.trim().toLowerCase() ?? ''
  const items = args.kind === 'albums' ? albums : args.kind === 'playlists' ? playlists
    : args.playlistId ? (catalog.playlists.find(list => list.id === args.playlistId)?.trackIds ?? []).map(id => byId.get(id)!)
      : tracks.filter(track => !args.albumId || track.albumId === args.albumId)
  const filtered = items.filter(item => (
    'artist' in item ? [item.title, item.artist, 'album' in item ? item.album : item.genre] : [item.title, item.description]
  ).some(value => value.toLowerCase().includes(query)))
  return { items: filtered.slice(args.offset, args.offset + args.limit), total: filtered.length }
}

class DemoAudio {
  private context?: AudioContext
  private master?: GainNode
  private analyser?: AnalyserNode
  private waveform = new Float32Array(512)
  private spectrum = new Float32Array(256)
  private voices: OscillatorNode[] = []
  private level = 0.7

  setVolume(level: number) {
    this.level = level
    if (this.context && this.master) this.master.gain.setTargetAtTime(level * 0.075, this.context.currentTime, 0.04)
  }

  async start(track: Track) {
    this.context ??= new AudioContext()
    await this.context.resume()
    this.stop()
    const context = this.context
    if (!this.master) {
      this.master = context.createGain()
      this.analyser = context.createAnalyser()
      this.analyser.fftSize = this.waveform.length
      this.master.connect(this.analyser).connect(context.destination)
    }
    this.setVolume(this.level)
    const root = 55 * 2 ** ((tracks.findIndex(item => item.id === track.id) % 12) / 12)
    this.voices = [1, 2, 2.998, 4.001].map((ratio, index) => {
      const voice = context.createOscillator()
      const envelope = context.createGain()
      const level = 0.22 / (1 + index)
      voice.type = index === 0 ? 'triangle' : 'sine'
      voice.frequency.value = root * ratio
      envelope.gain.setValueAtTime(0, context.currentTime)
      envelope.gain.setTargetAtTime(level, context.currentTime, 0.55 + index * 0.35)
      voice.connect(envelope).connect(this.master!)
      voice.onended = () => { voice.disconnect(); envelope.disconnect() }
      voice.start()
      return voice
    })
  }

  stop() {
    this.voices.forEach(voice => voice.stop())
    this.voices = []
  }

  signal(trackId: string): LiveSignal | null {
    if (!this.analyser) return null
    this.analyser.getFloatTimeDomainData(this.waveform)
    this.analyser.getFloatFrequencyData(this.spectrum)
    const waveform = Array.from(this.waveform)
    const peak = Math.max(...waveform.map(Math.abs))
    const rms = Math.sqrt(waveform.reduce((sum, value) => sum + value * value, 0) / waveform.length)
    const meter = { peak, rms, clips: 0 }
    const channels = ['L', 'R']
    return { trackId, waveform, waveChannel: 0, source: [meter, meter], output: [meter, meter],
      sourceChannels: channels, outputChannels: channels, truePeak: null, lufs: null, limiterReductionDb: null,
      correlation: rms > 0 ? 1 : null, phase: waveform.map(mid => ({ mid, side: 0 })),
      spectrum: Array.from({ length: 96 }, (_, index) => Math.max(-90, this.spectrum[Math.floor(255 ** (index / 95))])),
    }
  }
}

export class DemoBridge implements Bridge {
  private listeners = new Set<(event: PlayerEvent) => void>()
  private audio = new DemoAudio()
  private queue: Track[] = [...catalog.albums[0].tracks]
  private index = 0
  private position = 0
  private started = 0
  private playing = false
  private volume = 0.7
  private listening = false

  constructor() {
    window.setInterval(() => {
      if (!this.playing) return
      if (this.time() >= this.queue[this.index].duration) {
        if (this.index + 1 < this.queue.length) void this.change(this.index + 1).catch(() => this.pause())
        else this.pause()
        return
      }
      this.emit({ v: 1, event: 'progress', data: { id: this.queue[this.index].id, position: this.time() } })
      if (this.listening && !document.hidden) {
        const data = this.audio.signal(this.queue[this.index].id)
        if (data) this.emit({ v: 1, event: 'signal', data })
      }
    }, 125)
  }

  private time() {
    return Math.min(this.queue[this.index].duration, this.position + (this.playing ? (performance.now() - this.started) / 1000 : 0))
  }

  private state(): Playback {
    return { track: this.queue[this.index], queue: [...this.queue], queueIndex: this.index, position: this.time(),
      playing: this.playing, volume: this.volume, liveSignal: true }
  }

  private emit(event: PlayerEvent) {
    this.listeners.forEach(listener => listener(event))
  }

  private publish() {
    this.emit({ v: 1, event: 'state', data: this.state() })
  }

  private pause() {
    this.position = this.time()
    this.playing = false
    this.audio.stop()
    this.publish()
  }

  private async change(index: number, position = 0, queue = this.queue) {
    await this.audio.start(queue[index])
    this.queue = queue
    this.index = index
    this.position = position
    this.started = performance.now()
    this.playing = true
    this.publish()
  }

  subscribe(listener: (event: PlayerEvent) => void) {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  async request<K extends Command>(method: K, args: Commands[K]['args']): Promise<Commands[K]['result']> {
    return await this.dispatch({ method, args } as Request) as Commands[K]['result']
  }

  private async dispatch({ method, args }: Request): Promise<unknown> {
    switch (method) {
      case 'state': return this.state()
      case 'library': return library(args)
      case 'lyrics': return args.id === 'violet-1' ? catalog.lyrics : []
      case 'listenSignal':
        this.listening = args.enabled
        return args.enabled && this.playing ? this.audio.signal(this.queue[this.index].id) : null
      case 'play': {
        if (!args.id) {
          if (!this.playing) await this.change(this.index, this.time() >= this.queue[this.index].duration ? 0 : this.time())
          break
        }
        const scope = args.playlistId
          ? (catalog.playlists.find(list => list.id === args.playlistId)?.trackIds ?? []).map(id => byId.get(id)!)
          : tracks.filter(track => track.albumId === (args.albumId ?? byId.get(args.id!)?.albumId))
        const index = scope.findIndex(track => track.id === args.id)
        if (index < 0) throw new Error('This track is unavailable.')
        await this.change(index, 0, scope)
        break
      }
      case 'pause': this.pause(); break
      case 'seek':
        if (this.queue[this.index].id !== args.id) throw new Error('The active track has changed.')
        this.position = Math.max(0, Math.min(this.queue[this.index].duration, args.position))
        this.started = performance.now()
        this.publish()
        break
      case 'volume':
        this.volume = Math.max(0, Math.min(1, args.value))
        this.audio.setVolume(this.volume)
        this.publish()
        break
      case 'next':
        if (this.index + 1 < this.queue.length) await this.change(this.index + 1)
        break
      case 'previous':
        if (this.time() > 3 || this.index === 0) {
          this.position = 0
          this.started = performance.now()
          this.publish()
        } else await this.change(this.index - 1)
        break
      case 'enqueue': {
        const track = byId.get(args.id)
        if (!track) throw new Error('This track is unavailable.')
        this.queue.push(track)
        this.publish()
        break
      }
      case 'remove':
      case 'jump':
        if (this.queue[args.index]?.id !== args.id) throw new Error('The queue has changed.')
        if (method === 'jump') await this.change(args.index)
        else if (args.index > this.index) {
          this.queue.splice(args.index, 1)
          this.publish()
        }
        break
      case 'theme': break
    }
    return null
  }
}
