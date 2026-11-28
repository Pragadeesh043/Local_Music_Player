import { useCallback } from 'react'
import { Player, useResource } from './player'
import type { Notify } from './NowPlaying'

const repository = 'https://github.com/Pragadeesh043/MP4A'
const links = [['Repository', 'repository', ''], ['Releases', 'releases', '/releases'], ['Issues', 'issues', '/issues']] as const

export function Settings({ player, notify, open }: {
  player: Player; notify: Notify; open: (tool: 'Output settings' | 'DSP' | 'Keyboard shortcuts') => void
}) {
  const load = useCallback(() => player.bridge.request('appInfo', {}), [player])
  const { data, error } = useResource(load)
  return <section className="settings-page" aria-labelledby="settings-title">
    <h1 id="settings-title">Settings</h1>
    <section className="tool-card">
      <h2>About MP4A</h2>
      <dl className="tool-details">
        <dt>Build version</dt><dd>{data?.version ?? (error ? 'Unavailable' : 'Reading build version…')}</dd>
        <dt>GitHub repository</dt><dd>{repository}</dd>
      </dl>
      <div className="tool-actions">{links.map(([label, page, suffix]) =>
        <a className="load-more" key={page} href={repository + suffix} onClick={event => {
          event.preventDefault()
          notify(player.send('openRepository', { page }))
        }}>{label}</a>)}</div>
    </section>
    <section className="tool-card">
      <h2>Player preferences</h2>
      <div className="tool-actions">{(['Output settings', 'DSP', 'Keyboard shortcuts'] as const).map(tool =>
        <button type="button" className="load-more" key={tool} onClick={() => open(tool)}>{tool}</button>)}</div>
    </section>
  </section>
}
