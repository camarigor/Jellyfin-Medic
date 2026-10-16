/*
 * Animated Library Posters for Jellyfin web (10.10 / 10.11)
 * Served by the Animated Posters plugin; settings come from window.AnimatedPostersConfig.
 *
 * For every library card (Home > My Media, and the Libraries view) it pulls a
 * random set of items from that library and crossfades through their
 * backdrops with a slow pan/zoom. The library's own artwork can stay in the
 * rotation, be blended on top, or be dropped. Pure client side, no server
 * changes; remove the script to go back to stock.
 */
(function () {
  'use strict';

  // ---------------------------------------------------------------- settings
  const CONFIG = {
    interval: 7000,          // ms each image stays up
    fade: 1400,              // ms crossfade
    itemsPerLibrary: 24,     // random items fetched per library
    refreshMinutes: 30,      // re-roll the random pool this often
    kenBurns: true,          // slow zoom/pan on each image
    showLogos: true,         // draw the item's clear-logo (if it has one) over the backdrop
    // What to do with the library's existing artwork (your synthwave covers):
    //   'slide'  - keep it in the rotation, shown every `originalEvery` slides and first on load
    //   'screen' - blend it over every image (black areas go transparent; great for neon-on-black art)
    //   'none'   - replace it entirely
    originalArt: 'slide',
    originalEvery: 4,
    screenOpacity: 0.55,     // only used for 'screen'
    // Library names (as shown on the card) to leave static, e.g. ['Live TV']
    skipLibraries: [],
    // Per-library overrides by name, e.g. { 'Anime': { originalArt: 'screen' } }
    perLibrary: {},
    hideText: false,         // hide the library name under each card
    hideBorder: false,       // remove card borders, rounded corners and shadows
    debug: false
  };
  Object.assign(CONFIG, window.AnimatedPostersConfig || {});
  if (CONFIG.enabled === false) return;

  // ------------------------------------------------------------------ helpers
  const log = (...a) => CONFIG.debug && console.log('[animated-posters]', ...a);
  const reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const pools = new Map();      // libraryId -> { at, items: Promise<[{bg, logo}]> }
  const STYLE_ID = 'animated-posters-style';

  function injectStyle() {
    if (document.getElementById(STYLE_ID)) return;
    const s = document.createElement('style');
    s.id = STYLE_ID;
    s.textContent = `
      .ap-host { overflow: hidden; }
      .ap-wrap { position:absolute; inset:0; pointer-events:none; border-radius:inherit; overflow:hidden; z-index:0; }
      .ap-slide { position:absolute; inset:0; background-size:cover; background-position:center;
                  opacity:0; transition: opacity ${CONFIG.fade}ms ease-in-out; will-change: opacity, transform; }
      .ap-slide.ap-on { opacity:1; }
      .ap-slide.ap-kb { animation: ap-kb ${CONFIG.interval + CONFIG.fade * 2}ms linear forwards; }
      .ap-slide.ap-kb.ap-rev { animation-name: ap-kb-rev; }
      @keyframes ap-kb     { from { transform: scale(1.02) translate(0,0); }      to { transform: scale(1.14) translate(-2%,-1.5%); } }
      @keyframes ap-kb-rev { from { transform: scale(1.14) translate(-2%,1%); }   to { transform: scale(1.02) translate(1%,0); } }
      .ap-logo { position:absolute; left:6%; bottom:8%; width:45%; height:28%; background-size:contain;
                 background-repeat:no-repeat; background-position:left bottom; filter: drop-shadow(0 2px 6px rgba(0,0,0,.8)); }
      .ap-shade { position:absolute; inset:0; background:linear-gradient(to top, rgba(0,0,0,.55), transparent 55%); }
      .ap-screen { position:absolute; inset:0; background-size:cover; background-position:center;
                   mix-blend-mode:screen; pointer-events:none; }
      .ap-notext .cardFooter { display:none !important; }
      .ap-noborder .cardBox, .ap-noborder .cardScalable, .ap-noborder .cardPadder,
      .ap-noborder .cardImageContainer, .ap-noborder .ap-wrap,
      .ap-noborder .cardBox::before, .ap-noborder .cardBox::after,
      .ap-noborder .cardScalable::before, .ap-noborder .cardScalable::after {
        border:none !important; border-radius:0 !important; box-shadow:none !important; outline:none !important; }
      .ap-host > .cardOverlayContainer, .ap-host ~ .cardOverlayContainer { z-index:2; }
    `;
    document.head.appendChild(s);
  }

  function api() { return window.ApiClient || (window.connectionManager && window.connectionManager.currentApiClient && window.connectionManager.currentApiClient()); }

  function imgUrl(c, id, type, tag, maxWidth) {
    const opts = { type, maxWidth, quality: 85 };
    if (tag) opts.tag = tag;
    return c.getScaledImageUrl ? c.getScaledImageUrl(id, opts)
      : c.getUrl(`Items/${id}/Images/${type}`, opts);
  }

  async function fetchPool(libId) {
    const c = api();
    const userId = c.getCurrentUserId();
    const base = {
      ParentId: libId, Recursive: true, SortBy: 'Random', Limit: CONFIG.itemsPerLibrary,
      IncludeItemTypes: 'Movie,Series,MusicAlbum,MusicVideo,Video,BoxSet',
      Fields: 'ImageTags,BackdropImageTags', EnableImageTypes: 'Primary,Backdrop,Logo,Thumb',
      ExcludeLocationTypes: 'Virtual'
    };
    const getItems = (q) => c.getItems(userId, Object.assign({}, base, q)).then(r => r.Items || []);
    let items = await getItems({ ImageTypes: 'Backdrop' });
    if (items.length < 3) items = items.concat(await getItems({ ImageTypes: 'Thumb' }));
    if (items.length < 3) items = items.concat(await getItems({ ImageTypes: 'Primary' }));
    const w = 960;
    return items.map(it => {
      let bg = null;
      if (it.BackdropImageTags && it.BackdropImageTags.length) bg = imgUrl(c, it.Id, 'Backdrop', it.BackdropImageTags[0], w);
      else if (it.ImageTags && it.ImageTags.Thumb) bg = imgUrl(c, it.Id, 'Thumb', it.ImageTags.Thumb, w);
      else if (it.ImageTags && it.ImageTags.Primary) bg = imgUrl(c, it.Id, 'Primary', it.ImageTags.Primary, w);
      const logo = CONFIG.showLogos && it.ImageTags && it.ImageTags.Logo
        ? imgUrl(c, it.Id, 'Logo', it.ImageTags.Logo, 500) : null;
      return bg ? { bg, logo } : null;
    }).filter(Boolean);
  }

  function getPool(libId) {
    const p = pools.get(libId);
    if (p && Date.now() - p.at < CONFIG.refreshMinutes * 60000) return p.items;
    const items = fetchPool(libId).catch(e => { log('fetch failed', libId, e); pools.delete(libId); return []; });
    pools.set(libId, { at: Date.now(), items });
    return items;
  }

  function preload(url) {
    return new Promise(res => { const i = new Image(); i.onload = i.onerror = () => res(); i.src = url; });
  }

  function cardName(card) {
    const t = card.querySelector('.cardText, .cardFooter .cardText-first, button.itemAction[title]');
    return (t && (t.textContent || t.title) || card.getAttribute('data-name') || '').trim();
  }

  // -------------------------------------------------------------- per card
  async function animate(card) {
    const libId = card.getAttribute('data-id');
    const host = card.querySelector('.cardImageContainer');
    if (!libId || !host) return;
    card.dataset.apDone = '1';

    const name = cardName(card);
    if (CONFIG.skipLibraries.includes(name)) return;
    const cfg = Object.assign({}, CONFIG, CONFIG.perLibrary[name] || {});

    const pool = await getPool(libId);
    if (!pool.length || !card.isConnected) return;

    const c = api();
    const original = imgUrl(c, libId, 'Primary', null, 960);

    host.classList.add('ap-host');
    if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
    const wrap = document.createElement('div');
    wrap.className = 'ap-wrap';
    const slides = [0, 1].map(() => {
      const s = document.createElement('div');
      s.className = 'ap-slide';
      wrap.appendChild(s);
      return s;
    });
    if (cfg.originalArt === 'screen') {
      const scr = document.createElement('div');
      scr.className = 'ap-screen';
      scr.style.backgroundImage = `url("${original}")`;
      scr.style.opacity = cfg.screenOpacity;
      wrap.appendChild(scr);
    }
    host.insertBefore(wrap, host.firstChild);

    let order = pool.slice().sort(() => Math.random() - 0.5);
    let idx = 0, step = 0, front = 0, rev = false;

    function next() {
      step++;
      if (cfg.originalArt === 'slide' && (step === 1 || step % cfg.originalEvery === 0)) {
        return { bg: original, logo: null, isOriginal: true };
      }
      if (idx >= order.length) { idx = 0; order = order.sort(() => Math.random() - 0.5); }
      return order[idx++];
    }

    async function show() {
      if (!card.isConnected) return stop();
      if (document.hidden) return;
      const item = next();
      await preload(item.bg);
      if (item.logo) await preload(item.logo);
      if (!card.isConnected) return stop();
      const incoming = slides[1 - front], outgoing = slides[front];
      incoming.innerHTML = '';
      incoming.style.backgroundImage = `url("${item.bg}")`;
      if (!item.isOriginal && item.logo) {
        incoming.insertAdjacentHTML('beforeend', '<div class="ap-shade"></div>');
        const l = document.createElement('div');
        l.className = 'ap-logo';
        l.style.backgroundImage = `url("${item.logo}")`;
        incoming.appendChild(l);
      }
      incoming.classList.remove('ap-kb', 'ap-rev');
      void incoming.offsetWidth; // restart animation
      if (cfg.kenBurns && !reduceMotion && !item.isOriginal) {
        incoming.classList.add('ap-kb');
        if ((rev = !rev)) incoming.classList.add('ap-rev');
      }
      incoming.classList.add('ap-on');
      outgoing.classList.remove('ap-on');
      front = 1 - front;
    }

    let timer = null;
    function stop() { clearInterval(timer); timer = null; }

    // Stagger cards so they don't all flip at once
    const delay = Math.floor(Math.random() * cfg.interval);
    if (cfg.originalArt === 'slide') {
      slides[0].style.backgroundImage = `url("${original}")`;
      slides[0].classList.add('ap-on');
      step = 1;
    } else {
      await show();
    }
    setTimeout(() => {
      if (!card.isConnected) return;
      show();
      timer = setInterval(show, cfg.interval);
    }, delay);
  }

  // --------------------------------------------------------------- scanning
  const LIB_SELECTOR = '.card[data-type="CollectionFolder"], .card[data-type="UserView"]';

  function scan() {
    const c = api();
    if (!c || !c.getCurrentUserId || !c.getCurrentUserId()) return;
    injectStyle();
    document.querySelectorAll(LIB_SELECTOR).forEach(card => {
      // Only home sections and the libraries list, not the dashboard
      if (!card.closest('.homeSectionsContainer, .itemsContainer')) return;
      card.classList.toggle('ap-notext', !!CONFIG.hideText);
      card.classList.toggle('ap-noborder', !!CONFIG.hideBorder);
      if (card.dataset.apDone) return;
      animate(card).catch(e => log('animate failed', e));
    });
  }

  let pending = null;
  new MutationObserver(() => {
    if (pending) return;
    pending = setTimeout(() => { pending = null; scan(); }, 300);
  }).observe(document.body, { childList: true, subtree: true });

  scan();
  log('loaded');
})();
