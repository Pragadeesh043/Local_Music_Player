import { useVirtualizer } from '@tanstack/react-virtual'
import { ArrowLeft, ArrowRight, ChevronLeft, ChevronRight, Info, ListMusic, Pause, Play, Plus } from 'lucide-react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { RefObject } from 'react'
import { Artwork } from './Artwork'
import type { Album, Bridge, LibraryQuery, Playlist, Track } from './bridge'
import { timeLabel, useResource } from './player'

export type Route = 'Discover' | 'Browse' | 'Library' | 'Playlists'
export type Detail = { kind: 'album'; item: Album } | { kind: 'playlist'; item: Playlist }
type Item = Album | Playlist | Track
const pageSize = 60
const discoverySize = 6

export function CatalogView({ bridge, route, query, folder, detail, open, back, play, enqueue, inspect, recent }: {
  bridge: Bridge
  route: Route
  query: string
  folder: string
  detail: Detail | null
  open: (detail: Detail) => void
  back: () => void
  play: (id: string, albumId?: string, playlistId?: string) => void
  enqueue: (id: string) => void
  inspect?: (id: string) => void
  recent: Track[]
}) {
  const scroll = useRef<HTMLDivElement>(null)
  const requested = useRef(new Set<number>())
  const active = useRef(false)
  const [pages, setPages] = useState(new Map<number, Item[]>())
  const [total, setTotal] = useState<number | null>(null)
  const [errors, setErrors] = useState(new Map<number, string>())
  const kind = detail || route === 'Library' ? 'tracks' : route === 'Playlists' ? 'playlists' : 'albums'
  const albumId = detail?.kind === 'album' ? detail.item.id : undefined
  const playlistId = detail?.kind === 'playlist' ? detail.item.id : undefined
  const first = pages.get(0)?.[0]
  const discover = route === 'Discover' && !query && !detail
  const albums = discover ? (pages.get(0) ?? []).filter(item => 'firstTrackId' in item) : []
  const failure = errors.entries().next().value

  const load = useCallback((offset: number) => {
    if (requested.current.has(offset)) return
    requested.current.add(offset)
    const args: LibraryQuery = { kind, offset, limit: discover ? discoverySize : pageSize, ...(query && { query }), ...(albumId && { albumId }),
      ...(playlistId && { playlistId }), ...(folder && { folder }) }
    void bridge.request('library', args).then(result => {
      if (!active.current) return
      setPages(previous => new Map(previous).set(offset, result.items))
      setTotal(result.total)
    }, (error: Error) => {
      if (!active.current) return
      setErrors(previous => new Map(previous).set(offset, error.message))
    })
  }, [bridge, kind, query, folder, albumId, playlistId, discover])

  useEffect(() => {
    active.current = true
    load(0)
    return () => { active.current = false }
  }, [load])

  function retry(offset: number) {
    setErrors(previous => {
      const next = new Map(previous)
      next.delete(offset)
      return next
    })
    requested.current.delete(offset)
    load(offset)
  }

  return <div className="catalog-scroll" ref={scroll} tabIndex={0} aria-label={detail?.item.title ?? route}>
    <div className="catalog-content">
      {detail ? <header className="detail-header">
        <button className="back-link" onClick={back}><ArrowLeft size={16} /> Back to {route}</button>
        <div className="detail-identity">
          {detail.kind === 'album' ? <Artwork src={detail.item.artwork} loading="eager" /> : <span className="playlist-art"><ListMusic /></span>}
          <div><h1>{detail.item.title}</h1>
            <p>{detail.kind === 'album' ? [detail.item.artist, detail.item.year].filter(Boolean).join(' · ') : detail.item.description}</p>
            {detail.kind === 'album' && detail.item.edition && <p className="album-edition-detail">{detail.item.edition}</p>}
            {first && <button className="primary-action" onClick={() => play(first.id, albumId, playlistId)}><Play size={15} fill="currentColor" /> Play</button>}
          </div>
        </div>
      </header> : <header className="screen-heading">
        <h1>{query ? 'Search results' : route}</h1>
        {query && <p>Matches for “{query}”</p>}
      </header>}

      {!detail && !discover && <div className="section-heading">
        <h2>{kind === 'tracks' ? 'Songs' : kind === 'albums' ? 'Albums' : 'Your playlists'}</h2>
        {total !== null && total > 0 && <span>{total} {total === 1 ? kind.slice(0, -1) : kind}</span>}
      </div>}

      {failure && <div className="catalog-message catalog-error" role="alert">{failure[1]}
        <button className="back-link ml-2" onClick={() => retry(failure[0])}>Retry</button>
      </div>}
      {albums.length > 0 && <Discover bridge={bridge} folder={folder} albums={albums} recent={recent}
        open={open} play={play} enqueue={enqueue} inspect={inspect} />}
      {!discover && total !== null && total > 0 && <VirtualCatalog
        pages={pages} total={total} load={load} kind={kind} scroll={scroll} open={open}
        play={id => play(id, albumId, playlistId)} enqueue={enqueue} inspect={inspect} />}
      {!failure && total === null && <p className="catalog-message" role="status">Loading your library…</p>}
      {total === 0 && <p className="catalog-message">{query ? 'No matches found.' : 'There’s nothing here yet.'}</p>}
    </div>
  </div>
}

