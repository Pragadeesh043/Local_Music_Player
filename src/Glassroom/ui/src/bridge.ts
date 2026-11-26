import type { BookletAsset, BookletPage, Dsp, DspAction, DspView, LibrarySettings, LiveSignal,
  OutputConfiguration, OutputProfile, SignalReport, SmartFilter, TrackDetails } from './desktop'
export type { OutputConfiguration, OutputProfile } from './desktop'

export type Track = {
  id: string
  title: string
  artist: string
  albumId: string
  album: string
  artwork?: string
  duration: number
  number: number
  codec?: string
  rate?: number
  bits?: number
  channels?: number
  bitRate?: number
}

export type Album = {
  id: string
  title: string
  artist: string
  year: number
  genre: string
  edition?: string
  artwork?: string
  firstTrackId: string
}

export type Playlist = { id: string; title: string; description: string }
export type Lyric = { at: number; text: string }
export type DesktopAction = 'addFiles' | 'watchFolder' | 'reindex' | 'fullscreen' | 'shuffle'
export type Playback = {
  track: Track | null
  playing: boolean
  position: number
  volume: number
  queue: Track[]
  queueIndex: number
  liveSignal?: boolean
  desktop?: { status: string; volumeEnabled: boolean; catalogRevision: number; output: string; shuffled: boolean; fullscreen?: boolean
    signalPath?: { mode: string; dsp: boolean } | null }
}

export type LibraryQuery = {
  kind: 'albums' | 'tracks' | 'playlists'
  query?: string
  albumId?: string
  playlistId?: string
  folder?: string
  offset: number
  limit: number
}

export type Page = { items: (Album | Track | Playlist)[]; total: number }
export type Commands = {
  state: { args: Record<string, never>; result: Playback }
  library: { args: LibraryQuery; result: Page }
  lyrics: { args: { id: string }; result: Lyric[] }
  play: { args: { id?: string; albumId?: string; playlistId?: string; query?: string; folder?: string }; result: null }
  pause: { args: Record<string, never>; result: null }
  seek: { args: { id: string; position: number }; result: null }
  volume: { args: { value: number }; result: null }
  next: { args: Record<string, never>; result: null }
  previous: { args: Record<string, never>; result: null }
  enqueue: { args: { id: string }; result: null }
  remove: { args: { index: number; id: string }; result: null }
  jump: { args: { index: number; id: string }; result: null }
  theme: { args: { dark: boolean }; result: null }
  desktop: { args: { action: DesktopAction }; result: null }
  outputSettings: { args: Record<string, never>; result: OutputConfiguration }
  selectOutput: { args: { id: string }; result: null }
  saveOutput: { args: { id: string; profile: OutputProfile }; result: null }
  librarySettings: { args: Record<string, never>; result: LibrarySettings }
  saveLibrary: { args: { filter: SmartFilter; sort: number; descending: boolean; name: string }; result: null }
  trackDetails: { args: { id: string }; result: TrackDetails }
  rateTrack: { args: { id: string; rating: number }; result: null }
  analyzeTrack: { args: { id: string; edition: boolean }; result: null }
  booklet: { args: { id: string }; result: BookletAsset[] }
  bookletPage: { args: { id: string; index: number; page: number }; result: BookletPage }
  dsp: { args: Record<string, never>; result: DspView }
  editDsp: { args: { action: DspAction; name?: string; settings?: Dsp }; result: DspView }
  listenSignal: { args: { enabled: boolean }; result: LiveSignal | null }
  inspectChannel: { args: { channel: number }; result: null }
  signalReport: { args: Record<string, never>; result: SignalReport | null }
  hardwareMute: { args: { muted: boolean }; result: null }
}

export type Command = keyof Commands
export type Request = { [K in Command]: { v: 1; id: number; method: K; args: Commands[K]['args'] } }[Command]
export type PlayerEvent =
  | { v: 1; event: 'state'; data: Playback }
  | { v: 1; event: 'progress'; data: { id: string; position: number } }
  | { v: 1; event: 'signal'; data: LiveSignal }

export interface Bridge {
  request<K extends Command>(method: K, args: Commands[K]['args']): Promise<Commands[K]['result']>
  subscribe(listener: (event: PlayerEvent) => void): () => void
}

type Response = { v: 1; id: number; ok: boolean; result?: unknown; error?: string }
type WebView = {
  postMessage(request: Request): void
  addEventListener(type: 'message', listener: (message: MessageEvent<Response | PlayerEvent>) => void): void
}

class DesktopBridge implements Bridge {
  private nextId = 0
  private listeners = new Set<(event: PlayerEvent) => void>()
  private pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()

  constructor(private webview: WebView) {
    webview.addEventListener('message', ({ data }) => {
      if (data?.v !== 1) return
      if ('event' in data) {
        this.listeners.forEach(listener => listener(data))
        return
      }
      const pending = this.pending.get(data.id)
      if (data.ok) pending?.resolve(data.result)
      else pending?.reject(new Error(data.error ?? 'The player could not complete this action.'))
    })
  }

  request<K extends Command>(method: K, args: Commands[K]['args']): Promise<Commands[K]['result']> {
    const id = ++this.nextId
    const timeout = method === 'analyzeTrack' ? 1_800_000
      : ['library', 'play', 'seek', 'next', 'previous', 'jump', 'selectOutput', 'saveOutput', 'editDsp', 'booklet'].includes(method) ? 120_000 : 8000
    return new Promise<unknown>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id)
        reject(new Error('The desktop player did not respond.'))
      }, timeout)
      const cleanup = () => {
        window.clearTimeout(timer)
        this.pending.delete(id)
      }
      this.pending.set(id, {
        resolve: value => { cleanup(); resolve(value) },
        reject: error => { cleanup(); reject(error) },
      })
      try {
        this.webview.postMessage({ v: 1, id, method, args } as Request)
      } catch (error) {
        cleanup()
        reject(error)
      }
    }) as Promise<Commands[K]['result']>
  }

  subscribe(listener: (event: PlayerEvent) => void) {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }
}

export async function createBridge(): Promise<Bridge> {
  const webview = (window as Window & { chrome?: { webview?: WebView } }).chrome?.webview
  if (webview) return new DesktopBridge(webview)
  const { DemoBridge } = await import('./demo')
  return new DemoBridge()
}
