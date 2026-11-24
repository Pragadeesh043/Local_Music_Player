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
    let active = true
    let observer: IntersectionObserver | undefined
    const start = () => {
      void loadArtwork(src).then(url => {
        if (active) setLoaded({ source: src, url })
      })
    }
    if (loading === 'eager' || cachedArtwork(src) !== undefined) {
      start()
    } else {
      observer = new IntersectionObserver(items => {
        if (items.some(item => item.isIntersecting)) {
          observer?.disconnect()
          start()
        }
      }, { rootMargin: '160px' })
      observer.observe(element)
    }
    return () => { active = false; observer?.disconnect() }
  }, [src, loading])

  return <span ref={container} className={'artwork ' + className}>
    {url && failed !== url
      ? <img src={url} crossOrigin="anonymous" alt="" loading={loading} draggable={false} onError={() => setFailed(url)} />
      : <AudioLines aria-hidden="true" />}
  </span>
}