function Discover({ bridge, folder, albums, recent, open, play, enqueue, inspect }: {
  bridge: Bridge
  folder: string
  albums: Album[]
  recent: Track[]
  open: (detail: Detail) => void
  play: (id: string, albumId?: string) => void
  enqueue: (id: string) => void
  inspect?: (id: string) => void
}) {
  const loadTracks = useCallback(() => bridge.request('library', { kind: 'tracks', offset: 0, limit: discoverySize,
    ...(folder && { folder }) }), [bridge, folder])
  const { data, error } = useResource(loadTracks)
  const tracks = recent.length ? recent : data?.items.filter(item => 'albumId' in item)

  return <>
    <FeaturedCarousel albums={albums} track={tracks?.[0]} recent={recent.length > 0} open={open} play={play} />
    <div className="discover-sections">
      <section className="discover-tracks">
        <h2>{recent.length ? 'Recently Played' : 'Library Tracks'}</h2>
        {tracks?.map(track => <TrackRow key={track.id} item={track} play={play} enqueue={enqueue} inspect={inspect} />)}
        {!tracks && <p className="catalog-message">{error ?? 'Loading tracks…'}</p>}
        {tracks?.length === 0 && <p className="catalog-message">No tracks in this view.</p>}
      </section>
      <section className="discover-albums">
        <h2>From Your Library</h2>
        <div className="discover-album-grid">
          {albums.map(album => <AlbumCard key={album.id} item={album} open={() => open({ kind: 'album', item: album })} />)}
        </div>
      </section>
    </div>
  </>
}

