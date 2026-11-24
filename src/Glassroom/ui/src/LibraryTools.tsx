import { useCallback, useState } from 'react'
import type { LibrarySettings, SmartFilter } from './desktop'
import { Player, useResource } from './player'

const sortFields = ['Album order', 'Title', 'Artist', 'Album artist', 'Album', 'Disc / track', 'Release year',
  'Date added', 'Filename', 'Duration', 'Codec', 'Sample rate', 'Bit depth', 'Rating', 'Play count']
const blank: SmartFilter = { codec: '', genre: '', year: '', minimumRate: 0, minimumBits: 0, minimumDr: 0, rating: 0, plays: 0 }

export function LibraryTools({ player, folder, setFolder }: { player: Player; folder: string; setFolder: (folder: string) => void }) {
  const load = useCallback(() => player.bridge.request('librarySettings', {}), [player])
  const { data, error } = useResource(load)
  if (!data) return <p role="status">{error ?? 'Reading the library…'}</p>
  return <LibraryForm initial={data} player={player} folder={folder} setFolder={setFolder} />
}

function LibraryForm({ initial, player, folder, setFolder }: {
  initial: LibrarySettings; player: Player; folder: string; setFolder: (folder: string) => void
}) {
  const [filter, setFilter] = useState(initial.filter)
  const [sort, setSort] = useState(initial.sort)
  const [descending, setDescending] = useState(initial.descending)
  const [name, setName] = useState('')
  const [saved, setSaved] = useState(initial.saved)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const numeric = [
    ['Minimum sample rate (Hz)', 'minimumRate', 5000000, 1], ['Minimum bit depth', 'minimumBits', 64, 1],
    ['Minimum scanned DR', 'minimumDr', 80, .1], ['Minimum rating', 'rating', 5, 1], ['Minimum plays', 'plays', 100000, 1],
  ] as const
  const text = [['Codec', 'codec'], ['Genre', 'genre'], ['Year', 'year']] as const

  function apply(value: SmartFilter, title: string) {
    setBusy(true)
    setMessage('')
    void player.send('saveLibrary', { filter: value, sort, descending, name: title }).then(() => {
      setFilter(value)
      if (title) setSaved(current => ({ ...current, [title]: value }))
      setMessage('Library preferences updated.')
    }, error => setMessage(error.message)).finally(() => setBusy(false))
  }

  return <form className="tool-form" onSubmit={event => { event.preventDefault(); apply(filter, name.trim()) }}>
    <label>Folder<select value={folder} onChange={event => setFolder(event.target.value)}>
      <option value="">All folders</option>{initial.folders.map(path => <option key={path}>{path}</option>)}
    </select></label>
    <fieldset className="tool-fields" disabled={busy}>
      <label>Sort by<select value={sort} onChange={event => setSort(Number(event.target.value))}>
        {sortFields.map((field, index) => <option key={field} value={index}>{field}</option>)}
      </select></label>
      <label className="tool-check"><input type="checkbox" checked={descending} onChange={event => setDescending(event.target.checked)} /> Descending</label>
      <label>Saved smart filter<select value={name in saved ? name : ''} onChange={event => {
        const selected = event.target.value
        setName(selected)
        if (selected) setFilter(saved[selected])
      }}><option value="">Choose a saved filter</option>{Object.keys(saved).map(title => <option key={title}>{title}</option>)}</select></label>
      <label>Save filter as<input value={name} maxLength={100} onChange={event => setName(event.target.value)} /></label>
      {text.map(([label, key]) => <label key={key}>{label}<input value={filter[key]} maxLength={key === 'year' ? 4 : 100}
        onChange={event => setFilter(current => ({ ...current, [key]: event.target.value }))} /></label>)}
      {numeric.map(([label, key, max, step]) => <label key={key}>{label}<input type="number" min={0} max={max} step={step}
        value={filter[key]} onChange={event => setFilter(current => ({ ...current, [key]: Number(event.target.value) }))} /></label>)}
    </fieldset>
    {initial.sources.length > 0 && <section><h3>Watched folders</h3><ul className="tool-list">
      {initial.sources.map(source => <li key={source.id}>{source.name} · {source.online ? 'Connected' : 'Offline'}</li>)}
    </ul></section>}
    <div className="tool-actions"><button className="primary-action" disabled={busy}>Apply</button>
      <button className="load-more" type="button" disabled={busy} onClick={() => { setName(''); apply(blank, '') }}>Clear filters</button>
      <span role="status">{message}</span></div>
  </form>
}
