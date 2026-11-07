<script setup lang="ts">
import { ArrowBigUp, ArrowBigUpDash, ArrowDown, ArrowLeft, ArrowRight, ArrowRightToLine, ArrowUp, CornerDownLeft, Delete } from '@lucide/vue'
import { Key } from '@/lib/keys'
import type { Component } from 'vue'

defineProps<{ code: number; fallback?: string }>()

const icons: Record<number, Component> = {
  [Key.Shift]: ArrowBigUp, [Key.RightShift]: ArrowBigUp,
  [Key.CapsLock]: ArrowBigUpDash, [Key.Tab]: ArrowRightToLine,
  [Key.Return]: CornerDownLeft, [Key.Delete]: Delete, [Key.ForwardDelete]: Delete,
  [Key.Left]: ArrowLeft, [Key.Right]: ArrowRight, [Key.Up]: ArrowUp, [Key.Down]: ArrowDown,
}
</script>

<template>
  <svg v-if="code === Key.Command || code === Key.RightCommand" class="key-glyph" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M2 3h9v8H2zm11 0h9v8h-9zM2 13h9v8H2zm11 0h9v8h-9z" />
  </svg>
  <component :is="icons[code]" v-else-if="icons[code]" class="key-glyph" :class="code === Key.ForwardDelete && 'rotate-180'" :stroke-width="1.7" aria-hidden="true" />
  <span v-else>{{ fallback }}</span>
</template>

<style scoped>
.key-glyph { width: 1.15em; height: 1.15em; flex-shrink: 0; }
</style>
