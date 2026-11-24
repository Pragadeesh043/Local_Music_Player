import { useEffect, useRef } from 'react'
import type { ReactNode } from 'react'
import { X } from 'lucide-react'
import { IconButton } from './NowPlaying'

export function ToolDialog({ title, close, children }: { title: string; close: () => void; children: ReactNode }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = dialog.current
    element?.showModal()
    return () => { element?.close() }
  }, [])
  return <dialog ref={dialog} className="tool-dialog glass" aria-label={title} onCancel={event => { event.preventDefault(); close() }}>
    <header className="tool-header"><h2>{title}</h2><IconButton label={'Close ' + title} action={close}><X size={19} /></IconButton></header>
    <div className="tool-content">{children}</div>
  </dialog>
}
