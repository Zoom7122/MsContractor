<script setup>
import MoyskladDuplicatesView from './MoyskladDuplicatesView.vue'

const mockDuplicatesData = {
  fields: ['name', 'email', 'phone'],
  groupLimit: 30,
  itemLimit: 30,
  groupsTruncated: false,
  groups: [
    {
      key: 'ooo-romashka',
      matchCount: 3,
      itemsTruncated: false,
      values: {
        name: 'ООО Ромашка',
        email: 'info@romashka.ru',
        phone: '+79990000000'
      },
      counterparties: [
        {
          id: 'cp-1',
          name: 'ООО Ромашка',
          description: 'Основной контрагент',
          email: 'info@romashka.ru',
          phone: '+79990000000',
          archived: false,
          updatedAt: '2026-06-13 12:00:00',
          syncedAt: '2026-06-13 12:05:00'
        },
        {
          id: 'cp-2',
          name: 'ООО Ромашка дубль',
          description: '',
          email: 'info@romashka.ru',
          phone: '+79990000000',
          archived: true,
          updatedAt: '2026-06-10 09:30:00',
          syncedAt: '2026-06-13 12:05:00'
        },
        {
          id: 'cp-3',
          name: 'Ромашка ООО',
          description: '',
          email: 'info@romashka.ru',
          phone: '+79990000000',
          archived: false,
          updatedAt: '2026-06-09 17:10:00',
          syncedAt: '2026-06-13 12:05:00'
        }
      ]
    },
    {
      key: 'ivanov-phone',
      matchCount: 2,
      itemsTruncated: false,
      values: {
        name: '',
        email: '',
        phone: '+78880000000'
      },
      counterparties: [
        {
          id: 'cp-4',
          name: 'Иванов ИП',
          description: '',
          email: 'ivanov@example.com',
          phone: '+78880000000',
          archived: false,
          updatedAt: '2026-06-11 10:00:00',
          syncedAt: '2026-06-13 12:05:00'
        },
        {
          id: 'cp-5',
          name: 'ИП Иванов',
          description: '',
          email: '',
          phone: '+78880000000',
          archived: false,
          updatedAt: '2026-06-12 08:00:00',
          syncedAt: '2026-06-13 12:05:00'
        }
      ]
    }
  ]
}
</script>

<template>
  <Story title="МойСклад/Дубликаты">
    <Variant title="Список дублей">
      <div class="story-page">
        <MoyskladDuplicatesView :duplicates-data="mockDuplicatesData" />
      </div>
    </Variant>
  </Story>
</template>

<style>
.story-page {
  min-height: 100vh;
  padding: 32px;
  background: #f4f7fb;
}
</style>
