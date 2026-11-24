import { useCallback, useEffect, useState } from 'react'
import { Artwork } from './Artwork'
import type { BookletPage, TrackDetails } from './desktop'
import { Player, timeLabel, useResource } from './player'

export function FileTools({ player, id }: { player: Player; id: string }) {
  const [revision, setRevision] = useState(0)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [booklet, setBooklet] = useState(false)
  const load = useCallback(() => player.bridge.request('trackDetails', { id }), [id, player, revision])
  const { data, error } = useResource(load)

  function update(operation: Promise<unknown>, result: string) {
    setMessage('')
    setBusy(true)
    void operation.then(() => { setRevision(value => value + 1); setMessage(result) }, failure => setMessage(failure.message))
      .finally(() => setBusy(false))
  }

  return <div className="tool-form">
    {data ? <TrackFields player={player} id={id} update={update} track={data} busy={busy} /> :
      <p role="status">{error ?? 'Reading track details…'}</p>}
    <div className="tool-actions">
      <button className="load-more" disabled={busy} onClick={() => update(player.bridge.request('analyzeTrack', { id, edition: false }), 'Track analysis complete.')}>Scan track</button>
      <button className="load-more" disabled={busy} onClick={() => update(player.bridge.request('analyzeTrack', { id, edition: true }), 'Edition loudness scan complete.')}>Scan edition</button>
      <button className="load-more" onClick={() => setBooklet(open => !open)} aria-expanded={booklet}>Artwork and booklet</button>
      <span role="status">{busy ? 'Working…' : message}</span>
    </div>
    {booklet && <Booklet player={player} id={id} />}
  </div>
}

function TrackFields({ player, id, update, track, busy }: {
  player: Player; id: string; track: TrackDetails; busy: boolean
  update: (operation: Promise<unknown>, result: string) => void
}) {
  return <>
    <div className="tool-track"><Artwork src={track.artwork} loading="eager" /><div><h3>{track.title}</h3><p>{track.artist} · {track.album}</p></div></div>
    <dl className="tool-details">
      <dt>File</dt><dd>{track.path}</dd><dt>Container</dt><dd>{track.format} · {track.fileSize.toLocaleString()} bytes</dd>
      <dt>Audio</dt><dd>{[track.codec, track.sourceRate.toLocaleString() + ' Hz', track.bits ? track.bits + ' bit' : '',
        track.bitRate ? (track.bitRate / 1000).toFixed(1) + ' kbps' : '', track.channels + ' channels', track.layout].filter(Boolean).join(' · ')}</dd>
      <dt>Length</dt><dd>{timeLabel(track.duration)} · {track.playCount} plays</dd>
      {track.analysis && <>
        <dt>Analysis</dt><dd>{[
          [track.analysis.lufs, 'LUFS'], [track.analysis.truePeak, 'dBTP'], [track.analysis.dynamicRange, 'dB DR'],
          [track.analysis.replayGain, 'dB track ReplayGain'], [track.analysis.albumLufs, 'album LUFS'],
          [track.analysis.albumReplayGain, 'dB album ReplayGain'],
        ].filter(([value]) => value != null).map(([value, label]) => Number(value).toFixed(1) + ' ' + label).join(' · ')}
        <p>{track.analysis.spectrum}</p><p>{track.analysis.integrity}</p></dd>
      </>}
    </dl>
    <label>Rating<select disabled={busy} value={track.rating} onChange={event =>
      update(player.send('rateTrack', { id, rating: Number(event.target.value) }), 'Rating saved.')}>
      <option value={0}>Unrated</option>{[1, 2, 3, 4, 5].map(value => <option key={value}>{value}</option>)}
    </select></label>
    {Object.keys(track.tags).length > 0 && <details><summary>Metadata tags</summary><dl className="tool-details">
      {Object.entries(track.tags).sort(([left], [right]) => left.localeCompare(right)).map(([tag, value]) =>
        <div className="contents" key={tag}><dt>{tag}</dt><dd>{value}</dd></div>)}
    </dl></details>}
  </>
}

function Booklet({ player, id }: { player: Player; id: string }) {
  const load = useCallback(() => player.bridge.request('booklet', { id }), [player, id])
  const { data, error } = useResource(load)
  const [index, setIndex] = useState(0)
  const [page, setPage] = useState(0)
  const [image, setImage] = useState<BookletPage>()
  const [imageError, setImageError] = useState('')
  useEffect(() => {
    if (!data?.length) return
    let active = true
    setImage(undefined)
    setImageError('')
    void player.bridge.request('bookletPage', { id, index, page }).then(result => {
      if (active) setImage(result)
    }, failure => { if (active) setImageError(failure.message) })
    return () => { active = false }
  }, [player, id, data, index, page])

  if (error || !data?.length) return <p role="status">{error ?? (data ? 'No embedded artwork or folder booklets found.' : 'Finding artwork…')}</p>
  return <section className="tool-form">
    <label>Artwork or booklet<select value={index} onChange={event => { setIndex(Number(event.target.value)); setPage(0) }}>
      {data.map((item, i) => <option value={i} key={i}>{item.name}</option>)}
    </select></label>
    {image && <><img className="booklet-image" src={image.url} crossOrigin="anonymous" alt={data[index].name}
      onError={() => setImageError('The selected artwork could not be displayed.')} />
      {image.pages > 1 && <div className="tool-actions">
        <button className="load-more" disabled={page <= 0} onClick={() => setPage(page - 1)}>Previous page</button>
        <span>{image.page + 1} / {image.pages}</span>
        <button className="load-more" disabled={page + 1 >= image.pages} onClick={() => setPage(page + 1)}>Next page</button>
      </div>}</>}
    {imageError && <p role="alert">{imageError}</p>}
  </section>
}
