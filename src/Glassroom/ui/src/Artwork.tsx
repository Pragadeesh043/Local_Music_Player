import { AudioLines } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { cachedArtwork, cachedArtworkPreview, loadArtwork } from './artworkResource'

export function Artwork({ src, preview, className = '', loading = 'lazy' }: {
  src?: string | null
  preview?: string | null
  className?: string
  loading?: 'lazy' | 'eager'
}) {
  const container = useRef<HTMLSpanElement>(null)
  const [loaded, setLoaded] = useState<{ source: string; url: string | null }>()
  const [failed, setFailed] = useState<string>()
  const [painted, setPainted] = useState<string>()
  const cached = src ? cachedArtwork(src) : undefined
  const url = cached !== undefined ? cached : (loaded?.source === src ? loaded?.url : undefined)
  const fallback = preview || (src ? cachedArtworkPreview(src) : undefined)

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
      }, { root: element.closest('.catalog-scroll'), rootMargin: '240px 0px' })
      observer.observe(element)
    }
    return () => { stop(); observer?.disconnect() }
  }, [src, loading])

  return <span ref={container} className={'artwork ' + className}
    style={fallback ? { backgroundImage: `url("${fallback}")`, backgroundPosition: 'center', backgroundSize: 'cover' } : undefined}>
    {url && failed !== url
      ? <img className={fallback && painted !== url ? 'artwork-full artwork-pending' : 'artwork-full'}
        src={url} crossOrigin="anonymous" alt="" loading={loading} draggable={false}
        onLoad={() => setPainted(url)} onError={() => setFailed(url)} />
      : !fallback && <AudioLines aria-hidden="true" />}
  </span>
}
