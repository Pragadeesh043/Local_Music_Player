const coverOrigin = 'https://media.mp4a.glassroom.test/cover/'
const byteLimit = 32 * 1024 * 1024
const entryLimit = 768
const entries = new Map<string, { url: string | null; bytes: number; expires: number }>()
const pending = new Map<string, Promise<string | null>>()
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

export function loadArtwork(source: string): Promise<string | null> {
  if (!source.startsWith(coverOrigin)) return Promise.resolve(source)
  const entry = entries.get(source)
  if (entry) {
    entries.delete(source)
    entries.set(source, entry)
    if (entry.expires > Date.now()) return Promise.resolve(entry.url)
  }

  let request = pending.get(source)
  if (!request) {
    request = fetch(source, { credentials: 'omit' }).then(async response => {
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
    }).catch(() => entry?.url ?? source).finally(() => pending.delete(source))
    pending.set(source, request)
  }
  return request
}
