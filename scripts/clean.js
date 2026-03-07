#!/usr/bin/env bun
import { readdirSync, rmSync } from 'node:fs'
import { join } from 'node:path'

const rootDir = join(import.meta.dir, '..')
const targetNames = new Set(['bin', 'obj', 'TestResults'])
const ignoredNames = new Set(['.git', '.idea', 'refs'])

for (const rootName of ['src', 'tests']) {
  removeBuildOutputs(join(rootDir, rootName))
}

function removeBuildOutputs(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (!entry.isDirectory()) {
      continue
    }

    if (ignoredNames.has(entry.name)) {
      continue
    }

    const fullPath = join(directory, entry.name)
    if (targetNames.has(entry.name)) {
      rmSync(fullPath, { force: true, recursive: true })
      continue
    }

    removeBuildOutputs(fullPath)
  }
}
