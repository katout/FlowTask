// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// The pages come from ../docs/<locale>/ (see scripts/sync-docs.mjs). Japanese is the original; a page not yet
// translated shows the Japanese text with a notice.
const group = (ja, en, directory, pages) => ({
  label: ja,
  translations: { en },
  items: pages.map((page) => `${directory}/${page}`),
});

export default defineConfig({
  site: 'https://katout.github.io',
  base: '/FlowTask',
  integrations: [
    starlight({
      title: 'FlowTask',
      description: 'Structured-concurrency async game flow for C# (.NET, Unity, Godot)',
      defaultLocale: 'ja',
      locales: {
        ja: { label: '日本語', lang: 'ja' },
        en: { label: 'English', lang: 'en' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/katout/FlowTask' }],
      sidebar: [
        group('はじめに', 'Getting started', 'getting-started', ['introduction', 'installation', 'first-flow']),
        group('ガイド', 'Guide', 'guide', [
          'flows-and-world', 'scopes-and-cancellation', 'composition', 'time-and-clocks', 'signals', 'failures', 'threads',
        ]),
        group('Unity', 'Unity', 'unity', ['setup', 'lifetime', 'bridges', 'samples']),
        group('Godot', 'Godot', 'godot', ['setup', 'lifetime', 'signals', 'physics', 'samples']),
        group('連携', 'Integrations', 'integrations', ['task', 'unitask', 'r3']),
        group('テストと診断', 'Testing and diagnostics', 'tools', ['testing', 'debugging', 'analyzers']),
        group('詳しく', 'In depth', 'advanced', ['execution-model', 'performance', 'design-rationale', 'glossary']),
      ],
    }),
  ],
});
