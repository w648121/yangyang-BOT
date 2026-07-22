import { readFile } from "node:fs/promises"
import { tool } from "@opencode-ai/plugin"

const normalize = (value) => String(value ?? "")
  .trim()
  .toLowerCase()
  .replaceAll(" ", "_")
  .replaceAll("-", "_")
  .replace(/[^a-z0-9_]/g, "")

const unique = (values) => [...new Set((values ?? []).map(normalize).filter(Boolean))]

const scoreCandidate = (item, query) => {
  const emotions = item.emotions ?? {}
  const tags = new Set((item.semanticTags ?? []).map(normalize))
  const intents = new Set((item.intentTags ?? []).map(normalize))
  const components = []
  const emotionTotal = query.emotions.reduce((sum, value) => sum + value.weight, 0)
  if (emotionTotal > 0) {
    const covered = query.emotions.reduce(
      (sum, value) => sum + value.weight * Math.max(0, Math.min(1, Number(emotions[value.label] ?? 0))), 0)
    components.push([0.60, covered / emotionTotal])
  }
  if (query.semanticTags.length > 0) {
    const matched = query.semanticTags.filter(value => tags.has(value))
    components.push([0.25, matched.length / query.semanticTags.length])
  }
  if (query.intentTags.length > 0) {
    const matched = query.intentTags.filter(value => intents.has(value))
    components.push([0.15, matched.length / query.intentTags.length])
  }
  if (components.length === 0) return null
  const activeWeight = components.reduce((sum, value) => sum + value[0], 0)
  const score = components.reduce((sum, value) => sum + value[0] * value[1], 0) / activeWeight
  return {
    stickerId: item.stickerId,
    score: Number(score.toFixed(4)),
    matchedEmotions: query.emotions.filter(value => Number(emotions[value.label] ?? 0) > 0.05).map(value => value.label),
    matchedTags: query.semanticTags.filter(value => tags.has(value)),
    matchedIntents: query.intentTags.filter(value => intents.has(value)),
    source: item,
  }
}

const executeSearch = async (args) => {
  const path = process.env.HIME_STICKER_CATALOG_PATH
  if (!path) return JSON.stringify({ decision: "text-only", reason: "catalog-unavailable", stickers: [] })
  const catalog = JSON.parse(await readFile(path, "utf8"))
  const query = {
    emotions: (args.emotions ?? [])
      .map(value => ({ label: normalize(value.label), weight: Math.max(0, Math.min(1, Number(value.weight ?? 0))) }))
      .filter(value => value.label && value.weight > 0),
    semanticTags: unique(args.semanticTags),
    intentTags: unique(args.intentTags),
  }
  const count = Math.max(1, Math.min(3, Number(args.count ?? 1)))
  const strong = Number(catalog.strongMatchThreshold ?? 0.72)
  const partial = Number(catalog.partialMatchThreshold ?? 0.55)
  const primaryEmotion = [...query.emotions].sort((a, b) => b.weight - a.weight)[0]?.label
  const primaryTag = query.semanticTags[0]
  const ranked = (catalog.stickers ?? [])
    .map(item => scoreCandidate(item, query))
    .filter(Boolean)
    .sort((a, b) => b.score - a.score || a.stickerId.localeCompare(b.stickerId))

  const selected = []
  for (const candidate of ranked) {
    const primaryMatched = primaryEmotion
      ? candidate.matchedEmotions.includes(primaryEmotion)
      : primaryTag && candidate.matchedTags.includes(primaryTag)
    if (candidate.score >= strong || (candidate.score >= partial && primaryMatched)) {
      selected.push({ ...candidate, matchLevel: candidate.score >= strong ? "strong" : "partial" })
      if (selected.length >= count) break
    }
  }

  const used = new Set(selected.map(value => value.stickerId))
  const neutralTags = new Set((catalog.neutralFallbackTags ?? ["neutral", "calm", "gentle", "expressionless", "quiet"]).map(normalize))
  if (selected.length < count) {
    const neutral = ranked.filter(candidate => {
      if (used.has(candidate.stickerId)) return false
      const item = candidate.source
      if (Number(item.emotions?.neutral ?? 0) >= 0.35) return true
      return [...(item.semanticTags ?? []), ...(item.intentTags ?? [])].map(normalize).some(value => neutralTags.has(value))
    })
    for (const candidate of neutral) {
      selected.push({ ...candidate, score: 0, matchLevel: "neutral-fallback" })
      used.add(candidate.stickerId)
      if (selected.length >= count) break
    }
  }

  const stickers = selected.map(({ source, ...candidate }) => candidate)
  return JSON.stringify({
    decision: stickers.length > 0 ? "use-sticker-id" : "text-only",
    stickers,
    instruction: stickers.length > 0
      ? `Use only these exact final markers: ${stickers.map(value => `[sticker-id:${value.stickerId}]`).join(" ")}`
      : "No safe match exists. Send text only and do not invent a sticker id.",
  })
}

export const HimeStickerSearchPlugin = async () => ({
  tool: {
    hime_sticker_search: tool({
      description: "Search Hime's approved local sticker catalog by independent emotions, visual semantics, and conversational intent. Returns safe sticker IDs only and falls back to calm/neutral when match quality is low.",
      args: {
        emotions: tool.schema.array(tool.schema.object({
          label: tool.schema.string(),
          weight: tool.schema.number().min(0).max(1),
        })).optional(),
        semanticTags: tool.schema.array(tool.schema.string()).optional(),
        intentTags: tool.schema.array(tool.schema.string()).optional(),
        count: tool.schema.number().int().min(1).max(3).optional(),
      },
      async execute(args) {
        try {
          return await executeSearch(args)
        } catch (error) {
          return JSON.stringify({ decision: "text-only", reason: "tool-error", stickers: [], error: String(error?.message ?? error) })
        }
      },
    }),
  },
})
