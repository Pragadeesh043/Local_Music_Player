const coverOrigin = 'https://media.mp4a.glassroom.test/cover/'
const byteLimit = 32 * 1024 * 1024
const entryLimit = 768
const parallelRequests = 2
const entries = new Map<string, { url: string | null; bytes: number; expires: number }>()
type Request = {
  urgentConsumers: number
  consumers: number
  promise: Promise<string | null>
  finish: (url: string | null) => void
}
const pending = new Map<string, Request>()
const queued = new Map<string, Request>()
let fetching = 0
let retainedBytes = 0

function evict(source: string) {
  const entry = entries.get(source)
  if (!entry) return
  entries.delete(source)
  retainedBytes -= entry.bytes
  if (entry.url) URL.revokeObjectURL(entry.url)
}

export function cachedArtwork(source: string): string | null | undefined {
  if (!source.startsWith(coverOrigin)) return source
  const entry = entries.get(source)
  if (!entry || (!entry.url && entry.expires <= Date.now())) return undefined
  return entry.url
}

function drain() {
  while (fetching < parallelRequests && queued.size) {
    const next = [...queued].find(([, request]) => request.urgentConsumers > 0) ?? queued.entries().next().value!
    const [source, request] = next
    const entry = entries.get(source)
    queued.delete(source)
    fetching++
    void fetch(source, { credentials: 'omit' }).then(async response => {
      const missing = response.status === 204 || response.status === 404
      if (!response.ok && !missing) throw new Error('Artwork is unavailable.')
      const blob = missing ? null : await response.blob()
      const url = blob?.size ? URL.createObjectURL(blob) : null
      const bytes = blob?.size ?? 0
      evict(source)
      entries.set(source, { url, bytes, expires: Date.now() + (url ? 30 * 60_000 : 30_000) })
      retainedBytes += bytes
      while (entries.size > entryLimit || retainedBytes > byteLimit)
        evict(entries.keys().next().value!)
      return url
    }).catch(() => entry?.url ?? source).then(request.finish).finally(() => {
      fetching--
      pending.delete(source)
      drain()
    })
  }
}

export function loadArtwork(source: string, urgent: boolean, signal: AbortSignal): Promise<string | null> {
  if (signal.aborted) return Promise.resolve(null)
  if (!source.startsWith(coverOrigin)) return Promise.resolve(source)
  const entry = entries.get(source)
  if (entry) {
    entries.delete(source)
    entries.set(source, entry)
    if (entry.expires > Date.now()) return Promise.resolve(entry.url)
  }

  let request = pending.get(source)
  if (!request) {
    let finish!: Request['finish']
    const promise = new Promise<string | null>(resolve => { finish = resolve })
    request = { urgentConsumers: 0, consumers: 0, promise, finish }
    pending.set(source, request)
    queued.set(source, request)
  }
  if (urgent) request.urgentConsumers++
  request.consumers++
  const shared = request
  const cancel = () => {
    shared.consumers--
    if (urgent) shared.urgentConsumers--
    if (shared.consumers || queued.get(source) !== shared) return
    queued.delete(source)
    pending.delete(source)
    shared.finish(null)
  }
  signal.addEventListener('abort', cancel, { once: true })
  drain()
  return shared.promise.finally(() => signal.removeEventListener('abort', cancel))
}
