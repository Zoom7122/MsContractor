<script setup>
/**
 * Small line-art illustrations for empty/error states.
 * Colors come from design tokens through CSS classes.
 */
defineProps({
  name: { type: String, default: 'search' }
})
</script>

<template>
  <svg class="empty-illustration" viewBox="0 0 120 90" fill="none" aria-hidden="true" focusable="false">
    <ellipse class="ei-ground" cx="60" cy="82" rx="40" ry="4" />

    <!-- search: sheet with lines and a magnifier -->
    <g v-if="name === 'search'">
      <rect class="ei-sheet" x="24" y="12" width="54" height="62" rx="5" />
      <rect class="ei-line" x="32" y="22" width="28" height="4" rx="2" />
      <rect class="ei-line" x="32" y="32" width="38" height="4" rx="2" />
      <rect class="ei-line" x="32" y="42" width="22" height="4" rx="2" />
      <rect class="ei-line" x="32" y="52" width="30" height="4" rx="2" />
      <circle class="ei-soft" cx="78" cy="52" r="11" />
      <circle class="ei-stroke-accent" cx="78" cy="52" r="12" stroke-width="4" />
      <path class="ei-stroke-accent" d="M87 61l9 9" stroke-width="5" stroke-linecap="round" />
    </g>

    <!-- duplicates: two similar cards with an "equal" badge -->
    <g v-else-if="name === 'duplicates'">
      <rect class="ei-sheet" x="14" y="16" width="52" height="40" rx="5" />
      <circle class="ei-soft" cx="28" cy="30" r="6" />
      <rect class="ei-line" x="38" y="26" width="20" height="4" rx="2" />
      <rect class="ei-line" x="38" y="34" width="14" height="4" rx="2" />
      <rect class="ei-line" x="22" y="44" width="36" height="4" rx="2" />
      <rect class="ei-sheet ei-sheet--accent" x="50" y="32" width="52" height="40" rx="5" />
      <circle class="ei-accent" cx="64" cy="46" r="6" />
      <rect class="ei-line" x="74" y="42" width="20" height="4" rx="2" />
      <rect class="ei-line" x="74" y="50" width="14" height="4" rx="2" />
      <rect class="ei-line" x="58" y="60" width="36" height="4" rx="2" />
      <circle class="ei-accent" cx="100" cy="26" r="9" />
      <path d="M96 24h8M96 28.5h8" stroke="#fff" stroke-width="2" stroke-linecap="round" />
    </g>

    <!-- documents: stack of sheets -->
    <g v-else-if="name === 'documents'">
      <rect class="ei-line" x="42" y="22" width="46" height="54" rx="5" />
      <rect class="ei-sheet" x="36" y="17" width="46" height="54" rx="5" />
      <rect class="ei-sheet" x="30" y="12" width="46" height="54" rx="5" />
      <rect class="ei-accent" x="38" y="21" width="16" height="4" rx="2" />
      <rect class="ei-line" x="38" y="31" width="30" height="4" rx="2" />
      <rect class="ei-line" x="38" y="39" width="24" height="4" rx="2" />
      <rect class="ei-line" x="38" y="47" width="30" height="4" rx="2" />
      <rect class="ei-line" x="38" y="55" width="18" height="4" rx="2" />
    </g>

    <!-- queue: rows with a clock -->
    <g v-else-if="name === 'queue'">
      <rect class="ei-sheet" x="18" y="16" width="66" height="14" rx="4" />
      <circle class="ei-accent" cx="27" cy="23" r="3" />
      <rect class="ei-line" x="35" y="21" width="36" height="4" rx="2" />
      <rect class="ei-sheet" x="18" y="36" width="66" height="14" rx="4" />
      <circle class="ei-soft" cx="27" cy="43" r="3" />
      <rect class="ei-line" x="35" y="41" width="28" height="4" rx="2" />
      <rect class="ei-sheet" x="18" y="56" width="66" height="14" rx="4" />
      <circle class="ei-line" cx="27" cy="63" r="3" />
      <rect class="ei-line" x="35" y="61" width="32" height="4" rx="2" />
      <circle class="ei-sheet ei-sheet--accent" cx="92" cy="62" r="13" />
      <path class="ei-stroke-accent" d="M92 55v7l5 3" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" />
    </g>

    <!-- history: clock with a circular arrow -->
    <g v-else-if="name === 'history'">
      <path class="ei-stroke-soft" d="M30 44a30 30 0 1 1 9 21.2" stroke-width="4" stroke-linecap="round" />
      <path class="ei-soft" d="M26 60l14 1-3 12z" />
      <circle class="ei-sheet" cx="60" cy="44" r="22" />
      <path class="ei-stroke-accent" d="M60 32v12l8 6" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />
      <circle class="ei-accent" cx="60" cy="44" r="2.5" />
    </g>

    <!-- sync: circular arrows around a sheet -->
    <g v-else-if="name === 'sync'">
      <path class="ei-stroke-accent" d="M34 40a27 27 0 0 1 46-16" stroke-width="4" stroke-linecap="round" />
      <path class="ei-accent" d="M84 14l1 16-15-4z" />
      <path class="ei-stroke-soft" d="M86 50a27 27 0 0 1-46 16" stroke-width="4" stroke-linecap="round" />
      <path class="ei-soft" d="M36 76l-1-16 15 4z" />
      <rect class="ei-sheet" x="48" y="30" width="24" height="30" rx="4" />
      <rect class="ei-line" x="53" y="37" width="14" height="3" rx="1.5" />
      <rect class="ei-line" x="53" y="44" width="10" height="3" rx="1.5" />
      <rect class="ei-line" x="53" y="51" width="14" height="3" rx="1.5" />
    </g>

    <!-- error: sheet with a warning sign -->
    <g v-else-if="name === 'error'">
      <rect class="ei-sheet" x="28" y="12" width="64" height="62" rx="6" />
      <path class="ei-danger-soft" d="M60 24l22 38H38z" stroke-width="3" stroke-linejoin="round" />
      <rect class="ei-danger" x="58.5" y="36" width="3" height="14" rx="1.5" />
      <circle class="ei-danger" cx="60" cy="55.5" r="2" />
    </g>

    <!-- unavailable: dashed card with a pause mark -->
    <g v-else-if="name === 'unavailable'">
      <rect class="ei-sheet ei-sheet--dashed" x="22" y="14" width="62" height="56" rx="6" />
      <rect class="ei-line" x="31" y="25" width="30" height="4" rx="2" />
      <rect class="ei-line" x="31" y="35" width="40" height="4" rx="2" />
      <rect class="ei-line" x="31" y="45" width="24" height="4" rx="2" />
      <circle class="ei-sheet" cx="86" cy="62" r="13" />
      <rect class="ei-muted" x="81" y="56" width="3.5" height="12" rx="1.5" />
      <rect class="ei-muted" x="87.5" y="56" width="3.5" height="12" rx="1.5" />
    </g>

    <!-- select: list with a pointer -->
    <g v-else-if="name === 'select'">
      <rect class="ei-sheet" x="16" y="14" width="60" height="16" rx="4" />
      <rect class="ei-line" x="24" y="20" width="34" height="4" rx="2" />
      <rect class="ei-sheet ei-sheet--selected" x="16" y="35" width="60" height="16" rx="4" />
      <rect class="ei-soft-strong" x="24" y="41" width="40" height="4" rx="2" />
      <rect class="ei-sheet" x="16" y="56" width="60" height="16" rx="4" />
      <rect class="ei-line" x="24" y="62" width="28" height="4" rx="2" />
      <path class="ei-accent ei-cursor" d="M74 40v26l6.5-6 5 10.5 4.5-2-5-10.3H94z" />
    </g>

    <!-- success: check mark -->
    <g v-else-if="name === 'success'">
      <circle class="ei-success-soft" cx="60" cy="44" r="26" />
      <circle class="ei-sheet" cx="60" cy="44" r="18" />
      <path class="ei-stroke-success" d="M51 44.5l6 6 12-13" stroke-width="4" stroke-linecap="round" stroke-linejoin="round" />
    </g>
  </svg>
</template>

<style scoped src="../../styles/components/ui/empty-illustration.css"></style>