function FeaturedCarousel({ albums, track, recent, open, play }: {
  albums: Album[]; track?: Track; recent: boolean
  open: (detail: Detail) => void; play: (id: string, albumId?: string) => void
}) {
  const slides: { item: Album | Track; label: string; album?: Album }[] = albums.slice(0, 4).map((album, index) => ({
    item: album, label: index === 0 ? 'Featured album' : 'Library highlight', album,
  }))
  if (track) slides.splice(1, 0, { item: track, label: recent ? 'Recently played' : 'Featured track',
    album: albums.find(album => album.id === track.albumId) })
  const [index, setIndex] = useState(0)
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  const [rotation, setRotation] = useState<boolean>()
  const reducedMotion = useReducedMotion()
  const rotating = rotation ?? !reducedMotion
  const count = slides.length
  const current = index % count
  const { item, label, album } = slides[current]
  const isTrack = 'albumId' in item

  useEffect(() => {
    if (!rotating || hovered || focused || count < 2) return
    const timer = window.setInterval(() => {
      if (!document.hidden) setIndex((current + 1) % count)
    }, 6500)
    return () => window.clearInterval(timer)
  }, [rotating, hovered, focused, count, current])

  return <section className="featured-albums" aria-label="Discover highlights" aria-roledescription="carousel"
    onPointerEnter={() => setHovered(true)} onPointerLeave={() => setHovered(false)}
    onFocus={() => setFocused(true)} onBlur={event => {
      if (!event.currentTarget.contains(event.relatedTarget)) setFocused(false)
    }}>
    <div className="hero-slides" aria-live={rotating && !focused && !hovered ? 'off' : 'polite'}>
      <AnimatePresence initial={false} mode="wait">
        <motion.article className="hero" key={item.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }}
          exit={{ opacity: 0 }} transition={{ duration: reducedMotion ? 0 : .23 }}
          aria-label={`${current + 1} of ${count}`} aria-roledescription="slide">
          <Artwork src={item.artwork} className="hero-image" loading="eager" />
          <div className="hero-copy">
            <span className="hero-label">{label}</span>
            <h2 title={item.title}>{item.title}</h2>
            <p>{item.artist}</p>
            <div className="hero-actions">
              <button className="primary-action" aria-label={`Play ${isTrack ? '' : 'album '}${item.title}`}
                onClick={() => isTrack ? play(item.id, item.albumId) : play(item.firstTrackId, item.id)}>
                <Play size={14} fill="currentColor" />{isTrack ? 'Play track' : 'Play album'}
              </button>
              {album && <button className="hero-link" aria-label={'View album ' + album.title}
                onClick={() => open({ kind: 'album', item: album })}>View album <ArrowRight size={14} /></button>}
            </div>
          </div>
        </motion.article>
      </AnimatePresence>
    </div>
    {count > 1 && <div className="carousel-controls">
      <button className="icon-button" aria-label={rotating ? 'Pause carousel' : 'Resume carousel'} onClick={() => setRotation(!rotating)}>
        {rotating ? <Pause size={14} /> : <Play size={14} />}
      </button>
      <button className="icon-button" aria-label="Previous highlight" onClick={() => setIndex((current - 1 + count) % count)}><ChevronLeft size={17} /></button>
      {slides.map((slide, position) => <button key={slide.item.id} className="carousel-dot" aria-label={'Show ' + slide.item.title}
        aria-current={current === position ? 'true' : undefined} onClick={() => setIndex(position)} />)}
      <button className="icon-button" aria-label="Next highlight" onClick={() => setIndex((current + 1) % count)}><ChevronRight size={17} /></button>
    </div>}
  </section>
}

function pageOffset(index: number) {
  return Math.floor(index / pageSize) * pageSize
}

