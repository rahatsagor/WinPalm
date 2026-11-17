<script setup lang="ts">
import { EyeOff, LoaderCircle, Power, RefreshCw, Search, SquarePlus, SquareArrowOutUpRight } from '@lucide/vue'
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref } from 'vue'
import AppIcon from '@/components/AppIcon.vue'
import Segmented from '@/components/Segmented.vue'
import Sheet from '@/components/Sheet.vue'
import { isOpen, loadApps, markQuitting } from '@/lib/apps'
import { notify, send, state } from '@/lib/connection'
import { addTileToCurrentPage, newTile } from '@/lib/deck'
import { haptic } from '@/lib/haptics'
import type { AppEntry, AppOp } from '@/lib/protocol'
import { settings } from '@/lib/settings'

const filter = ref<'running' | 'all'>('running')
const query = ref('')
const search = computed(() => query.value.trim())
const menu = ref<AppEntry | null>(null)

onMounted(() => loadApps(true))
// Kept alive between tabs, so refresh (at most every few seconds) on return.
onActivated(() => loadApps())

const apps = computed(() => {
  const q = search.value.toLowerCase()
  let list = state.apps
  if (q) list = list.filter((a) => a.name.toLowerCase().includes(q))
  else if (filter.value === 'running') list = list.filter(isOpen)
  return [...list].sort((a, b) => a.name.localeCompare(b.name))
})

function act(app: AppEntry, op: AppOp) {
  menu.value = null
  if (!send({ t: 'appAction', id: app.id, op })) {
    notify('Not connected to the PC.', 'warning')
    return
  }
  if (op === 'quit') markQuitting(app.id)
  haptic('light')
}

async function addToDeck(app: AppEntry) {
  menu.value = null
  const page = await addTileToCurrentPage(newTile({ kind: 'app', id: app.id, op: 'toggle' }, app.name), settings.deckPage)
  if (page === null) return
  notify(page ? `Added ${app.name} to “${page}”.` : 'That deck page is full.', page ? 'info' : 'warning')
}

let longTimer: ReturnType<typeof setTimeout> | undefined
let longFired = false
function onDown(app: AppEntry) {
  longFired = false
  clearTimeout(longTimer)
  longTimer = setTimeout(() => {
    longFired = true
    haptic('medium')
    menu.value = app
  }, 500)
}
function onUp() {
  clearTimeout(longTimer)
}
// A hold that outlives the tab would open the menu over another tab.
onDeactivated(onUp)
onBeforeUnmount(onUp)
function onClick(app: AppEntry) {
  if (longFired) return
  act(app, 'toggle')
}
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex flex-col gap-2 px-3 pt-1 pb-2">
      <div class="flex min-h-11 items-center gap-1 rounded-xl bg-surface-3 text-muted">
        <label class="flex min-h-11 min-w-0 flex-1 items-center gap-2 pl-3">
          <Search :size="16" class="shrink-0" aria-hidden="true" />
          <input v-model="query" type="search" class="h-full min-w-0 flex-1 bg-transparent text-[16px] text-text outline-none" placeholder="Search apps on the PC" aria-label="Search apps on the PC" />
        </label>
        <button type="button" class="grid size-11 shrink-0 place-items-center rounded-xl active:bg-surface-2" aria-label="Refresh apps" @click="loadApps(true)"><RefreshCw :size="16" /></button>
      </div>
      <Segmented
        v-if="!search"
        v-model="filter"
        class="py-0!"
        aria-label="Apps to show"
        :options="[
          { value: 'running', label: 'Open' },
          { value: 'all', label: 'All apps' },
        ]"
      />
    </div>

    <div v-if="!state.appsLoaded" class="grid flex-1 place-items-center text-muted"><LoaderCircle class="animate-spin" /></div>
    <div v-else class="no-scrollbar min-h-0 flex-1 overflow-y-auto overscroll-contain px-3 pb-4">
      <div class="grid grid-cols-[repeat(auto-fill,minmax(84px,1fr))] gap-x-2 gap-y-4">
        <button
          v-for="app in apps"
          :key="app.id"
          type="button"
          class="flex flex-col items-center gap-1.5 rounded-2xl p-1.5 transition-transform active:scale-95"
          :aria-label="`${app.name}${isOpen(app) ? ', open' : ''}${app.active ? ', in front' : ''}`"
          @pointerdown="onDown(app)"
          @pointerup="onUp"
          @pointercancel="onUp"
          @pointerleave="onUp"
          @click="onClick(app)"
          @contextmenu.prevent="menu = app"
        >
          <span class="relative">
            <AppIcon :id="app.id" :size="56" :class="app.hidden && 'opacity-50'" />
            <span v-if="app.active" class="absolute -bottom-1 left-1/2 h-1 w-4 -translate-x-1/2 rounded-full bg-accent" />
            <span v-else-if="isOpen(app)" class="absolute -bottom-1 left-1/2 size-1 -translate-x-1/2 rounded-full bg-muted" />
          </span>
          <span class="line-clamp-2 w-full text-center text-[12px] leading-tight">{{ app.name }}</span>
        </button>
      </div>
      <div v-if="apps.length === 0" class="py-12 text-center">
        <p class="text-[15px] text-muted" role="status">
          {{ search ? `No apps match “${search}”.` : filter === 'running' ? 'No apps are open.' : 'No apps found on the PC.' }}
        </p>
        <button v-if="search" type="button" class="empty-action" @click="query = ''">Clear search</button>
        <button v-else-if="filter === 'running'" type="button" class="empty-action" @click="filter = 'all'">Show all apps</button>
      </div>
      <p v-else class="pt-6 text-center text-[12px] text-faint">Tap to show or hide · Touch and hold for more</p>
    </div>

    <Sheet v-if="menu" :title="menu.name" @close="menu = null">
      <div class="flex flex-col gap-1.5 pb-2">
        <button type="button" class="menu-row" @click="act(menu, 'open')"><SquareArrowOutUpRight :size="18" /> {{ menu.running ? 'Bring to front' : 'Open' }}</button>
        <button v-if="menu.running" type="button" class="menu-row" @click="act(menu, 'hide')"><EyeOff :size="18" /> Minimize</button>
        <button type="button" class="menu-row" @click="addToDeck(menu)"><SquarePlus :size="18" /> Add to deck</button>
        <button v-if="menu.running" type="button" class="menu-row text-bad" @click="act(menu, 'quit')"><Power :size="18" /> Quit</button>
      </div>
    </Sheet>
  </div>
</template>

<style scoped>
@reference '../style.css';
.menu-row {
  @apply flex h-12 w-full items-center gap-3 rounded-xl bg-surface-2 px-4 text-left text-[15px];
}
.empty-action {
  @apply mt-3 min-h-11 rounded-xl bg-surface-2 px-4 text-[14px] font-medium text-accent;
}
</style>
