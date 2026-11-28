import type { Notify } from './NowPlaying'
import type { Player } from './player'

const seekSeconds = 5
const longSeekSeconds = 30
const volumeStep = 0.02

export const keyboardShortcuts = [
  ['Space', 'Play or pause'],
  ['Left / Right', `Seek ${seekSeconds} seconds`],
  ['Shift + Left / Right', `Seek ${longSeekSeconds} seconds`],
  ['Up / Down', 'Adjust volume'],
  ['M', 'Mute or restore volume'],
  ['S', 'Toggle shuffle'],
  ['Media keys', 'Play, pause, skip, or stop through Windows'],
  ['Ctrl + K', 'Focus search'],
  ['Escape', 'Close the active panel'],
  ['F11', 'Toggle fullscreen'],
] as const

export function handlePlaybackShortcut(event: KeyboardEvent, player: Player, notify: Notify) {
  if (event.ctrlKey || event.metaKey || event.altKey) return
  const target = event.target instanceof Element ? event.target : null
  const control = target?.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), ' +
    '[role="textbox"], [role="searchbox"], [role="combobox"], [role="slider"], [role="listbox"], ' +
    '[role="menu"], [role="tablist"], [role="spinbutton"]')
  if (control) return

  const { track, desktop } = player.snapshot()
  const key = event.key.toLowerCase()
  if (key === ' ' && track && !event.shiftKey) {
    const button = target?.closest('button, summary, [role="button"]')
    if (button?.matches(':focus-visible')) return
    event.preventDefault()
    if (!event.repeat) notify(player.togglePlayback())
  } else if ((key === 'arrowleft' || key === 'arrowright') && track) {
    event.preventDefault()
    const distance = event.shiftKey ? longSeekSeconds : seekSeconds
    if (!event.repeat) notify(player.seekBy(key === 'arrowleft' ? -distance : distance))
  } else if (!event.shiftKey && (key === 'arrowup' || key === 'arrowdown') && desktop?.volumeEnabled !== false) {
    event.preventDefault()
    notify(player.adjustVolume(key === 'arrowup' ? volumeStep : -volumeStep))
  } else if (!event.shiftKey && key === 'm' && desktop?.volumeEnabled !== false) {
    event.preventDefault()
    if (!event.repeat) notify(player.toggleMute())
  } else if (!event.shiftKey && key === 's' && desktop) {
    event.preventDefault()
    if (!event.repeat) notify(player.send('desktop', { action: 'shuffle' }))
  }
}
