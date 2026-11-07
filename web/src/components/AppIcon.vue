<script setup lang="ts">
import { AppWindow, Calculator, FileText, FolderOpen, Globe, Settings, SquareTerminal } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { iconUrl, state } from '@/lib/connection'

const props = withDefaults(defineProps<{ id: string; size?: number }>(), { size: 48 })
const failed = ref(false)
watch(() => [props.id, props.size, state.sessionKey, state.apps], () => (failed.value = false))

// Useful even when an app has no Windows icon, is on a disconnected drive,
// or is one of Windows' protocol-only built-ins.
const builtins = {
  'builtin:explorer': { icon: FolderOpen, color: '#e5a323' },
  'builtin:browser': { icon: Globe, color: '#168dcc' },
  'builtin:terminal': { icon: SquareTerminal, color: '#749b87' },
  'builtin:notepad': { icon: FileText, color: '#42a5e4' },
  'builtin:calculator': { icon: Calculator, color: '#858eda' },
  'builtin:settings': { icon: Settings, color: '#8495ab' },
}
const fallback = computed(() => builtins[props.id as keyof typeof builtins] ?? {
  icon: AppWindow,
  color: ['#428ed3', '#9d77d5', '#3b9c87', '#c47c4d', '#c96792'][Math.abs([...props.id].reduce((hash, c) => Math.imul(hash, 31) + c.charCodeAt(0) | 0, 0)) % 5]!,
})
</script>

<template>
  <img
    v-if="!failed && id && state.sessionKey"
    :src="iconUrl(id, size)"
    :width="size"
    :height="size"
    alt=""
    loading="lazy"
    decoding="async"
    draggable="false"
    class="pointer-events-none object-contain select-none"
    @error="failed = true"
  />
  <span v-else class="grid shrink-0 place-items-center rounded-[22%]" :style="{ width: `${size}px`, height: `${size}px`, color: fallback.color, background: `color-mix(in srgb, ${fallback.color} 16%, var(--surface-2))` }">
    <component :is="fallback.icon" :size="size * 0.6" :stroke-width="1.65" />
  </span>
</template>
