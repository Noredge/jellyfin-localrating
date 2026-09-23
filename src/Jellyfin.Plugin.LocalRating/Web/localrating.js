(() => {
    'use strict';

    if (window.__jlrLoaded) return;
    window.__jlrLoaded = true;

    const state = {
        userKey: undefined,
        userGeneration: 0,
        routeKey: '',
        panelItemId: null,
        detailLoad: null,
        scanTimer: null,
        layoutTimer: null,
        refreshPanelLayout: null,
        batchTimer: null,
        libraryRequestId: 0,
        libraryUi: null,
        librarySelection: null,
        ratings: new Map(),
        pendingIds: new Set()
    };

    const libraryPageSize = 100;
    const draftPrefix = 'jlr-review-draft:';
    const drafts = new Map();
    let draftStorageFailed = false;

    function draftKey(userKey, itemId) {
        return `${draftPrefix}${encodeURIComponent(userKey)}:${itemId}`;
    }

    function readDraft(key) {
        if (drafts.has(key)) return drafts.get(key);
        try {
            const value = sessionStorage.getItem(key);
            if (value !== null && value.length <= 4000) {
                drafts.set(key, value);
                return value;
            }
        } catch { draftStorageFailed = true; }
        return null;
    }

    function writeDraft(key, text) {
        if (text === null) drafts.delete(key);
        else drafts.set(key, text);
        try {
            if (text === null) sessionStorage.removeItem(key);
            else sessionStorage.setItem(key, text);
        } catch { draftStorageFailed = true; }
    }

    function clearOtherDrafts(userKey) {
        const prefix = userKey ? `${draftPrefix}${encodeURIComponent(userKey)}:` : null;
        for (const key of drafts.keys()) {
            if (!prefix || !key.startsWith(prefix)) drafts.delete(key);
        }
        try {
            for (const key of Object.keys(sessionStorage)) {
                if (key.startsWith(draftPrefix) && (!prefix || !key.startsWith(prefix))) sessionStorage.removeItem(key);
            }
        } catch { draftStorageFailed = true; }
    }

    window.addEventListener('beforeunload', event => {
        if (draftStorageFailed && drafts.size) {
            event.preventDefault();
            event.returnValue = '';
        }
    });

    const api = () => window.ApiClient;
    const normalizeId = value => {
        const match = String(value || '').match(/[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f-]{27}/i);
        return match ? match[0].replaceAll('-', '').toLowerCase() : null;
    };

    function currentUserKey() {
        const client = api();
        if (typeof client?.getCurrentUserId !== 'function') return null;
        const userId = normalizeId(client.getCurrentUserId());
        if (!userId) return null;
        const server = new URL(client.getUrl('LocalRating/Items'), location.href);
        return `${server.origin}${server.pathname}|${client.serverId?.() || ''}|${userId}`;
    }

    function resetUserScopedState(nextUserKey) {
        // An API client can be absent briefly during initial page load.
        if (nextUserKey || state.userKey) clearOtherDrafts(nextUserKey);
        state.userKey = nextUserKey;
        state.userGeneration += 1;
        if (state.batchTimer) clearTimeout(state.batchTimer);
        state.batchTimer = null;
        state.ratings.clear();
        state.pendingIds.clear();
        state.panelItemId = null;
        state.detailLoad = null;
        state.refreshPanelLayout = null;
        state.librarySelection = null;
        removeLibraryUi();
        document.querySelectorAll('.jlr-panel, .jlr-rating-badge').forEach(element => element.remove());
        document.querySelectorAll('.jlr-badge-host').forEach(element => element.classList.remove('jlr-badge-host'));
        document.querySelectorAll('.card[data-jlr-item-id]').forEach(card => delete card.dataset.jlrItemId);
    }

    function syncCurrentUser() {
        const nextUserKey = currentUserKey();
        if (nextUserKey !== state.userKey) resetUserScopedState(nextUserKey);
        return nextUserKey;
    }

    function currentItemId() {
        const raw = `${location.search}&${location.hash}`;
        const match = raw.match(/[?&]id=([0-9a-f-]{32,36})/i);
        return normalizeId(match?.[1]);
    }

    function request(options) {
        const client = api();
        if (!client?.ajax || !client?.getUrl) return Promise.reject(new Error('Jellyfin API client is unavailable.'));
        return client.ajax({
            dataType: 'json',
            contentType: 'application/json',
            ...options,
            url: client.getUrl(options.path)
        });
    }

    function getItemRating(itemId) {
        return request({ type: 'GET', path: `LocalRating/Items/${itemId}` });
    }

    function saveRating(itemId, rating) {
        return request({
            type: 'PUT',
            path: `LocalRating/Items/${itemId}/rating`,
            data: JSON.stringify({ rating })
        });
    }

    function saveReview(itemId, reviewText) {
        return request({
            type: 'PUT',
            path: `LocalRating/Items/${itemId}/review`,
            data: JSON.stringify({ reviewText })
        });
    }

    function reviewFailureMessage(error) {
        const status = Number(error?.status);
        if (status === 401 || status === 403) return '评价保存失败：登录已失效或没有权限。草稿已保留，请重新登录后重试。';
        if (status === 404) return '评价保存失败：影片已不可访问。草稿已保留，请检查媒体库。';
        if (status >= 500) return '评价保存失败：服务器暂时无法写入。草稿已保留，请稍后重试；持续失败时请管理员检查存储权限与日志。';
        return '评价保存失败：请检查网络和服务器连接。草稿已保留，可重试。';
    }

    function removeLibraryUi() {
        state.libraryRequestId += 1;
        state.libraryUi?.tab?.classList.remove('jlr-library-mode');
        state.libraryUi?.toolbar?.classList.remove('jlr-library-native-toolbar');
        state.libraryUi?.controls?.remove();
        state.libraryUi?.results?.remove();
        state.libraryUi = null;
        document.querySelectorAll('.jlr-library-controls, .jlr-library-results').forEach(element => element.remove());
        document.querySelectorAll('.jlr-library-mode').forEach(element => element.classList.remove('jlr-library-mode'));
        document.querySelectorAll('.jlr-library-native-toolbar').forEach(element => element.classList.remove('jlr-library-native-toolbar'));
    }

    function libraryContext() {
        if (!location.hash.startsWith('#/movies')) return null;
        const page = document.querySelector('#moviesPage:not(.hide)');
        const legacyTab = page?.querySelector('#moviesTab.pageTabContent.is-active');
        const nativeGrid = (legacyTab || page)?.querySelector('.itemsContainer');
        const tab = legacyTab || nativeGrid?.parentElement;
        const sortButton = legacyTab?.querySelector('.btnSort');
        const toolbar = sortButton?.parentElement;
        const queryStart = location.hash.indexOf('?');
        const parameters = new URLSearchParams(queryStart >= 0 ? location.hash.slice(queryStart + 1) : '');
        const parentId = normalizeId(parameters.get('topParentId'));
        // Jellyfin 12 Modern renders the grid without moviesTab and moves the
        // native toolbar into the app bar. Only the Movies tab is supported.
        const selectedTab = parameters.get('tab');
        if (!tab || !nativeGrid || !parentId || (selectedTab && selectedTab !== '0')) return null;
        if (legacyTab && !toolbar) return null;
        return { tab, nativeGrid, toolbar, parentId };
    }

    function itemRating(item) {
        const raw = item?.UserData?.Rating ?? item?.userData?.rating ?? null;
        const value = Number(raw);
        return Number.isInteger(value) && value >= 1 && value <= 10 ? value : null;
    }

    function itemImageTag(item) {
        return item?.ImageTags?.Primary ?? item?.imageTags?.Primary ?? item?.imageTags?.primary ?? null;
    }

    function itemValue(item, name) {
        const camelName = `${name[0].toLowerCase()}${name.slice(1)}`;
        return item?.[name] ?? item?.[camelName] ?? null;
    }

    function makeLibraryCard(item) {
        const itemId = normalizeId(itemValue(item, 'Id'));
        if (!itemId) return null;
        const name = String(itemValue(item, 'Name') || '未命名影片');
        const year = itemValue(item, 'ProductionYear');
        const rating = itemRating(item);
        const client = api();
        const rawServerId = typeof client?.serverId === 'function' ? client.serverId() : client?.serverId;
        const serverId = normalizeId(rawServerId);
        const detailUrl = `#/details?id=${itemId}${serverId ? `&serverId=${serverId}` : ''}`;

        const card = document.createElement('article');
        card.className = 'jlr-library-card';
        card.dataset.jlrItemId = itemId;

        const imageLink = document.createElement('a');
        imageLink.className = 'jlr-library-image';
        imageLink.href = detailUrl;
        imageLink.setAttribute('aria-label', name);

        const placeholder = document.createElement('span');
        placeholder.className = 'material-icons jlr-library-placeholder';
        placeholder.setAttribute('aria-hidden', 'true');
        placeholder.textContent = 'movie';
        const imageTag = itemImageTag(item);
        if (imageTag) {
            const image = document.createElement('img');
            image.alt = '';
            image.loading = 'lazy';
            image.draggable = false;
            image.src = client.getUrl(
                `Items/${itemId}/Images/Primary?fillWidth=320&quality=90&tag=${encodeURIComponent(imageTag)}`);
            placeholder.hidden = true;
            image.addEventListener('error', () => {
                image.remove();
                placeholder.hidden = false;
            }, { once: true });
            imageLink.append(image);
        }
        imageLink.append(placeholder);

        if (rating !== null) {
            const badge = document.createElement('span');
            badge.className = 'jlr-rating-badge';
            if (badge.textContent !== `★ ${rating}`) badge.textContent = `★ ${rating}`;
            imageLink.append(badge);
            state.ratings.set(itemId, rating);
        } else {
            state.ratings.set(itemId, null);
        }

        const title = document.createElement('a');
        title.className = 'jlr-library-card-title';
        title.href = detailUrl;
        title.title = name;
        title.textContent = name;

        const metadata = document.createElement('div');
        metadata.className = 'jlr-library-card-meta';
        metadata.textContent = year ? String(year) : '';

        card.append(imageLink, title, metadata);
        return card;
    }

    function renderLibraryItems(ui, items) {
        const fragment = document.createDocumentFragment();
        items.forEach(item => {
            const card = makeLibraryCard(item);
            if (card) fragment.append(card);
        });
        if (!fragment.childNodes.length) {
            const empty = document.createElement('div');
            empty.className = 'jlr-library-message';
            empty.textContent = '没有符合当前条件的影片。';
            fragment.append(empty);
        }
        ui.results.replaceChildren(fragment);
    }

    function renderLibraryError(ui) {
        const message = document.createElement('div');
        message.className = 'jlr-library-message';
        message.textContent = '个人评分结果加载失败。';
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.className = 'jlr-library-button';
        retry.textContent = '重试';
        retry.addEventListener('click', () => void loadLibraryResults(ui));
        message.append(retry);
        ui.results.replaceChildren(message);
    }

    function syncLibrarySelection(ui) {
        state.librarySelection = {
            parentId: ui.parentId,
            ratingState: ui.ratingState.value,
            score: ui.score.value,
            upperScore: ui.upperScore.value,
            sortOrder: ui.sortOrder.value,
            startIndex: ui.startIndex
        };
    }

    function libraryModeActive(ui) {
        return ui.ratingState.value !== 'All' || ui.sortOrder.value !== '';
    }

    function restoreNativeLibrary(ui) {
        state.libraryRequestId += 1;
        ui.tab.classList.remove('jlr-library-mode');
        ui.controls.classList.remove('jlr-library-active');
        ui.results.hidden = true;
        ui.results.removeAttribute('aria-busy');
        ui.paging.hidden = true;
        ui.previous.hidden = true;
        ui.next.hidden = true;
        ui.status.textContent = '';
        ui.reset.hidden = true;
        ui.reset.disabled = true;
    }

    async function loadLibraryResults(ui) {
        if (state.libraryUi !== ui || !ui.controls.isConnected) return;
        syncLibrarySelection(ui);
        if (!libraryModeActive(ui)) {
            restoreNativeLibrary(ui);
            return;
        }

        const requestUserKey = syncCurrentUser();
        if (!requestUserKey || state.libraryUi !== ui) return;
        const requestGeneration = state.userGeneration;
        const requestId = ++state.libraryRequestId;
        const parameters = new URLSearchParams({
            ratingState: ['All', 'Rated', 'Unrated'].includes(ui.ratingState.value) ? ui.ratingState.value : 'Rated',
            parentId: ui.parentId,
            recursive: 'true',
            includeItemTypes: 'Movie',
            startIndex: String(ui.startIndex),
            limit: String(libraryPageSize)
        });
        const condition = ui.ratingState.value;
        if (['Equal', 'AtLeast', 'Between'].includes(condition)) parameters.set('minRating', ui.score.value);
        if (['Equal', 'AtMost'].includes(condition)) parameters.set('maxRating', ui.score.value);
        if (condition === 'Between') parameters.set('maxRating', ui.upperScore.value);
        if (ui.sortOrder.value) {
            parameters.set('sortOrder', ui.sortOrder.value);
            parameters.set('unratedPlacement', 'Last');
        }

        ui.tab.classList.add('jlr-library-mode');
        ui.controls.classList.add('jlr-library-active');
        ui.results.hidden = false;
        ui.results.setAttribute('aria-busy', 'true');
        ui.paging.hidden = false;
        ui.previous.hidden = true;
        ui.next.hidden = true;
        ui.status.textContent = '正在读取个人评分…';
        ui.previous.disabled = true;
        ui.next.disabled = true;
        ui.reset.hidden = false;
        ui.reset.disabled = false;

        try {
            const response = await request({
                type: 'GET',
                path: `LocalRating/Items/query?${parameters}`
            });
            if (state.libraryUi !== ui
                || currentUserKey() !== requestUserKey
                || state.userGeneration !== requestGeneration
                || requestId !== state.libraryRequestId) {
                syncCurrentUser();
                scheduleScan();
                return;
            }

            const items = response.Items ?? response.items ?? [];
            const total = Number(response.TotalRecordCount ?? response.totalRecordCount ?? 0);
            if (total > 0 && ui.startIndex >= total) {
                ui.startIndex = Math.floor((total - 1) / libraryPageSize) * libraryPageSize;
                syncLibrarySelection(ui);
                void loadLibraryResults(ui);
                return;
            }

            renderLibraryItems(ui, items);
            const first = total === 0 ? 0 : ui.startIndex + 1;
            const last = Math.min(ui.startIndex + items.length, total);
            const hasMultiplePages = total > libraryPageSize;
            ui.status.textContent = hasMultiplePages ? `${first}–${last} / ${total}` : `共 ${total} 部`;
            ui.previous.hidden = !hasMultiplePages;
            ui.next.hidden = !hasMultiplePages;
            ui.previous.disabled = ui.startIndex === 0;
            ui.next.disabled = ui.startIndex + items.length >= total;
        } catch (error) {
            if (state.libraryUi !== ui || requestId !== state.libraryRequestId) return;
            console.error('[Local Rating] Library query failed.', error);
            ui.status.textContent = '加载失败';
            renderLibraryError(ui);
        } finally {
            if (state.libraryUi === ui && requestId === state.libraryRequestId) {
                ui.results.removeAttribute('aria-busy');
            }
        }
    }

    function mountLibraryUi() {
        const context = libraryContext();
        if (!context) {
            removeLibraryUi();
            return;
        }
        if (state.libraryUi?.parentId === context.parentId
            && state.libraryUi.controls.isConnected
            && state.libraryUi.tab === context.tab) {
            return;
        }

        removeLibraryUi();
        const saved = state.librarySelection?.parentId === context.parentId
            ? state.librarySelection
            : { ratingState: 'All', sortOrder: '', startIndex: 0 };

        const controls = document.createElement('section');
        controls.className = 'jlr-library-controls';
        controls.setAttribute('aria-label', '我的评分筛选与排序');

        const heading = document.createElement('strong');
        heading.className = 'jlr-library-heading';
        heading.textContent = '我的评分';

        const ratingLabel = document.createElement('label');
        ratingLabel.className = 'jlr-library-field jlr-library-rating-field';
        const ratingCaption = document.createElement('span');
        ratingCaption.className = 'jlr-library-field-label';
        ratingCaption.textContent = '条件';
        const ratingState = document.createElement('select');
        ratingState.className = 'jlr-library-select';
        ratingState.setAttribute('aria-label', '个人评分状态');
        [
            ['All', '全部'],
            ['Rated', '已评分'],
            ['Unrated', '未评分'],
            ['Equal', '等于'],
            ['AtMost', '低于或等于'],
            ['AtLeast', '高于或等于'],
            ['Between', '评分区间']
        ].forEach(([value, text]) => ratingState.add(new Option(text, value)));
        ratingState.value = saved.ratingState;
        ratingLabel.append(ratingCaption, ratingState);

        const bounds = document.createElement('div');
        bounds.className = 'jlr-library-bounds';
        const score = document.createElement('select');
        score.className = 'jlr-library-select';
        score.setAttribute('aria-label', '评分数值或区间下限');
        const upperScore = document.createElement('select');
        upperScore.className = 'jlr-library-select';
        upperScore.setAttribute('aria-label', '评分区间上限');
        for (let value = 1; value <= 10; value += 1) {
            score.add(new Option(`${value} 分`, String(value)));
            upperScore.add(new Option(`${value} 分`, String(value)));
        }
        score.value = saved.score || '6';
        upperScore.value = saved.upperScore || '10';
        const between = document.createElement('span');
        between.textContent = '至';
        const inclusive = document.createElement('span');
        inclusive.textContent = '含两端';
        bounds.append(score, between, upperScore, inclusive);
        const refreshBounds = () => {
            bounds.hidden = ['All', 'Rated', 'Unrated'].includes(ratingState.value);
            between.hidden = upperScore.hidden = inclusive.hidden = ratingState.value !== 'Between';
        };
        refreshBounds();

        const sortLabel = document.createElement('label');
        sortLabel.className = 'jlr-library-field jlr-library-sort-field';
        const sortCaption = document.createElement('span');
        sortCaption.className = 'jlr-library-field-label';
        sortCaption.textContent = '排序';
        const sortOrder = document.createElement('select');
        sortOrder.className = 'jlr-library-select';
        sortOrder.setAttribute('aria-label', '个人评分排序');
        [
            ['', '默认顺序'],
            ['Descending', '评分：高到低'],
            ['Ascending', '评分：低到高']
        ].forEach(([value, text]) => sortOrder.add(new Option(text, value)));
        sortOrder.value = saved.sortOrder;
        sortLabel.append(sortCaption, sortOrder);

        const reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'jlr-library-button';
        reset.textContent = '重置筛选';
        reset.hidden = true;

        const paging = document.createElement('div');
        paging.className = 'jlr-library-paging';
        paging.hidden = true;
        const previous = document.createElement('button');
        previous.type = 'button';
        previous.className = 'jlr-library-page-button';
        previous.textContent = '上一页';
        const status = document.createElement('span');
        status.className = 'jlr-library-status';
        status.setAttribute('role', 'status');
        status.setAttribute('aria-live', 'polite');
        const next = document.createElement('button');
        next.type = 'button';
        next.className = 'jlr-library-page-button';
        next.textContent = '下一页';
        paging.append(previous, status, next);
        controls.append(heading, ratingLabel, bounds, sortLabel, reset, paging);

        const results = document.createElement('div');
        results.className = 'jlr-library-results';
        results.setAttribute('aria-label', '我的评分媒体库结果');
        results.hidden = true;

        if (context.toolbar) {
            context.toolbar.classList.add('jlr-library-native-toolbar');
            context.toolbar.append(controls);
        } else {
            controls.classList.add('jlr-library-modern-controls');
            context.nativeGrid.insertAdjacentElement('beforebegin', controls);
        }
        context.nativeGrid.insertAdjacentElement('afterend', results);

        const ui = {
            ...context,
            controls,
            results,
            ratingState,
            score,
            upperScore,
            sortOrder,
            reset,
            paging,
            previous,
            status,
            next,
            startIndex: Number(saved.startIndex) || 0
        };
        state.libraryUi = ui;

        const changeQuery = () => {
            refreshBounds();
            ui.startIndex = 0;
            void loadLibraryResults(ui);
        };
        ratingState.addEventListener('change', changeQuery);
        score.addEventListener('change', () => {
            if (Number(score.value) > Number(upperScore.value)) upperScore.value = score.value;
            changeQuery();
        });
        upperScore.addEventListener('change', () => {
            if (Number(upperScore.value) < Number(score.value)) score.value = upperScore.value;
            changeQuery();
        });
        sortOrder.addEventListener('change', changeQuery);
        reset.addEventListener('click', () => {
            ratingState.value = 'All';
            sortOrder.value = '';
            refreshBounds();
            ui.startIndex = 0;
            void loadLibraryResults(ui);
        });
        previous.addEventListener('click', () => {
            ui.startIndex = Math.max(0, ui.startIndex - libraryPageSize);
            void loadLibraryResults(ui);
        });
        next.addEventListener('click', () => {
            ui.startIndex += libraryPageSize;
            void loadLibraryResults(ui);
        });
        void loadLibraryResults(ui);
    }

    function findDetailHost() {
        return document.querySelector('.itemDetailPage:not(.hide) .detailPagePrimaryContainer')
            || document.querySelector('.detailPagePrimaryContainer')
            || document.querySelector('.detailPageContent')
            || document.querySelector('.itemDetailPage:not(.hide)');
    }

    function isVideoType(type) {
        return ['Movie', 'Episode', 'Video', 'MusicVideo', 'Trailer'].includes(type);
    }

    function usesNativeSidePoster(host) {
        const primaryContent = host.querySelector('.detailPagePrimaryContent');
        const posterCard = host.querySelector('.detailImageContainer .card');
        if (!primaryContent || !posterCard) return false;

        const posterStyle = getComputedStyle(posterCard);
        const contentPadding = Number.parseFloat(getComputedStyle(primaryContent).paddingLeft) || 0;
        return posterStyle.display !== 'none' && posterStyle.visibility !== 'hidden' && contentPadding > 100;
    }

    function placeDetailPanel(host, panel) {
        const primaryContent = host.querySelector('.detailPagePrimaryContent');
        const sidePosterLayout = primaryContent?.parentElement === host && usesNativeSidePoster(host);
        const narrowSidePoster = sidePosterLayout && window.matchMedia('(max-width: 42rem)').matches;
        const nativeSidePoster = sidePosterLayout && !narrowSidePoster;
        panel.classList.toggle('jlr-panel-native', nativeSidePoster);
        panel.classList.toggle('jlr-panel-narrow', narrowSidePoster);
        panel.classList.toggle('jlr-panel-wide', !nativeSidePoster);
        panel.style.removeProperty('--jlr-narrow-clearance');

        if (narrowSidePoster) {
            const posterCard = host.querySelector('.detailImageContainer .card');
            const posterBottom = posterCard?.getBoundingClientRect().bottom || 0;
            const hostTop = host.getBoundingClientRect().top;
            panel.style.setProperty('--jlr-narrow-clearance', `${Math.max(0, Math.ceil(posterBottom - hostTop))}px`);
            if (panel.parentElement !== host || panel.nextElementSibling !== primaryContent) {
                host.insertBefore(panel, primaryContent);
            }
            return;
        }

        if (nativeSidePoster) {
            if (panel.parentElement !== primaryContent || panel !== primaryContent.firstElementChild) {
                primaryContent.prepend(panel);
            }
            return;
        }

        if (primaryContent?.parentElement === host) {
            if (panel.parentElement !== host || panel.nextElementSibling !== primaryContent) {
                host.insertBefore(panel, primaryContent);
            }
        } else if (panel.parentElement !== host) {
            host.append(panel);
        }
    }

    async function mountDetailPanel() {
        const userKeyAtStart = state.userKey;
        if (!userKeyAtStart) return;
        const itemId = currentItemId();
        if (!itemId) {
            document.querySelector('.jlr-panel')?.remove();
            state.panelItemId = null;
            state.refreshPanelLayout = null;
            return;
        }

        const host = findDetailHost();
        if (!host) return;
        if (state.panelItemId === itemId && document.querySelector(`.jlr-panel[data-jlr-item-id="${itemId}"]`)) return;

        const routeAtStart = `${location.pathname}${location.search}${location.hash}`;
        if (state.detailLoad?.host === host && state.detailLoad.route === routeAtStart
            && state.detailLoad.userKey === userKeyAtStart) return;
        const attempt = { host, route: routeAtStart, userKey: userKeyAtStart };
        state.detailLoad = attempt;
        const loading = document.createElement('section');
        loading.className = 'jlr-panel jlr-load-panel';
        loading.dataset.jlrItemId = itemId;
        loading.innerHTML = '<h2 class="jlr-title">我的评分</h2><p role="status">正在读取评分与评价…</p><button type="button" class="jlr-library-button" hidden>重试</button>';
        document.querySelectorAll('.jlr-panel').forEach(panel => panel.remove());
        placeDetailPanel(host, loading);
        const isCurrentAttempt = () => state.detailLoad === attempt && host.isConnected
            && currentUserKey() === userKeyAtStart
            && routeAtStart === `${location.pathname}${location.search}${location.hash}`;
        loading.querySelector('button').addEventListener('click', () => {
            if (!isCurrentAttempt()) return;
            state.detailLoad = null;
            loading.remove();
            void mountDetailPanel();
        });
        try {
            const data = await getItemRating(itemId);
            if (currentUserKey() !== userKeyAtStart) {
                syncCurrentUser();
                scheduleScan();
                return;
            }
            if (!isCurrentAttempt()) return;
            if (!isVideoType(data.ItemType || data.itemType)) {
                loading.remove();
                return;
            }
            renderPanel(host, itemId, data, userKeyAtStart);
        } catch (error) {
            console.debug('[Local Rating] Detail data unavailable.', error);
            if (!isCurrentAttempt()) return;
            if (Number(error?.status) === 404) {
                loading.remove();
                return;
            }
            loading.querySelector('[role="status"]').textContent = Number(error?.status) === 401 || Number(error?.status) === 403
                ? '评分与评价读取失败，请确认登录状态后重试。'
                : '评分与评价暂时无法读取，请检查连接后重试。已有草稿会保留。';
            loading.querySelector('button').hidden = false;
        }
    }

    function renderPanel(host, itemId, rawData, panelUserKey) {
        document.querySelectorAll('.jlr-panel').forEach(panel => panel.remove());
        const data = {
            rating: rawData.Rating ?? rawData.rating ?? null,
            reviewText: rawData.ReviewText ?? rawData.reviewText ?? ''
        };
        let savedRating = data.rating;
        let selectedRating = savedRating;
        let savedReviewText = data.reviewText;
        let ratingBusy = false;
        let queuedRating;
        let reviewBusy = false;
        const key = draftKey(panelUserKey, itemId);
        const panelGeneration = state.userGeneration;
        let discardedReview = null;
        let undoDeadline = 0;
        let undoTimer;

        const panel = document.createElement('section');
        panel.className = 'jlr-panel';
        panel.dataset.jlrItemId = itemId;
        panel.innerHTML = `
            <div class="jlr-title-row">
                <h2 class="jlr-title">我的评分</h2>
                <div class="jlr-current-rating" aria-label="当前评分：未评分">
                    <span class="jlr-current-rating-label">当前评分</span>
                    <strong class="jlr-current-rating-value">未评分</strong>
                    <span class="jlr-current-rating-state"></span>
                </div>
            </div>
            <div class="jlr-rating-row">
                <div class="jlr-rating-grid" role="radiogroup" aria-label="我的评分"></div>
                <button class="jlr-clear-button" type="button">清除评分</button>
            </div>
            <div class="jlr-rating-status" role="status" aria-live="polite">选择数字即可自动保存</div>
            <div class="jlr-review-heading-row">
                <label class="jlr-review-label" for="jlr-review-${itemId}">个人评价</label>
            </div>
            <div class="jlr-review-times"></div>
            <div class="jlr-review-editor">
                <textarea class="jlr-review-input" id="jlr-review-${itemId}" aria-describedby="jlr-review-status-${itemId}" maxlength="4000" placeholder="仅你自己可见，最多 4000 字。"></textarea>
                <div class="jlr-actions">
                <div class="jlr-review-meta">
                    <span class="jlr-review-status" id="jlr-review-status-${itemId}" role="status" aria-live="polite"></span>
                    <span class="jlr-count">0 / 4000</span>
                </div>
                <div class="jlr-action-buttons">
                    <button class="jlr-save-button" type="button" title="Ctrl+Enter / ⌘+Enter 保存评价" aria-keyshortcuts="Control+Enter Meta+Enter">保存评价</button>
                    <button class="jlr-cancel-button" type="button">放弃修改</button>
                    <button class="jlr-cancel-button jlr-undo-button" type="button" hidden>撤销</button>
                </div>
                </div>
            </div>
        `;
        const isCurrentPanel = () => panel.isConnected
            && state.panelItemId === itemId
            && state.userKey === panelUserKey
            && currentUserKey() === panelUserKey;

        const grid = panel.querySelector('.jlr-rating-grid');
        const times = panel.querySelector('.jlr-review-times');
        const textarea = panel.querySelector('.jlr-review-input');
        const count = panel.querySelector('.jlr-count');
        const currentRating = panel.querySelector('.jlr-current-rating');
        const currentRatingValue = panel.querySelector('.jlr-current-rating-value');
        const currentRatingState = panel.querySelector('.jlr-current-rating-state');
        const ratingStatus = panel.querySelector('.jlr-rating-status');
        const reviewStatus = panel.querySelector('.jlr-review-status');
        const save = panel.querySelector('.jlr-save-button');
        const cancel = panel.querySelector('.jlr-cancel-button');
        const undo = panel.querySelector('.jlr-undo-button');
        const clear = panel.querySelector('.jlr-clear-button');
        const restoredDraft = readDraft(key);
        textarea.value = restoredDraft ?? data.reviewText;
        const persistDraft = () => writeDraft(key, reviewIsDirty() ? textarea.value : null);
        const refreshTimes = response => {
            const format = value => {
                if (!value) return '';
                const date = new Date(value);
                return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
            };
            const created = format(response.CreatedAt ?? response.createdAt);
            const updated = format(response.UpdatedAt ?? response.updatedAt);
            times.textContent = created ? `创建：${created}${updated ? ` · 最后编辑：${updated}` : ''}` : '尚未保存评价';
        };
        refreshTimes(rawData);

        const refreshRatingSummary = () => {
            const pending = ratingBusy || queuedRating !== undefined || selectedRating !== savedRating;
            const stateText = pending ? (ratingBusy ? '保存中' : '未保存') : '';
            currentRating.classList.toggle('jlr-current-rating-pending', pending);
            currentRatingValue.textContent = selectedRating === null ? '未评分' : `★ ${selectedRating} / 10`;
            currentRatingState.textContent = stateText;
            currentRating.setAttribute(
                'aria-label',
                selectedRating === null
                    ? `当前评分：未评分${stateText ? `，${stateText}` : ''}`
                    : `当前评分：${selectedRating} 分，满分 10 分${stateText ? `，${stateText}` : ''}`);
        };
        const refreshButtons = () => {
            grid.querySelectorAll('.jlr-rating-button').forEach(button => {
                const value = Number(button.dataset.jlrValue);
                const active = value === selectedRating;
                button.classList.toggle('jlr-selected', active);
                button.setAttribute('aria-checked', String(active));
                button.tabIndex = value === (selectedRating ?? 1) ? 0 : -1;
            });
            clear.disabled = selectedRating === null && savedRating === null && queuedRating === undefined;
            grid.setAttribute('aria-busy', String(ratingBusy));
            refreshRatingSummary();
        };
        const reviewIsDirty = () => textarea.value !== savedReviewText;
        const clearUndo = () => {
            discardedReview = null;
            undoDeadline = 0;
            undo.hidden = true;
            if (undoTimer) clearTimeout(undoTimer);
            undoTimer = undefined;
            if (reviewStatus.textContent === '已放弃修改 · 10 秒内可撤销') reviewStatus.textContent = '已放弃修改';
        };
        const autoSizeTextarea = () => {
            textarea.style.height = 'auto';
            const minimum = Number.parseFloat(getComputedStyle(textarea).minHeight) || 192;
            const style = getComputedStyle(textarea);
            const border = (Number.parseFloat(style.borderTopWidth) || 0) + (Number.parseFloat(style.borderBottomWidth) || 0);
            const height = Math.max(textarea.scrollHeight + border, minimum);
            textarea.style.height = `${height}px`;
            textarea.style.overflowY = 'hidden';
        };
        const refreshReview = message => {
            count.textContent = `${textarea.value.length} / 4000`;
            panel.classList.toggle('jlr-review-dirty', reviewIsDirty());
            save.disabled = !reviewIsDirty() || reviewBusy;
            save.textContent = reviewBusy ? '正在保存评价…' : '保存评价';
            cancel.disabled = reviewBusy || !reviewIsDirty();
            if (message !== undefined) {
                reviewStatus.textContent = message;
                reviewStatus.classList.remove('jlr-status-error');
            }
            autoSizeTextarea();
        };
        const recoverRating = async intendedRating => {
            try {
                const current = await getItemRating(itemId);
                if (!isCurrentPanel()) return;
                const serverRating = current.Rating ?? current.rating ?? null;
                savedRating = serverRating;
                state.ratings.set(itemId, serverRating);
                updateBadges();
                if (serverRating === intendedRating) {
                    ratingStatus.textContent = '已重新确认评分保存';
                } else {
                    ratingStatus.textContent = '评分保存失败；当前选择尚未保存，再次点击可重试';
                }
            } catch (recoveryError) {
                console.debug('[Local Rating] Rating-state recovery failed.', recoveryError);
                if (isCurrentPanel()) {
                    ratingStatus.textContent = '评分保存失败；当前选择已保留，再次点击可重试';
                }
            }
        };
        const persistRating = async intendedRating => {
            if (!isCurrentPanel()) {
                syncCurrentUser();
                scheduleScan();
                return;
            }
            ratingBusy = true;
            ratingStatus.textContent = '正在保存评分…';
            refreshButtons();
            try {
                const saved = await saveRating(itemId, intendedRating);
                if (!isCurrentPanel()) return;
                savedRating = saved.Rating ?? saved.rating ?? null;
                state.ratings.set(itemId, savedRating);
                updateBadges();
                ratingStatus.textContent = queuedRating === undefined ? '评分已保存' : '正在保存最新评分…';
            } catch (error) {
                console.error('[Local Rating] Rating save failed.', error);
                await recoverRating(intendedRating);
            } finally {
                ratingBusy = false;
                const nextRating = queuedRating;
                queuedRating = undefined;
                refreshButtons();
                if (nextRating !== undefined && nextRating !== savedRating) {
                    void persistRating(nextRating);
                } else if (nextRating !== undefined) {
                    selectedRating = savedRating;
                    ratingStatus.textContent = '评分已保存';
                    refreshButtons();
                }
            }
        };
        const chooseRating = rating => {
            selectedRating = rating;
            refreshButtons();
            if (ratingBusy) {
                queuedRating = rating;
                ratingStatus.textContent = '等待保存最新评分…';
                return;
            }

            if (rating === savedRating) {
                ratingStatus.textContent = '评分已保存';
                return;
            }

            void persistRating(rating);
        };

        for (let rating = 1; rating <= 10; rating += 1) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'jlr-rating-button';
            button.dataset.jlrValue = String(rating);
            button.setAttribute('role', 'radio');
            button.setAttribute('aria-label', `${rating} 分`);
            button.textContent = String(rating);
            button.addEventListener('click', () => chooseRating(rating));
            button.addEventListener('keydown', event => {
                const keys = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'];
                if (!keys.includes(event.key)) return;
                event.preventDefault();
                const current = Number(button.dataset.jlrValue);
                const next = event.key === 'Home'
                    ? 1
                    : event.key === 'End'
                        ? 10
                        : event.key === 'ArrowLeft' || event.key === 'ArrowUp'
                            ? (current === 1 ? 10 : current - 1)
                            : (current === 10 ? 1 : current + 1);
                grid.querySelector(`[data-jlr-value="${next}"]`)?.focus();
                chooseRating(next);
            });
            grid.append(button);
        }

        clear.addEventListener('click', () => chooseRating(null));
        textarea.addEventListener('input', () => {
            if (!isCurrentPanel()) return;
            clearUndo();
            persistDraft();
            refreshReview(reviewIsDirty()
                ? (draftStorageFailed ? '评价尚未保存；当前浏览器无法保留刷新后的草稿' : '评价尚未保存 · 已保留本标签页草稿')
                : '评价已保存');
        });
        textarea.addEventListener('keydown', event => {
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) {
                event.preventDefault();
                if (!save.disabled) save.click();
            }
        });
        cancel.addEventListener('click', () => {
            if (!isCurrentPanel() || !reviewIsDirty() || reviewBusy) return;

            clearUndo();
            discardedReview = textarea.value;
            textarea.value = savedReviewText;
            persistDraft();
            undoDeadline = Date.now() + 10000;
            undo.hidden = false;
            undoTimer = setTimeout(clearUndo, 10000);
            refreshReview('已放弃修改 · 10 秒内可撤销');
        });
        undo.addEventListener('click', () => {
            if (!isCurrentPanel() || reviewBusy) return;
            if (discardedReview === null || Date.now() >= undoDeadline) {
                clearUndo();
                return;
            }
            textarea.value = discardedReview;
            clearUndo();
            persistDraft();
            refreshReview('已恢复修改，尚未保存');
        });
        save.addEventListener('click', async () => {
            const intendedReview = textarea.value;
            if (intendedReview === savedReviewText || reviewBusy) return;
            if (!isCurrentPanel()) {
                syncCurrentUser();
                scheduleScan();
                return;
            }
            clearUndo();
            reviewBusy = true;
            refreshReview('正在保存评价…');
            try {
                const saved = await saveReview(itemId, intendedReview);
                if (state.userGeneration !== panelGeneration || currentUserKey() !== panelUserKey) return;
                if (readDraft(key) === intendedReview) writeDraft(key, null);
                if (!isCurrentPanel()) return;
                savedReviewText = saved.ReviewText ?? saved.reviewText ?? '';
                refreshTimes(saved);
                persistDraft();
                if (textarea.value === savedReviewText) {
                    reviewStatus.textContent = '评价已保存';
                } else {
                    reviewStatus.textContent = '上一版本已保存；当前修改尚未保存';
                }
            } catch (error) {
                console.error('[Local Rating] Review save failed.', error);
                try {
                    const current = await getItemRating(itemId);
                    if (!isCurrentPanel()) return;
                    savedReviewText = current.ReviewText ?? current.reviewText ?? '';
                    refreshTimes(current);
                    persistDraft();
                    reviewStatus.textContent = savedReviewText === textarea.value
                        ? '已重新确认评价保存'
                        : savedReviewText === intendedReview
                            ? '上一版本已保存；当前修改尚未保存'
                            : reviewFailureMessage(error);
                    reviewStatus.classList.toggle('jlr-status-error', savedReviewText !== textarea.value && savedReviewText !== intendedReview);
                } catch (recoveryError) {
                    console.debug('[Local Rating] Review-state recovery failed.', recoveryError);
                    if (isCurrentPanel()) {
                        reviewStatus.textContent = reviewFailureMessage(error);
                        reviewStatus.classList.add('jlr-status-error');
                    }
                }
            } finally {
                reviewBusy = false;
                refreshReview();
            }
        });

        refreshButtons();
        placeDetailPanel(host, panel);
        state.panelItemId = itemId;
        persistDraft();
        refreshReview(restoredDraft !== null && reviewIsDirty() ? '已恢复未保存的草稿，请检查后保存' : '直接编辑，点击保存后生效');
        const refreshPlacement = () => {
            if (!isCurrentPanel()) return;
            placeDetailPanel(host, panel);
            autoSizeTextarea();
        };
        state.refreshPanelLayout = refreshPlacement;
        requestAnimationFrame(refreshPlacement);
        setTimeout(refreshPlacement, 500);
    }

    function cardItemId(card) {
        const values = [
            card.dataset.id,
            card.dataset.itemId,
            card.querySelector('[data-id]')?.dataset.id,
            card.querySelector('[data-item-id]')?.dataset.itemId,
            card.querySelector('a[href*="id="]')?.getAttribute('href')
        ];
        for (const value of values) {
            const id = normalizeId(value);
            if (id) return id;
        }
        return null;
    }

    function scanCards() {
        document.querySelectorAll('.card').forEach(card => {
            const itemId = cardItemId(card);
            if (!itemId) return;
            if (card.dataset.jlrItemId !== itemId) {
                card.querySelector('.jlr-rating-badge')?.remove();
                card.dataset.jlrItemId = itemId;
            }
            if (!state.ratings.has(itemId)) state.pendingIds.add(itemId);
        });
        updateBadges();
        scheduleBatch();
    }

    function updateBadges() {
        document.querySelectorAll('.card[data-jlr-item-id]').forEach(card => {
            const itemId = card.dataset.jlrItemId;
            const rating = state.ratings.get(itemId);
            let badge = card.querySelector('.jlr-rating-badge');
            if (!rating) {
                badge?.remove();
                return;
            }
            const host = card.querySelector('.cardImageContainer') || card.querySelector('.cardBox') || card;
            host.classList.add('jlr-badge-host');
            if (!badge) {
                badge = document.createElement('span');
                badge.className = 'jlr-rating-badge';
                host.append(badge);
            }
            if (badge.textContent !== `★ ${rating}`) badge.textContent = `★ ${rating}`;
        });
    }

    function scheduleBatch() {
        if (state.batchTimer || state.pendingIds.size === 0) return;
        state.batchTimer = setTimeout(loadPendingRatings, 120);
    }

    async function loadPendingRatings() {
        state.batchTimer = null;
        const requestUserKey = syncCurrentUser();
        if (!requestUserKey) return;
        const requestGeneration = state.userGeneration;
        const itemIds = [...state.pendingIds].slice(0, 200);
        itemIds.forEach(id => state.pendingIds.delete(id));
        if (!itemIds.length) return;
        try {
            const results = await request({
                type: 'POST',
                path: 'LocalRating/Items/ratings',
                data: JSON.stringify({ itemIds })
            });
            if (currentUserKey() !== requestUserKey || state.userGeneration !== requestGeneration) {
                syncCurrentUser();
                scheduleScan();
                return;
            }
            itemIds.forEach(id => state.ratings.set(id, null));
            results.forEach(entry => state.ratings.set(
                normalizeId(entry.ItemId ?? entry.itemId),
                entry.Rating ?? entry.rating));
            updateBadges();
        } catch (error) {
            console.debug('[Local Rating] Card ratings unavailable.', error);
        }
        if (state.pendingIds.size) scheduleBatch();
    }

    function scheduleScan() {
        if (state.scanTimer) return;
        state.scanTimer = setTimeout(() => {
            state.scanTimer = null;
            if (!syncCurrentUser()) return;
            const routeKey = `${location.pathname}${location.search}${location.hash}`;
            if (routeKey !== state.routeKey) {
                state.routeKey = routeKey;
                state.panelItemId = null;
                state.detailLoad = null;
                state.refreshPanelLayout = null;
                document.querySelector('.jlr-panel')?.remove();
            }
            mountDetailPanel();
            mountLibraryUi();
            scanCards();
        }, 80);
    }

    new MutationObserver(scheduleScan).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', scheduleScan);
    window.addEventListener('popstate', scheduleScan);
    window.addEventListener('resize', () => {
        if (state.layoutTimer) clearTimeout(state.layoutTimer);
        state.layoutTimer = setTimeout(() => {
            state.layoutTimer = null;
            state.refreshPanelLayout?.();
        }, 120);
    });
    document.addEventListener('viewshow', scheduleScan);
    scheduleScan();
})();
