<template>
  <section class="documents-history-placeholder">
    <div class="documents-history-placeholder__panel">
      <span class="documents-history-placeholder__kicker">История документов</span>
      <h1>Раздел готовится</h1>
      <p>
        Здесь будет отдельный журнал изменений документов с фильтрами и деталями по связям.
        Навигация уже подготовлена, чтобы интерфейс не выглядел пустым.
      </p>
    </div>
  </section>
</template>

<style scoped>
.documents-history-placeholder {
  min-height: 100%;
  padding: 24px 32px 32px;
  background:
    radial-gradient(circle at top left, rgba(45, 108, 223, 0.12), transparent 30%),
    #f4f7fb;
}

.documents-history-placeholder__panel {
  max-width: 760px;
  padding: 28px 30px;
  background: linear-gradient(135deg, #ffffff, #f8fbff);
  border: 1px solid #dfe7f3;
  border-radius: 18px;
  box-shadow: 0 14px 34px rgba(15, 35, 80, 0.08);
}

.documents-history-placeholder__kicker {
  display: inline-flex;
  margin-bottom: 10px;
  color: #335c99;
  font-size: 12px;
  font-weight: 800;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.documents-history-placeholder__panel h1 {
  margin: 0 0 10px;
  color: #0f1b3d;
  font-size: 30px;
  font-weight: 800;
}

.documents-history-placeholder__panel p {
  margin: 0;
  color: #64759b;
  font-size: 15px;
  line-height: 1.6;
}
</style>
