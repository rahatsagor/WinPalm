<script setup lang="ts" generic="T extends string">
import { computed } from 'vue'

const model = defineModel<T>({ required: true })
const props = defineProps<{ options: { value: T; label: string }[]; label?: string; ariaLabel?: string }>()
const selectedIndex = computed(() => Math.max(0, props.options.findIndex((option) => option.value === model.value)))

function onKey(event: KeyboardEvent, index: number) {
  if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return
  let next: number
  if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (index + 1) % props.options.length
  else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (index - 1 + props.options.length) % props.options.length
  else if (event.key === 'Home') next = 0
  else if (event.key === 'End') next = props.options.length - 1
  else return
  event.preventDefault()
  model.value = props.options[next]!.value
  const buttons = (event.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLButtonElement>('[role="radio"]')
  buttons?.[next]?.focus()
}
</script>

<template>
  <div class="py-2">
    <div v-if="label" class="mb-2 text-[15px]">{{ label }}</div>
    <div class="flex rounded-xl bg-surface-3 p-0.5" role="radiogroup" :aria-label="label || ariaLabel">
      <button
        v-for="(option, index) in options"
        :key="option.value"
        type="button"
        role="radio"
        :aria-checked="model === option.value"
        :tabindex="selectedIndex === index ? 0 : -1"
        class="min-h-11 flex-1 rounded-[10px] px-2 py-1.5 text-[13px] font-medium transition-colors"
        :class="model === option.value ? 'bg-surface text-text shadow-sm' : 'text-muted'"
        @click="model = option.value"
        @keydown="onKey($event, index)"
      >
        {{ option.label }}
      </button>
    </div>
  </div>
</template>
