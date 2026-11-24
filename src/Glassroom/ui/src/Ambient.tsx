import { AnimatePresence, motion } from 'motion/react'
import { useEffect, useState } from 'react'
import type { CSSProperties } from 'react'
import { loadArtwork } from './artworkResource'

const fallback = ['72 72 72', '38 38 38', '55 55 55']
type Palette = { colors: string[]; backdrop?: string }
const defaultPalette: Palette = { colors: fallback }
const palettes = new Map<string, Promise<Palette>>()

async function extractPalette(url: string) {
  const image = new Image()
  image.crossOrigin = 'anonymous'
  image.src = url
  await image.decode()
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = 24
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) return defaultPalette
  context.drawImage(image, 0, 0, 24, 24)
  const { data } = context.getImageData(0, 0, 24, 24)
  const buckets = new Map<string, { rgb: number[]; count: number; weight: number }>()
  for (let offset = 0; offset < data.length; offset += 4) {
    const rgb = [data[offset], data[offset + 1], data[offset + 2]]
    const max = Math.max(...rgb)
    if (data[offset + 3] < 128 || max < 15) continue
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
  const colors = fallback.map((color, index) => {
    const rgb = selected[index] ?? selected[0]
    if (!rgb) return color
    const [red, green, blue] = rgb
    const luminance = red * 0.2126 + green * 0.7152 + blue * 0.0722
    const shade = Math.round(luminance * 0.5 + 24)
    return `${shade} ${shade} ${shade}`
  })
  const thumbnail = document.createElement('canvas')
  thumbnail.width = thumbnail.height = 8
  const small = thumbnail.getContext('2d')
  if (!small) return { colors }
  small.drawImage(canvas, 0, 0, 8, 8)
  return { colors, backdrop: thumbnail.toDataURL() }
}

export function Ambient({ artwork }: { artwork?: string }) {
  const [layer, setLayer] = useState({ source: '', palette: defaultPalette })
  useEffect(() => {
    if (!artwork) return
    let active = true
    void loadArtwork(artwork).then(async url => {
      if (!active) return
      if (!url) {
        setLayer({ source: '', palette: defaultPalette })
        return
      }
      let cached = palettes.get(url)
      if (!cached) {
        if (palettes.size >= 128) palettes.delete(palettes.keys().next().value!)
        cached = extractPalette(url).catch(() => {
          palettes.delete(url)
          return defaultPalette
        })
        palettes.set(url, cached)
      }
      const palette = await cached
      if (active) setLayer({ source: url, palette })
    })
    return () => { active = false }
  }, [artwork])

  const visible = artwork ? layer : { source: '', palette: defaultPalette }
  return <div className="ambient" aria-hidden="true">
    <AnimatePresence initial={false}>
      <AmbientLayer key={visible.source} palette={visible.palette} />
    </AnimatePresence>
  </div>
}

function AmbientLayer({ palette: { colors, backdrop } }: { palette: Palette }) {
  const style = {
    '--ambient-a': colors[0],
    '--ambient-b': colors[1],
    '--ambient-c': colors[2],
    '--ambient-art': backdrop ? 'url(' + backdrop + ')' : 'none',
  } as CSSProperties
  return <motion.div className="ambient-layer" style={style} initial={{ opacity: 0 }}
    animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: 0.6 }} />
}
