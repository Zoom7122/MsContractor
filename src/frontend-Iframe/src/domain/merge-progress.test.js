import assert from 'node:assert/strict'
import test from 'node:test'

import { buildJobStages, jobProgress, STEP_STATUS } from './merge.js'

test('shows stopped operations explicitly and counts them as terminal progress', () => {
  const job = {
    status: 'failed',
    operations: [
      { type: 'discover_documents', status: 'completed' },
      { type: 'update_main_counterparty', status: 'failed' },
      { type: 'change_document_counterparties', status: 'no_continue' },
      { type: 'recreate_salesreturns', status: 'no_continue' },
      { type: 'recreate_purchasereturns', status: 'no_continue' },
      { type: 'recreate_factureins', status: 'no_continue' },
      { type: 'recreate_factureouts', status: 'no_continue' },
      { type: 'archive_duplicate', status: 'no_continue' }
    ]
  }

  const stages = buildJobStages(job)

  assert.equal(STEP_STATUS.no_continue.label, 'Не запускалась')
  assert.equal(stages.find((stage) => stage.key === 'documents').status, 'no_continue')
  assert.equal(stages.find((stage) => stage.key === 'recreate').status, 'no_continue')
  assert.equal(stages.find((stage) => stage.key === 'archive').status, 'no_continue')
  assert.equal(jobProgress(job), 100)
})