function VirtualCatalog({ pages, total, load, kind, scroll, open, play, enqueue, inspect }: {
  pages: Map<number, Item[]>
  total: number
  load: (offset: number) => void
  kind: LibraryQuery['kind']
  scroll: RefObject<HTMLDivElement | null>
  open: (detail: Detail) => void
  play: (id: string) => void
  enqueue: (id: string) => void
  inspect?: (id: string) => void
}) {
  const list = useRef<HTMLDivElement>(null)
  const [geometry, setGeometry] = useState({ width: 1000, margin: 0 })
  const grid = kind !== 'tracks'
  const columns = grid ? Math.max(1, Math.min(6, Math.floor((geometry.width + 16) / 145))) : 1
  const rowHeight = grid ? Math.floor((geometry.width - (columns - 1) * 16) / columns) + 66 : 64

  useLayoutEffect(() => {
    const element = list.current
    const scroller = scroll.current
    if (!element || !scroller) return
    const measure = () => {
      const margin = Math.round(element.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop)
      const width = element.clientWidth
      setGeometry(previous => previous.margin === margin && previous.width === width ? previous : { margin, width })
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(scroller)
    if (element.parentElement) observer.observe(element.parentElement)
    return () => observer.disconnect()
  }, [scroll])

  const rows = useVirtualizer({
    count: Math.ceil(total / columns),
    getScrollElement: () => scroll.current,
    estimateSize: () => rowHeight,
    scrollMargin: geometry.margin,
    overscan: 4,
  })

  useLayoutEffect(() => { rows.measure() }, [rows, rowHeight, columns])

  const visible = rows.getVirtualItems()
  const firstRow = visible[0]?.index
  const lastRow = visible.at(-1)?.index
  useEffect(() => {
    if (firstRow === undefined || lastRow === undefined) return
    const start = pageOffset(firstRow * columns)
    const end = pageOffset(Math.min(total - 1, (lastRow + 1) * columns - 1))
    for (let offset = start; offset <= end; offset += pageSize)
      load(offset)
  }, [firstRow, lastRow, columns, total, load])

  return <div ref={list} role="list" className="virtual-catalog" style={{ height: rows.getTotalSize() }}>
    {visible.map(row => <div key={row.key} role="presentation" className="virtual-row"
      style={{ transform: 'translateY(' + (row.start - geometry.margin) + 'px)', height: row.size, gridTemplateColumns: 'repeat(' + columns + ', minmax(0, 1fr))' }}>
      {Array.from({ length: Math.min(columns, total - row.index * columns) }, (_, column) => {
        const index = row.index * columns + column
        const offset = pageOffset(index)
        const item = pages.get(offset)?.[index - offset]
        if (!item) return <div key={index} aria-hidden="true" className={'catalog-placeholder ' + (grid ? 'catalog-placeholder-art' : 'catalog-placeholder-track')} />
        return <div role="listitem" key={item.id}>
          {'albumId' in item ? <TrackRow item={item} play={play} enqueue={enqueue} inspect={inspect} />
            : 'firstTrackId' in item ? <AlbumCard item={item} open={() => open({ kind: 'album', item })} />
              : <PlaylistCard item={item} open={() => open({ kind: 'playlist', item })} />}
        </div>
      })}
    </div>)}
  </div>
}

function AlbumCard({ item, open }: { item: Album; open: () => void }) {
  return <motion.button className="album-card" onClick={open} whileTap={{ scale: 0.97 }}>
    <Artwork src={item.artwork} />
    <strong>{item.title}</strong><span>{item.artist}</span>
    {item.edition && <span className="album-edition" title={item.edition}>{item.edition}</span>}
  </motion.button>
}

function PlaylistCard({ item, open }: { item: Playlist; open: () => void }) {
  return <motion.button className="album-card" onClick={open} whileTap={{ scale: 0.97 }}>
    <span className="playlist-art"><ListMusic /></span>
    <strong>{item.title}</strong><span>{item.description}</span>
  </motion.button>
}

function TrackRow({ item, play, enqueue, inspect }: {
  item: Track; play: (id: string) => void; enqueue: (id: string) => void; inspect?: (id: string) => void
}) {
  return <div className={'track-row' + (inspect ? ' with-inspector' : '')}>
    <button className="track-link" onClick={() => play(item.id)} aria-label={'Play ' + item.title}>
      <Artwork src={item.artwork} />
      <span className="min-w-0"><strong>{item.title}</strong><small>{item.artist}</small></span>
    </button>
    <span className="track-album">{item.album}</span>
    <time>{timeLabel(item.duration)}</time>
    <button className="icon-button" aria-label={'Add ' + item.title + ' to queue'} title="Add to queue" onClick={() => enqueue(item.id)}><Plus size={16} /></button>
    {inspect && <button className="icon-button" aria-label={'Details for ' + item.title} title="Track details" onClick={() => inspect(item.id)}><Info size={16} /></button>}
  </div>
}
