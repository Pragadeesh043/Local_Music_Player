import { AudioLines } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { cachedArtwork, loadArtwork } from './artworkResource'

export function Artwork({ src, className = '', loading = 'lazy' }: {
  src?: string
  className?: string
  loading?: 'lazy' | 'eager'
}) {
  const container = useRef<HTMLSpanElement>(null)
  const [loaded, setLoaded] = useState<{ source: string; url: string | null }>()
  const [failed, setFailed] = useState<string>()
  const cached = src ? cachedArtwork(src) : undefined
  const url = cached !== undefined ? cached : (loaded?.source === src ? loaded?.url : undefined)

  useEffect(() => {
    const element = container.current
    if (!src || !element) return
    let request: AbortController | undefined
    let observer: IntersectionObserver | undefined
    const start = () => {
      if (request) return
      const current = new AbortController()
      request = current
      void loadArtwork(src, loading === 'eager', current.signal).then(url => {
        if (!current.signal.aborted) {
          setLoaded({ source: src, url })
          observer?.disconnect()
        }
      })
    }
    const stop = () => {
      request?.abort()
      request = undefined
    }
    if (loading === 'eager' || cachedArtwork(src) !== undefined) {
      start()
    } else {
      observer = new IntersectionObserver(items => {
        if (items.at(-1)?.isIntersecting) start()
        else stop()
      })
      observer.observe(element)
    }
    return () => { stop(); observer?.disconnect() }
  }, [src, loading])

  return <span ref={container} className={'artwork ' + className}>
    {url && failed !== url
      ? <img src={url} crossOrigin="anonymous" alt="" loading={loading} draggable={false} onError={() => setFailed(url)} />
      : <AudioLines aria-hidden="true" />}
  </span>
}
