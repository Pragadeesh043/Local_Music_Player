import { Compass, Disc3, FilePlus2, FolderOpen, Keyboard, LibraryBig, ListFilter, ListMusic, RefreshCw, Search, Settings as SettingsIcon, X } from 'lucide-react'
import { AnimatePresence, LayoutGroup, motion, MotionConfig } from 'motion/react'
import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import type { CSSProperties } from 'react'
import { Ambient, useAmbient } from './Ambient'
import type { DesktopAction } from './bridge'
import { CatalogView } from './Library'
import type { BrowsePosition, Detail, Route } from './Library'
import { LibraryTools } from './LibraryTools'
import { DspTools } from './DspTools'
import { handlePlaybackShortcut, keyboardShortcuts } from './keyboardShortcuts'
import { FileTools } from './FileTools'
import { OutputSettings } from './OutputSettings'
import { SignalTools } from './SignalTools'
import { Settings } from './Settings'
import { ToolDialog } from './ToolDialog'
import { Dock, IconButton, NowPlaying, QueueRail, spring } from './NowPlaying'
import { Player, usePlayer, useResource } from './player'
import type { AudioTool } from './LiveTelemetry'

const navigation = [
  { title: 'Discover', Icon: Compass },
  { title: 'Browse', Icon: Disc3 },
  { title: 'Library', Icon: LibraryBig },
  { title: 'Playlists', Icon: ListMusic },
] as const
type Tool = AudioTool | 'Library tools' | 'Track details' | 'Keyboard shortcuts'
const queueRailMedia = '(min-width: 1160px)'

