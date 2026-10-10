// Copies the Markdown of ../docs/<locale>/ into src/content/docs/<locale>/ for Starlight. The files under docs/ stay
// plain Markdown that reads on GitHub (a '# Title' line, relative '.md' links); this turns the title into front matter,
// points each link at the page's URL on the site, links files outside docs/ to GitHub, and sets the page's edit link.
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = 'https://github.com/katout/FlowTask';
const base = '/FlowTask';
const locales = ['ja', 'en'];
// Links to files outside docs/ (samples, CHANGELOG.md) show the version that the site describes: the tag when the
// release workflow builds it (GITHUB_REF_TYPE is the caller's there too), main otherwise.
const sourceRef = process.env.GITHUB_REF_TYPE === 'tag' ? process.env.GITHUB_REF_NAME : 'main';

const websiteDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const repoDir = path.dirname(websiteDir);
const docsDir = path.join(repoDir, 'docs');
const outDir = path.join(websiteDir, 'src', 'content', 'docs');

async function* markdownFiles(dir) {
  for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* markdownFiles(full);
    else if (entry.name.endsWith('.md')) yield full;
  }
}

const toPosix = (p) => p.split(path.sep).join('/');

// docs/ja/guide/signals.md -> /FlowTask/ja/guide/signals/ ; docs/ja/index.md -> /FlowTask/ja/
function pageUrl(docsRelative) {
  const slug = docsRelative.replace(/\.md$/, '').replace(/(^|\/)index$/, '');
  return `${base}/${slug}${slug.endsWith('/') || slug === '' ? '' : '/'}`.replace(/\/+$/, '/');
}

function rewriteLink(target, sourceFile) {
  if (/^(?:[a-z]+:|#|\/)/i.test(target)) return target;
  const [file, hash = ''] = target.split('#', 2);
  const resolved = path.resolve(path.dirname(sourceFile), decodeURI(file));
  const anchor = hash ? `#${hash}` : '';
  const insideDocs = toPosix(path.relative(docsDir, resolved));
  if (!insideDocs.startsWith('..') && locales.some((l) => insideDocs.startsWith(`${l}/`)) && insideDocs.endsWith('.md')) {
    return pageUrl(insideDocs) + anchor;
  }
  const inRepo = toPosix(path.relative(repoDir, resolved));
  if (inRepo.startsWith('..')) throw new Error(`${sourceFile}: link outside the repository: ${target}`);
  return `${repo}/blob/${sourceRef}/${inRepo}${anchor}`;
}

function transform(text, sourceFile) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  const titleIndex = lines.findIndex((l) => /^# /.test(l));
  if (titleIndex < 0) throw new Error(`${sourceFile}: no '# Title' line`);
  const title = lines[titleIndex].slice(2).trim();
  lines.splice(titleIndex, 1);
  let inFence = false;
  const body = lines
    .map((line) => {
      if (/^\s*(```|~~~)/.test(line)) inFence = !inFence;
      if (inFence) return line;
      // Inline links and images outside code spans: [text](target) and ![alt](target).
      return line.replace(/(`[^`]*`)|(!?\[[^\]]*\])\(([^)\s]+)\)/g, (m, code, label, target) =>
        code ? code : `${label}(${rewriteLink(target, sourceFile)})`);
    })
    .join('\n')
    .replace(/^\n+/, '');
  const edit = `${repo}/edit/main/${toPosix(path.relative(repoDir, sourceFile))}`;
  return `---\ntitle: ${JSON.stringify(title)}\neditUrl: ${JSON.stringify(edit)}\n---\n\n${body}`;
}

await fs.rm(outDir, { recursive: true, force: true });
let count = 0;
for (const locale of locales) {
  const localeDir = path.join(docsDir, locale);
  try { await fs.access(localeDir); } catch { continue; }
  for await (const file of markdownFiles(localeDir)) {
    const target = path.join(outDir, path.relative(docsDir, file));
    await fs.mkdir(path.dirname(target), { recursive: true });
    await fs.writeFile(target, transform(await fs.readFile(file, 'utf8'), file));
    count++;
  }
}
console.log(`sync-docs: ${count} pages`);
