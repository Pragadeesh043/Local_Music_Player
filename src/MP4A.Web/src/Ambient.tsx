import { AnimatePresence, motion } from 'motion/react'
import { useEffect, useState } from 'react'
import type { CSSProperties } from 'react'
import { loadArtwork } from './artworkResource'

const fallback = ['67 142 177', '165 92 56', '93 76 139']
const palettes = new Map<string, Promise<string[]>>()

async function extractPalette(url: string) {
  const image = new Image()
  image.crossOrigin = 'anonymous'
  image.src = url
  await image.decode()
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = 24
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) return fallback
  context.drawImage(image, 0, 0, 24, 24)
  const { data } = context.getImageData(0, 0, 24, 24)
  const buckets = new Map<string, { rgb: number[]; count: number; weight: number }>()
  for (let offset = 0; offset < data.length; offset += 4) {
    const rgb = [data[offset], data[offset + 1], data[offset + 2]]
    const max = Math.max(...rgb)
    if (data[offset + 3] < 128 || max < 24) continue
    const chroma = (max - Math.min(...rgb)) / 255
    const key = rgb.map(channel => Math.floor(channel / 48)).join(',')
    const bucket = buckets.get(key) ?? { rgb: [0, 0, 0], count: 0, weight: 0 }
    rgb.forEach((channel, index) => { bucket.rgb[index] += channel })
    bucket.count += 1
    bucket.weight += 0.16 + chroma * 2.5
    buckets.set(key, bucket)
  }
  const sorted = [...buckets.values()].sort((a, b) => b.weight - a.weight)
    .map(({ rgb, count }) => rgb.map(channel => channel / count))
  const selected: number[][] = []
  for (const rgb of sorted) {
    if (selected.every(color => Math.hypot(...rgb.map((value, index) => value - color[index])) > 54)) {
      selected.push(rgb)
      if (selected.length === 3) break
    }
  }
  return fallback.map((color, index) => (selected[index] ?? selected[0])?.map(Math.round).join(' ') ?? color)
}

const defaultLayer = { source: '', colors: fallback }

export function useAmbient(artwork?: string) {
  const [layer, setLayer] = useState(defaultLayer)
  useEffect(() => {
    if (!artwork) {
      setLayer(defaultLayer)
      return
    }
    const request = new AbortController()
    void loadArtwork(artwork, true, request.signal).then(async url => {
      if (request.signal.aborted) return
      if (!url) {
        setLayer(defaultLayer)
        return
      }
      let cached = palettes.get(url)
      if (!cached) {
        if (palettes.size >= 128) palettes.delete(palettes.keys().next().value!)
        cached = extractPalette(url).catch(() => {
          palettes.delete(url)
          return fallback
        })
        palettes.set(url, cached)
      }
      const colors = await cached
      if (!request.signal.aborted) setLayer({ source: url, colors })
    }).catch(() => { if (!request.signal.aborted) setLayer(defaultLayer) })
    return () => request.abort()
  }, [artwork])

  return artwork ? layer : defaultLayer
}

export function Ambient({ layer }: { layer: typeof defaultLayer }) {
  return <div className="ambient" aria-hidden="true">
    <AnimatePresence initial={false}>
      <AmbientLayer key={layer.source} colors={layer.colors} />
    </AnimatePresence>
  </div>
}

function AmbientLayer({ colors }: { colors: string[] }) {
  const style = {
    '--ambient-a': colors[0],
    '--ambient-b': colors[1],
    '--ambient-c': colors[2],
  } as CSSProperties
  return <motion.div className="ambient-layer" style={style} initial={{ opacity: 0 }}
    animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: 0.6 }} />
}
