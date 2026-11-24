import { useEffect, useLayoutEffect, useRef, useState } from 'react'

type Series = { values: number[]; horizontal?: number[]; secondary?: boolean }

export function Plot({ label, points }: { label: string; points: Series[] }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [size, setSize] = useState({ width: 0, height: 0 })
  useLayoutEffect(() => {
    const element = canvas.current
    if (!element) return
    const observer = new ResizeObserver(([entry]) => setSize({ width: entry.contentRect.width, height: entry.contentRect.height }))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const element = canvas.current
    const context = element?.getContext('2d')
    if (!element || !context || !size.width || !size.height) return
    const scale = devicePixelRatio || 1
    const width = Math.floor(size.width * scale)
    const height = Math.floor(size.height * scale)
    if (element.width !== width || element.height !== height) { element.width = width; element.height = height }
    const style = getComputedStyle(element)
    context.clearRect(0, 0, width, height)
    context.strokeStyle = style.getPropertyValue('--line')
    context.lineWidth = scale
    context.beginPath()
    context.moveTo(0, height / 2); context.lineTo(width, height / 2)
    context.moveTo(width / 2, 0); context.lineTo(width / 2, height)
    context.stroke()
    for (const { values, horizontal, secondary } of points) {
      context.strokeStyle = style.getPropertyValue(secondary ? '--muted' : '--accent')
      context.lineWidth = 1.4 * scale
      context.beginPath()
      values.forEach((value, index) => {
        const x = (horizontal?.[index] ?? index / Math.max(1, values.length - 1)) * width
        const y = Math.min(1, Math.max(0, value)) * height
        if (index === 0) context.moveTo(x, y)
        else context.lineTo(x, y)
      })
      context.stroke()
    }
  }, [points, size])

  return <figure className="tool-plot"><figcaption>{label}</figcaption><canvas ref={canvas} role="img" aria-label={label} /></figure>
}
