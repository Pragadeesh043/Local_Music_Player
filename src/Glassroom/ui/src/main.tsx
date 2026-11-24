import { createRoot } from 'react-dom/client'
import { App } from './App'
import { createBridge } from './bridge'
import { Player } from './player'
import './style.css'

const root = createRoot(document.getElementById('root')!)
void createBridge().then(bridge => root.render(<App player={new Player(bridge)} />)).catch(() => {
  root.render(<p role="alert" className="startup-error">Glassroom could not start. Reopen the player to try again.</p>)
})
