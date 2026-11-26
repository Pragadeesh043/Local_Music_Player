export type OutputProfile = {
  route: number; volumeMode: number; memory: boolean; memoryLimitMiB: number; targetRate: number
  outputBits: number; sharper: boolean; dopConfirmed: boolean; minimumPhase: boolean; asioDriver: string | null
}
export type OutputConfiguration = {
  devices: { id: string; name: string; rate: number; channels: number; hardwareVolume: boolean }[]
  selected: string | null; followDefault: boolean; drivers: string[]; rates: number[]
  minimumMemory: number; maximumMemory: number; profile: OutputProfile
}
export type SmartFilter = {
  codec: string; minimumRate: number; minimumBits: number; minimumDr: number
  genre: string; year: string; rating: number; plays: number
}
export type LibrarySettings = {
  filter: SmartFilter; sort: number; descending: boolean; saved: Record<string, SmartFilter>
  folders: string[]; sources: { id: string; name: string; online: boolean }[]
}
export type TrackDetails = {
  title: string; artist: string; album: string; path: string; format: string; fileSize: number
  codec: string; sourceRate: number; bits: number; bitRate: number; channels: number; layout: string
  duration: number; rating: number; playCount: number; artwork: string | null; artworkPreview?: string | null; tags: Record<string, string>
  analysis: {
    lufs: number | null; truePeak: number | null; dynamicRange: number | null; replayGain: number | null
    spectrum: string; integrity: string; albumLufs: number | null; albumReplayGain: number | null
  } | null
}
export type BookletAsset = { name: string; pdf: boolean }
export type BookletPage = { url: string; page: number; pages: number }
export type EqBand = { frequency: number; gain: number; q: number; shape: number; channel: number; active: boolean }
export type Dsp = {
  enabled: boolean; preamp: number; bass: number; width: number; crossfeed: number; balance: number
  invert: boolean; dither: number; normalization: number; headroom: number; limiter: boolean
  parametric: EqBand[]; graphic: number[]
}
export type DspView = { settings: Dsp; impulse: string | null; presets: string[]; left: number[]; right: number[] }
export type DspAction = 'apply' | 'reset' | 'load' | 'save' | 'autoeq' | 'impulse' | 'clearImpulse'
export type Meter = { peak: number; rms: number; clips: number }
export type LiveSignal = {
  trackId?: string
  source: Meter[]; output: Meter[]; waveform: number[]; waveChannel: number; truePeak: number | null
  lufs: number | null; correlation: number | null; phase: { side: number; mid: number }[] | null
  limiterReductionDb: number | null; spectrum: number[]; sourceChannels: string[]; outputChannels: string[]
}
export type SignalReport = {
  report: { verdict: string; exactAtOutput: boolean; explanation: string; outputSummary: string
    stages: { name: string; summary: string; detail: string }[] }
  notice: string; asio: boolean; rate: number | null
  hardware: { decibels: number | null; scalar: number | null; muted: boolean | null
    minimumDb: number | null; maximumDb: number | null; stepDb: number | null } | null
}
