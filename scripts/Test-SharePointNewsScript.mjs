import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/Kiosk.Client/SharePointNews.cs', import.meta.url), 'utf8');
const method = source.indexOf('private static string Script');
const opening = source.indexOf('"""', method);
const closing = source.indexOf('"""', opening + 3);
if (method < 0 || opening < 0 || closing < 0) throw new Error('SharePoint script was not found');
const script = source.slice(opening + 3, closing)
  .replaceAll('SITE', '"https://example.com/sites/news"')
  .replaceAll('COUNT', '6');
const site = new URL('https://example.com/sites/news');

class MockFileReader {
  async readAsDataURL(blob) {
    this.result = 'data:image/png;base64,' + Buffer.from(await blob.arrayBuffer()).toString('base64');
    this.onload();
  }
}

async function run(response, bannerResponse = async () => response, code = script) {
  const messages = [];
  await vm.runInNewContext(code, {
    URL, TextDecoder, Uint8Array, Blob, FileReader: MockFileReader,
    location: site,
    chrome: { webview: { postMessage: value => messages.push(value) } },
    fetch: async target => String(target).includes('/_api/') ? response : bannerResponse(target),
  });
  if (messages.length !== 1) throw new Error('Expected one WebView2 message');
  return JSON.parse(messages[0]);
}

const item = { Title: 'News', Description: 'Text', FileRef: '/sites/news/SitePages/a.aspx', BannerImageUrl: null };
const ok = await run(new Response(JSON.stringify({ value: [item] })));
if (!ok.ok || ok.items.length !== 1 || ok.items[0].url !== 'https://example.com/sites/news/SitePages/a.aspx')
  throw new Error('Normal SharePoint response failed');
const external = await run(new Response(JSON.stringify({ value: [{ ...item, FileRef: 'https://outside.example/a' }] })));
if (!external.ok || external.items.length !== 0) throw new Error('External page URL was accepted');
const declared = await run({ ok: true, headers: { get: () => '4000001' }, body: null });
if (declared.ok || !declared.error.includes('za duża')) throw new Error('Declared oversized metadata was accepted');
const growing = await run(new Response('x'.repeat(4000001)));
if (growing.ok || !growing.error.includes('za duża')) throw new Error('Growing oversized metadata was accepted');
const many = await run(new Response(JSON.stringify({ value: Array(21).fill(item) })));
if (many.ok || !many.error.includes('zbyt wiele')) throw new Error('Excess SharePoint items were accepted');
const banners = Array.from({ length: 3 }, (_, i) => ({
  ...item, FileRef: `/sites/news/SitePages/${i}.aspx`, BannerImageUrl: { Url: `/images/${i}.png` },
}));
const budgetCode = script.replace('const bannerTotalLimit = 16000000', 'const bannerTotalLimit = 100');
const budget = await run(new Response(JSON.stringify({ value: banners })),
  async () => new Response(new Uint8Array(60), { headers: { 'content-length': '60', 'content-type': 'image/png' } }), budgetCode);
if (!budget.ok || budget.items.length !== 3 || !budget.items[0].image.startsWith('data:image/png;base64,') ||
    budget.items[1].image !== '' || budget.items[2].image !== '')
  throw new Error('Aggregate banner budget was not applied');
console.log('Bounded SharePoint script cases passed.');