export function App({ player }: { player: Player }) {
  const [route, setRoute] = useState<Route>('Discover')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [detail, setDetail] = useState<Detail | null>(null)
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [folder, setFolder] = useState('')
  const [browseFilterRevision, setBrowseFilterRevision] = useState(0)
  const [tool, setTool] = useState<Tool | null>(null)
  const [fileId, setFileId] = useState('')
  const [expanded, setExpanded] = useState(false)
  const [queue, setQueue] = useState(false)
  const [showRail, setShowRail] = useState(true)
  const [wide, setWide] = useState(() => matchMedia(queueRailMedia).matches)
  const [error, setError] = useState('')
  const searchInput = useRef<HTMLInputElement>(null)
  const focusSearch = useRef(false)
  const browsePosition = useRef<BrowsePosition | null>(null)
  const opener = useRef<HTMLElement | null>(null)
  const restoreFocus = useRef(false)
  const { track, desktop, playing } = usePlayer(player)
  const ambient = useAmbient(track?.artwork)
  const catalogRevision = desktop?.catalogRevision
  const browseScope = route === 'Browse' ? JSON.stringify([query, folder, browseFilterRevision]) : ''
  const loadPlaylists = useCallback(() => player.bridge.request('library', { kind: 'playlists', offset: 0, limit: 5 }),
    [player, catalogRevision])
  const { data: playlists } = useResource(loadPlaylists)
  const desktopAction = (action: DesktopAction) => notify(player.send('desktop', { action }))

  const notify = useCallback((operation: Promise<unknown>) => {
    setError('')
    void operation.catch((failure: Error) => setError(failure.message))
  }, [])

  useEffect(() => {
    void player.ready.catch((failure: Error) => setError(failure.message))
  }, [player])

  useEffect(() => {
    notify(player.send('theme', { dark: true }))
  }, [notify, player])

  useEffect(() => {
    const media = matchMedia(queueRailMedia)
    const update = () => setWide(media.matches)
    media.addEventListener('change', update)
    update()
    return () => media.removeEventListener('change', update)
  }, [])

  useEffect(() => {
    if (!search.trim()) {
      setQuery('')
      return
    }
    const timer = window.setTimeout(() => setQuery(search.trim()), 180)
    return () => window.clearTimeout(timer)
  }, [search])

  useEffect(() => {
    if (!expanded && restoreFocus.current) {
      restoreFocus.current = false
      opener.current?.focus()
    }
  }, [expanded])

  useEffect(() => {
    if (!settingsOpen && focusSearch.current) {
      focusSearch.current = false
      searchInput.current?.focus()
    }
  }, [settingsOpen])

  const openSearch = useCallback(() => {
    if (settingsOpen) {
      focusSearch.current = true
      setSettingsOpen(false)
    } else {
      searchInput.current?.focus()
    }
  }, [settingsOpen])

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.defaultPrevented || event.isComposing || tool) return
      const host = player.snapshot().desktop
      if (host && event.key === 'F11') {
        event.preventDefault()
        if (!event.repeat) notify(player.send('desktop', { action: 'fullscreen' }))
        return
      }
      if (!expanded && !event.altKey && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        openSearch()
        return
      }
      if (event.key === 'Escape' && !expanded) {
        const target = event.target instanceof Element ? event.target : null
        if (target?.closest('.queue-rail') && wide && showRail) {
          event.preventDefault()
          document.querySelector<HTMLButtonElement>('.dock [aria-label="Hide queue"]')?.focus()
          setShowRail(false)
        } else if (host?.fullscreen && !target?.closest('input, textarea, select, [contenteditable]')) {
          event.preventDefault()
          if (!event.repeat) notify(player.send('desktop', { action: 'fullscreen' }))
        }
        return
      }
      handlePlaybackShortcut(event, player, notify)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [player, notify, tool, expanded, wide, showRail, openSearch])

  function navigate(next: Route) {
    setSettingsOpen(false)
    setDetail(null)
    setSearch('')
    setQuery('')
    setRoute(next)
  }

  function expand(withQueue = false) {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setQueue(withQueue)
    setExpanded(true)
  }

  function close() {
    restoreFocus.current = true
    setQueue(false)
    setExpanded(false)
  }

  return <MotionConfig reducedMotion="user">
    <LayoutGroup>
      <div className="app-shell" style={{ '--album-primary': ambient.colors[0], '--album-secondary': ambient.colors[1] } as CSSProperties}>
        <Ambient layer={ambient} />
        <div className="workspace" inert={expanded} aria-hidden={expanded || undefined} data-queue-rail={wide && showRail}>
          <aside className="sidebar" aria-label="Music navigation">
            <div className="brand"><img className="brand-symbol" src="./brand.png" alt="" width="38" height="38" /><strong>MP4A</strong></div>
            <nav>
              <button type="button" className="nav-item" title="Search" onClick={openSearch}>
                <Search size={19} /><span>Search</span>
              </button>
              {navigation.map(({ title, Icon }) => <button key={title} type="button" className="nav-item" title={title}
                aria-current={!settingsOpen && route === title ? 'page' : undefined} onClick={() => navigate(title)}>
                {!settingsOpen && route === title && <motion.span className="nav-highlight" layoutId="active-navigation" transition={spring} />}
                <Icon size={19} /><span>{title}</span>
              </button>)}
            </nav>
            {playlists?.items.some(item => 'description' in item) && <nav className="playlist-shortcuts" aria-label="Playlist shortcuts">
              {playlists.items.filter(item => 'description' in item).map(item => <button key={item.id}
                type="button" className="playlist-shortcut" title={item.title} onClick={() => {
                  navigate('Playlists')
                  setDetail({ kind: 'playlist', item })
                }}>{item.title}</button>)}
            </nav>}
            {desktop && <nav className="desktop-nav" aria-label="Desktop tools">
              {desktop.mediaToolsAvailable === false && <button className="nav-item" title="Set up FFmpeg" onClick={() => desktopAction('setupFFmpeg')}><FolderOpen size={19} /><span>Set up FFmpeg</span></button>}
              <button className="nav-item" title="Add files" onClick={() => desktopAction('addFiles')}><FilePlus2 size={19} /><span>Add files</span></button>
              <button className="nav-item" title="Watch folder" onClick={() => desktopAction('watchFolder')}><FolderOpen size={19} /><span>Watch folder</span></button>
              <button className="nav-item" title="Reindex library" onClick={() => desktopAction('reindex')}><RefreshCw size={19} /><span>Reindex library</span></button>
              <button className="nav-item" title="Library tools" onClick={() => setTool('Library tools')}><ListFilter size={19} /><span>Library tools</span></button>
              <button className="nav-item" title="Keyboard shortcuts" onClick={() => setTool('Keyboard shortcuts')}><Keyboard size={19} /><span>Keyboard shortcuts</span></button>
              <button className="nav-item" title="Settings" aria-current={settingsOpen ? 'page' : undefined}
                onClick={() => setSettingsOpen(true)}><SettingsIcon size={19} /><span>Settings</span></button>
              <div className="desktop-output" title={desktop.output}>{desktop.output}</div>
            </nav>}
          </aside>
          <main className="main-panel">{settingsOpen ? <Settings player={player} notify={notify} open={setTool} /> : <>
            <header className="toolbar">
              <div className="search">
                <Search size={17} aria-hidden="true" />
                <input ref={searchInput} type="search" value={search} placeholder="Search this view" aria-label="Search this view"
                  onChange={event => { setDetail(null); setSearch(event.target.value) }}
                  onKeyDown={event => {
                    if (event.key === 'Escape' && !event.nativeEvent.isComposing) {
                      event.preventDefault()
                      setSearch('')
                      setQuery('')
                    }
                  }} />
                {search ? <IconButton label="Clear search" action={() => { setSearch(''); setQuery(''); searchInput.current?.focus() }}><X size={15} /></IconButton> : <kbd>Ctrl K</kbd>}
              </div>
            </header>
            <AnimatePresence initial={false} mode="wait">
              <motion.div className="route-frame" key={JSON.stringify([route, query, folder, detail?.item.id ?? '', catalogRevision ?? 0, browseScope])}
                initial={{ opacity: 0, y: 9 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -5 }}
                transition={{ duration: 0.17 }}>
                <CatalogView bridge={player.bridge} revision={catalogRevision ?? 0} route={route} query={query} folder={folder} detail={detail} open={setDetail} back={() => setDetail(null)}
                  browsePosition={route === 'Browse' && !detail ? browsePosition : undefined} browseScope={browseScope}
                  recent={player.recentTracks()} playingTrackId={playing ? track?.id : undefined}
                  play={(id, albumId, playlistId) => notify(player.send('play', { id, ...(albumId && { albumId }), ...(playlistId && { playlistId }),
                    ...(query && { query }), ...(folder && { folder }) }))}
                  enqueue={id => notify(player.send('enqueue', { id }))}
                  inspect={desktop ? id => { setFileId(id); setTool('Track details') } : undefined} />
              </motion.div>
            </AnimatePresence>
            </>}
            {desktop?.status && <div className="desktop-status" role="status">{desktop.status}</div>}
          </main>
          {wide && showRail && !expanded && <QueueRail player={player} notify={notify} openTool={setTool} />}
          <Dock player={player} notify={notify} expand={expand} active={!expanded}
            openTool={setTool}
            queueVisible={wide ? showRail : undefined} toggleQueue={() => wide ? setShowRail(visible => !visible) : expand(true)} />
        </div>
        <AnimatePresence>
          {expanded && <NowPlaying player={player} ambient={ambient} notify={notify} close={close} queueOpen={queue} setQueueOpen={setQueue} openTool={setTool}
            inspect={desktop && track ? () => { setFileId(track.id); setTool('Track details') } : undefined} />}
        </AnimatePresence>
        {tool && <ToolDialog key={tool} title={tool} close={() => setTool(null)}>
          {tool === 'Output settings' && <OutputSettings player={player} />}
          {tool === 'Library tools' && <LibraryTools player={player} folder={folder} setFolder={value => { setFolder(value); if (value) navigate('Library') }}
            onFilterChange={() => setBrowseFilterRevision(revision => revision + 1)} />}
          {tool === 'DSP' && <DspTools player={player} />}
          {tool === 'Signal inspector' && <SignalTools player={player} />}
          {tool === 'Track details' && <FileTools player={player} id={fileId} />}
          {tool === 'Keyboard shortcuts' && <dl className="tool-details tool-card">
            {keyboardShortcuts.map(([keys, action]) => <Fragment key={keys}><dt><kbd>{keys}</kbd></dt><dd>{action}</dd></Fragment>)}
          </dl>}
        </ToolDialog>}
        {error && <div className="toast glass" role="alert">{error}<IconButton label="Dismiss error" action={() => setError('')}><X size={17} /></IconButton></div>}
      </div>
    </LayoutGroup>
  </MotionConfig>
}
