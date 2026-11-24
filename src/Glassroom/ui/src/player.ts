import { useEffect, useState, useSyncExternalStore } from 'react'
import type { Bridge, Command, Commands, Playback, Track } from './bridge'

const initial: Playback = { track: null, playing: false, position: 0, volume: 1, queue: [], queueIndex: -1 }

export class Player {
  private state = initial
  private recent: Track[] = []
  private listeners = new Set<() => void>()
  private clock = { position: 0, at: performance.now() }
  private operations = Promise.resolve()

  constructor(public bridge: Bridge) {
    let receivedState = false
    bridge.subscribe(event => {
      if (event.event === 'state') {
        receivedState = true
        this.update(event.data)
      } else if (event.event === 'progress' && event.data.id === this.state.track?.id) {
        this.clock = { position: event.data.position, at: performance.now() }
      }
    })
    this.ready = bridge.request('state', {}).then(state => {
      if (!receivedState) this.update(state)
    })
  }

  ready: Promise<void>

  private update(state: Playback) {
    const track = state.track
    if (state.playing && track && this.recent[0]?.id !== track.id) {
      this.recent = [track, ...this.recent.filter(item => item.id !== track.id)].slice(0, 6)
    }
    this.state = state
    this.clock = { position: state.position, at: performance.now() }
    this.listeners.forEach(listener => listener())
  }

  snapshot = () => this.state
  recentTracks = () => this.recent
  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  time = () => Math.max(0, Math.min(this.state.track?.duration ?? 0,
    this.clock.position + (this.state.playing ? (performance.now() - this.clock.at) / 1000 : 0)))

  send<K extends Command>(method: K, args: Commands[K]['args']) {
    const operation = this.operations.then(() => this.bridge.request(method, args))
    this.operations = operation.then(() => undefined, () => undefined)
    return operation
  }
}

export const usePlayer = (player: Player) => useSyncExternalStore(player.subscribe, player.snapshot)

export function useResource<T>(load: () => Promise<T>) {
  const [resource, setResource] = useState<{ data?: T; error?: string }>({})
  useEffect(() => {
    let active = true
    setResource({})
    load().then(data => {
      if (active) setResource({ data })
    }, (error: Error) => {
      if (active) setResource({ error: error.message })
    })
    return () => { active = false }
  }, [load])
  return resource
}

export function timeLabel(seconds: number) {
  const total = Math.floor(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const remainder = String(total % 60).padStart(2, '0')
  return hours ? hours + ':' + String(minutes).padStart(2, '0') + ':' + remainder : minutes + ':' + remainder
}
