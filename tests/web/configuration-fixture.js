// Loads the actual embedded settings page with synthetic API responses only.
(() => {
    const user = 'a'.repeat(32), library = 'f'.repeat(32);
    let config = { EnableRatingCompatibility: false, CompatibilityRules: [], FutureSetting: 'preserve' };
    let mode = 'normal', releaseSave, writes = 0;
    window.ApiClient = {
        getPluginConfiguration: async () => {
            if (mode === 'readfail') throw new Error('Synthetic read failure');
            return structuredClone(config);
        },
        getUsers: async () => [{ Id: user, Name: '测试账号' }],
        getVirtualFolders: async () => [{ ItemId: library, Name: '测试电影库', CollectionType: 'movies' }],
        updatePluginConfiguration: async (_, updated) => {
            writes++;
            if (mode === 'delay') await new Promise(resolve => { releaseSave = resolve; });
            if (mode === 'savefail') throw new Error('Synthetic save failure');
            config = structuredClone(updated);
        }
    };
    const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
    const assert = (value, message) => { if (!value) throw new Error(message); };
    document.addEventListener('DOMContentLoaded', () => {
        const page = document.querySelector('#localRatingConfiguration');
        const form = page.querySelector('form');
        const enabled = page.querySelector('#lrCompatibilityEnabled');
        const fields = page.querySelector('#lrConfigurationFields');
        const save = page.querySelector('#lrConfigurationSave');
        const status = page.querySelector('#lrConfigurationStatus');
        const retry = page.querySelector('#lrConfigurationRetry');
        const button = document.createElement('button'); button.textContent = 'Run settings tests';
        const report = document.createElement('pre'); report.id = 'report';
        document.body.prepend(button, report);
        const change = (input, value) => { input.checked = value; input.dispatchEvent(new Event('change', { bubbles: true })); };
        const show = () => page.dispatchEvent(new Event('viewshow'));
        const hide = () => page.dispatchEvent(new Event('viewhide'));
        button.onclick = async () => {
            button.disabled = true; report.textContent = '';
            const pass = text => { report.textContent += `PASS ${text}\n`; };
            try {
                config = { EnableRatingCompatibility: false, CompatibilityRules: [], FutureSetting: 'preserve' }; mode = 'normal'; writes = 0;
                show(); await wait(30);
                assert(save.disabled && !fields.disabled, 'clean state');
                assert(page.querySelector('#lrConfigurationSummary').textContent === '关闭 · 已选 0 个账号、0 个账号与电影库组合', 'localized summary broken');
                assert(getComputedStyle(retry).display === 'none', 'hidden retry visible');
                change(enabled, true);
                assert(!save.disabled && status.textContent.includes('尚未保存'), 'dirty state');
                save.click(); await wait(30);
                assert(writes === 0 && status.textContent.includes('至少选择'), 'empty enabled scope accepted');
                pass('未修改禁用保存；修改有提示；空范围不可启用');
                change(page.querySelector('#lrCompatibilityRules input'), true);
                mode = 'delay'; save.click(); await wait(30);
                assert(fields.disabled && save.disabled && releaseSave, 'editable during save');
                releaseSave(); await wait(30); mode = 'normal';
                assert(save.disabled && !fields.disabled && config.EnableRatingCompatibility && config.FutureSetting === 'preserve', 'save/preserve failed');
                pass('保存中锁定控件，成功后保持干净状态与无关配置');
                change(enabled, false); mode = 'savefail'; save.click(); await wait(30);
                assert(!save.disabled && !fields.disabled && !enabled.checked && status.textContent.includes('失败'), 'failed save lost selection');
                mode = 'normal'; save.click(); await wait(30);
                assert(!config.EnableRatingCompatibility && save.disabled, 'retry failed');
                pass('保存失败保留选择，可以重试');
                hide(); mode = 'readfail'; show(); await wait(30);
                assert(fields.disabled && save.disabled && !retry.hidden, 'read failure enabled stale controls');
                mode = 'normal'; retry.click(); await wait(30);
                assert(!fields.disabled && retry.hidden, 'read retry failed');
                pass('读取失败不会提交旧设置，可在页内重试');
                change(enabled, true); mode = 'delay'; save.click(); await wait(30);
                hide(); mode = 'normal'; show(); await wait(30);
                assert(!enabled.checked && save.disabled, 'reopened state incorrect');
                releaseSave(); await wait(30);
                assert(!enabled.checked && save.disabled && !status.textContent.includes('已保存。'), 'old response overwrote reopened page');
                pass('离开后返回，旧保存响应不会覆盖新页面');
                hide(); show(); await wait(30);
                assert(enabled.checked && config.CompatibilityRules.length === 1, 'persisted scope missing');
                pass('重新进入读取服务器确认的账号与库范围');
                report.textContent += 'ALL 6 CHECKS PASSED';
            } catch (error) { report.textContent += `FAIL ${error.message}`; }
            finally { button.disabled = false; }
        };
        show();
    });
})();
