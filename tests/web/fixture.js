/* Browser-only tests against the unmodified embedded production script and CSS. */
const ids = ['1'.repeat(32), '2'.repeat(32), '3'.repeat(32), '4'.repeat(32)];
const users = ['a'.repeat(32), 'b'.repeat(32)];
let user = users[0];
let server = 'server-a';
let mode = 'normal';
let releaseSave;
let lastQuery;
let detailReads = 0;
const records = new Map();
const writes = [];
const $ = selector => document.querySelector(selector);
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const until = async predicate => {
    for (let attempt = 0; attempt < 100; attempt++) {
        if (predicate()) return;
        await wait(30);
    }
    throw new Error('Timed out waiting for fixture state');
};
function record(id, identity = user, serverId = server) {
    const key = `${serverId}:${identity}:${id}`;
    if (!records.has(key)) records.set(key, {
        ItemId: id, ItemType: 'Movie', Rating: identity === users[0] ? [6, 8, null, 7][ids.indexOf(id)] : null,
        ReviewText: '', CreatedAt: null, UpdatedAt: null
    });
    return records.get(key);
}
window.ApiClient = {
    getCurrentUserId: () => user,
    getUrl: value => {
        if (!value) throw new Error('Url name cannot be empty');
        return new URL(value, location.origin + '/').href;
    },
    serverId: () => server,
    ajax: async options => {
        const url = new URL(options.url);
        const body = JSON.parse(options.data || '{}');
        const id = url.pathname.split('/')[3];
        if (url.pathname.endsWith('/ratings')) return body.itemIds.flatMap(id => {
            const r = record(id); return r.Rating === null ? [] : [{ ItemId: id, Rating: r.Rating }];
        });
        if (url.pathname.endsWith('/query')) {
            lastQuery = url.searchParams;
            let items = ids.map(id => ({ Id: id, Name: `Fixture ${ids.indexOf(id) + 1}`, UserData: { Rating: record(id).Rating } }));
            const state = lastQuery.get('ratingState');
            items = items.filter(item => state === 'All' || (state === 'Rated' ? item.UserData.Rating !== null : item.UserData.Rating === null));
            for (const bound of ['minRating', 'maxRating']) {
                if (lastQuery.has(bound)) items = items.filter(item => item.UserData.Rating !== null &&
                    (bound === 'minRating' ? item.UserData.Rating >= Number(lastQuery.get(bound)) : item.UserData.Rating <= Number(lastQuery.get(bound))));
            }
            return { Items: items, TotalRecordCount: items.length };
        }
        if (options.type === 'GET') {
            detailReads++;
            if (mode === 'readfail') throw { status: 503 };
        }
        const target = record(id);
        if (options.type === 'PUT') {
            writes.push({ path: url.pathname, body });
            if (url.pathname.endsWith('/rating')) target.Rating = body.rating;
            else {
                if (mode === 'delay') await new Promise(resolve => { releaseSave = resolve; });
                if (mode === 'fail') throw new Error('Synthetic offline failure');
                if (mode === 'serverfail') throw { status: 503 };
                if (mode === 'authfail') throw { status: 401 };
                if (target.ReviewText !== body.reviewText) target.UpdatedAt = new Date().toISOString();
                target.CreatedAt ||= target.UpdatedAt;
                target.ReviewText = body.reviewText;
            }
        }
        return { ...target };
    }
};
function render() {
    $('#fixture').innerHTML = location.hash.startsWith('#/details')
        ? '<div class="itemDetailPage"><div class="detailPagePrimaryContainer"><div class="detailPagePrimaryContent">Synthetic detail</div></div></div>'
        : '<div id="moviesPage"><div><div class="itemsContainer">' + ids.map((id, i) =>
            `<div class="card" data-id="${id}"><div class="cardImageContainer"><a href="#/details?id=${id}">Fixture ${i + 1}</a></div></div>`).join('') + '</div></div></div>';
}
const library = () => { location.hash = '#/movies?topParentId=' + 'f'.repeat(32); };
const detail = () => { location.hash = '#/details?id=' + ids[0]; };
window.addEventListener('hashchange', render);
$('#library').onclick = library;
$('#detail').onclick = detail;
$('#long').onclick = async () => {
    detail(); await until(() => $('.jlr-review-input'));
    input(('长评价测试：保留段落，页面滚动，输入框无内部滚动。\n').repeat(100));
};
function input(value) {
    $('.jlr-review-input').value = value;
    $('.jlr-review-input').dispatchEvent(new Event('input', { bubbles: true }));
}
function select(selector, value) {
    $(selector).value = value;
    $(selector).dispatchEvent(new Event('change', { bubbles: true }));
}
function assert(condition, message) { if (!condition) throw new Error(message); }
$('#run').onclick = async () => {
    $('#run').disabled = true;
    const report = $('#report'); report.textContent = '';
    const pass = text => { report.textContent += 'PASS ' + text + '\n'; };
    try {
        user = users[0]; server = 'server-a'; mode = 'normal'; records.clear(); writes.length = 0;
        library(); await until(() => $('.jlr-library-controls'));
        select('[aria-label="个人评分状态"]', 'AtMost'); await wait(150);
        assert(lastQuery.get('maxRating') === '6' && !lastQuery.has('minRating'), '<= mapping');
        assert($('.jlr-library-status').textContent === '共 1 部', '6 included; unrated excluded');
        pass('低于或等于 6 包含 6，排除未评分');
        select('[aria-label="个人评分状态"]', 'Equal');
        select('[aria-label="评分数值或区间下限"]', '8'); await wait(150);
        assert(lastQuery.get('minRating') === '8' && lastQuery.get('maxRating') === '8', 'exact mapping');
        pass('等于 8 的请求映射');
        select('[aria-label="个人评分状态"]', 'Between');
        select('[aria-label="评分数值或区间下限"]', '7');
        select('[aria-label="评分区间上限"]', '8'); await wait(150);
        assert($('.jlr-library-status').textContent === '共 2 部', 'inclusive range');
        pass('区间包含两端');
        mode = 'readfail'; const readsBefore = detailReads;
        detail(); await until(() => $('.jlr-load-panel button:not([hidden])'));
        await wait(250);
        assert(detailReads === readsBefore + 1, 'failed detail load retried automatically');
        mode = 'normal'; $('.jlr-load-panel button').click();
        await until(() => $('.jlr-review-input'));
        pass('详情读取失败可见且不循环请求，点击重试恢复');
        detail(); await until(() => $('.jlr-review-input'));
        assert(!$('.jlr-review-input').closest('[hidden]') && document.activeElement !== $('.jlr-review-input'), 'inline without autofocus');
        input('draft A');
        $('.jlr-rating-button[data-jlr-value="8"]').click(); await wait(150);
        assert(writes.length === 1 && writes[0].body.reviewText === undefined && record(ids[0]).ReviewText === '', 'rating submitted draft');
        pass('直接编辑且无自动聚焦；评分不提交草稿');
        render(); await until(() => $('.jlr-review-input'));
        assert($('.jlr-review-input').value === 'draft A', 'same-route remount lost panel or draft');
        pass('同路由页面重建仍恢复评价面板与草稿');
        library(); await until(() => $('.jlr-library-controls'));
        assert($('[aria-label="个人评分状态"]').value === 'Between' && $('[aria-label="评分数值或区间下限"]').value === '7', 'selection lost');
        detail(); await until(() => $('.jlr-review-input'));
        assert($('.jlr-review-input').value === 'draft A', 'draft lost on navigation');
        pass('详情往返保留筛选及草稿');
        mode = 'delay'; $('.jlr-save-button').click(); await until(() => releaseSave);
        input('draft B'); releaseSave(); releaseSave = null; await wait(150); mode = 'normal';
        assert(record(ids[0]).ReviewText === 'draft A' && $('.jlr-review-input').value === 'draft B' && !$('.jlr-save-button').disabled, 'save overwrote newer draft');
        assert($('.jlr-review-times').textContent.includes('最后编辑'), 'timestamps absent');
        pass('保存中继续输入不丢失；显示评价时间');
        mode = 'fail'; $('.jlr-save-button').click(); await wait(150); mode = 'normal';
        assert($('.jlr-review-input').value === 'draft B' && $('.jlr-review-status').textContent.includes('失败'), 'failed save lost draft');
        pass('保存失败保留输入并允许重试');
        for (const [failure, message] of [['serverfail', '服务器暂时无法写入'], ['authfail', '登录已失效']]) {
            mode = failure; $('.jlr-save-button').click(); await wait(150);
            assert($('.jlr-review-status').textContent.includes(message) && $('.jlr-review-input').value === 'draft B', 'failure reason or draft missing');
        }
        mode = 'normal';
        pass('服务端写入故障与登录失效分别提示，保留输入');
        $('.jlr-cancel-button').click();
        assert($('.jlr-review-input').value === 'draft A' && $('.jlr-save-button').disabled && !$('.jlr-undo-button').hidden, 'single-click discard failed');
        const writeCount = writes.length;
        $('.jlr-undo-button').click();
        assert($('.jlr-review-input').value === 'draft B' && !$('.jlr-save-button').disabled && writes.length === writeCount, 'undo failed or submitted draft');
        library(); await until(() => $('.jlr-library-controls'));
        detail(); await until(() => $('.jlr-review-input'));
        assert($('.jlr-review-input').value === 'draft B', 'undone draft not persisted');
        pass('单击放弃、撤销恢复草稿，不写入服务器');
        $('.jlr-cancel-button').click();
        input('new input');
        assert($('.jlr-undo-button').hidden, 'stale undo can overwrite new input');
        $('.jlr-undo-button').click();
        assert($('.jlr-review-input').value === 'new input', 'stale undo overwrote new input');
        $('.jlr-cancel-button').click();
        await wait(10200);
        assert($('.jlr-undo-button').hidden && !$('.jlr-review-status').textContent.includes('10 秒'), 'undo did not expire');
        $('.jlr-undo-button').click();
        assert($('.jlr-review-input').value === 'draft A', 'expired undo restored discarded text');
        pass('继续输入使旧撤销失效；10 秒后自动到期');
        input('keyboard save');
        $('.jlr-review-input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true }));
        await wait(150);
        assert(record(ids[0]).ReviewText === 'keyboard save' && $('.jlr-save-button').disabled, 'keyboard save failed');
        input('draft A'); $('.jlr-save-button').click(); await wait(150);
        pass('Ctrl+Enter 显式保存，已保存后不重复提交');
        input('private draft'); user = users[1]; document.dispatchEvent(new Event('viewshow')); await wait(250);
        assert($('.jlr-review-input').value === '', 'cross user draft leak');
        user = users[0]; document.dispatchEvent(new Event('viewshow')); await wait(250);
        assert($('.jlr-review-input').value === 'draft A', 'logout did not clear draft');
        pass('切换用户清除草稿且不会串号');
        input('private server draft'); server = 'server-b'; document.dispatchEvent(new Event('viewshow')); await wait(250);
        assert($('.jlr-review-input').value === '', 'cross server leak');
        pass('同一用户 ID 在不同服务器隔离');
        input(('长文测试段落。\n').repeat(350)); await wait(150);
        const area = $('.jlr-review-input');
        assert(area.scrollHeight <= area.clientHeight + 1, 'textarea internally scrolls');
        assert(document.documentElement.scrollWidth <= innerWidth, 'horizontal overflow');
        pass('长文自然增高、无内部滚动、无水平溢出');
        report.textContent += 'ALL 16 CHECKS PASSED';
    } catch (error) { report.textContent += 'FAIL ' + error.message; }
    finally { $('#run').disabled = false; }
};
if (!location.hash) library();
render();
