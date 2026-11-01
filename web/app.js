(function () {
    'use strict';

    var state = {
        dirs: [],
        dirId: null,
        folder: '',
        page: 1,
        size: 100,
        total: 0,
        sort: 'mtime',
        desc: true,
        concurrent: 3,
        uploads: [],
        running: 0,
        decisions: {},
        expanded: { '': true },
        tree: null,
        openModals: 0,
        lastSignature: null,
        listSeq: 0,
        panelCollapsed: false,
        conflictKind: 'conflict',
        conflictItem: null,
        renaming: null,
        folderAction: null,
        confirmResolver: null,
        conflictResolver: null,
        rosterEnabled: false,
        rosterNames: [],
        lastRosterSignature: null,
        rosterResolver: null,
        personResolver: null
    };

    var uploadSeq = 0;
    var batchSeq = 0;
    var speedSamples = [];
    var speedValue = 0;
    var progressSignature = '';
    var conflictQueue = [];
    var conflictBusy = false;
    var lastCollapsed = null;
    var rosterDragging = false;
    var uploadNodes = {};
    var worker = null;
    var refreshSoon = null;
    var tickCount = 0;
    var refreshLeft = 10;
    var refreshing = false;

    function $(id) {
        return document.getElementById(id);
    }

    function esc(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function formatSize(bytes) {
        if (bytes == null || bytes < 0) return '';
        if (bytes < 1024) return bytes + ' B';
        var units = ['KB', 'MB', 'GB', 'TB'];
        var value = bytes / 1024;
        var index = 0;
        while (value >= 1024 && index < units.length - 1) {
            value /= 1024;
            index++;
        }
        return (value >= 100 ? value.toFixed(0) : value.toFixed(value >= 10 ? 1 : 2)) + ' ' + units[index];
    }

    function extOf(name) {
        var index = String(name).lastIndexOf('.');
        if (index <= 0 || index === name.length - 1) return '';
        return String(name).slice(index + 1).toLowerCase();
    }

    function splitName(name) {
        var index = String(name).lastIndexOf('.');
        if (index <= 0) return { base: String(name), ext: '' };
        return { base: String(name).slice(0, index), ext: String(name).slice(index) };
    }

    var ICONS = {
        slides: { cls: 'ext-ppt', icon: 'bi-file-earmark-slides' },
        word: { cls: 'ext-doc', icon: 'bi-file-earmark-word' },
        excel: { cls: 'ext-xls', icon: 'bi-file-earmark-excel' },
        pdf: { cls: 'ext-pdf', icon: 'bi-file-earmark-pdf' },
        zip: { cls: 'ext-zip', icon: 'bi-file-earmark-zip' },
        video: { cls: 'ext-video', icon: 'bi-file-earmark-play' },
        audio: { cls: 'ext-audio', icon: 'bi-file-earmark-music' },
        image: { cls: 'ext-image', icon: 'bi-file-earmark-image' },
        code: { cls: 'ext-code', icon: 'bi-file-earmark-code' },
        text: { cls: '', icon: 'bi-file-earmark-text' },
        file: { cls: '', icon: 'bi-file-earmark' }
    };

    function iconOf(ext) {
        switch (ext) {
            case 'ppt': case 'pptx': case 'pps': case 'ppsx': case 'pot': case 'potx':
                return ICONS.slides;
            case 'doc': case 'docx':
                return ICONS.word;
            case 'xls': case 'xlsx':
                return ICONS.excel;
            case 'pdf':
                return ICONS.pdf;
            case 'zip': case 'rar': case '7z':
                return ICONS.zip;
            case 'mp4': case 'avi': case 'mkv': case 'mov': case 'wmv':
                return ICONS.video;
            case 'mp3': case 'wav':
                return ICONS.audio;
            case 'png': case 'jpg': case 'jpeg': case 'gif': case 'webp':
                return ICONS.image;
            case 'html': case 'htm':
                return ICONS.code;
            case 'txt':
                return ICONS.text;
            default:
                return ICONS.file;
        }
    }

    /* ---------------- HTTP ---------------- */

    async function api(path, options) {
        var response;
        try {
            response = await fetch(path, options);
        } catch (err) {
            markConnection(false);
            throw err;
        }
        markConnection(true);
        if (response.status === 401) {
            location.href = '/';
            throw new Error('登录已失效');
        }
        return response;
    }

    var connected = false;

    function markConnection(ok) {
        if (ok === connected) return;
        connected = ok;
        var dot = $('connDot');
        if (!dot) return;
        dot.classList.toggle('ok', ok);
        dot.title = ok ? '已连接' : '未连接';
    }

    function heartbeat() {
        fetch('/api/status', { cache: 'no-store' }).then(function (response) {
            markConnection(true);
            return response.json();
        }).then(function (data) {
            if (data) updateDisks(data.disks);
        }).catch(function () {
            markConnection(false);
        });
    }

    function sizeText(bytes) {
        if (bytes >= 1024 * 1024 * 1024) {
            return (bytes / (1024 * 1024 * 1024)).toFixed(1) + ' GB';
        }
        return Math.round(bytes / (1024 * 1024)) + ' MB';
    }

    var diskCache = [];

    function updateDisks(disks) {
        diskCache = disks || [];
        var button = $('btnDiskSpace');
        if (!button) return;
        button.classList.toggle('d-none', !diskCache.length);
    }

    function showDiskSpace() {
        var list = $('diskList');
        list.textContent = '';
        for (var i = 0; i < diskCache.length; i++) {
            var line = document.createElement('div');
            line.className = 'disk-line';
            line.textContent = diskCache[i].name + '剩余可用：' + sizeText(diskCache[i].free);
            list.appendChild(line);
        }
        modal('diskModal').show();
    }

    async function getJson(path) {
        var response = await api(path);
        return response.json();
    }

    async function postJson(path, payload) {
        var response = await api(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload || {})
        });
        var data = null;
        try { data = await response.json(); } catch (e) { }
        if (!data) throw new Error('服务器无响应');
        return data;
    }

    function delay(ms) {
        return new Promise(function (resolve) { setTimeout(resolve, ms); });
    }

    function toast(message) {
        var container = $('toastHost');
        if (!container) {
            container = document.createElement('div');
            container.id = 'toastHost';
            container.className = 'toast-host';
            document.body.appendChild(container);
        }
        var el = document.createElement('div');
        el.className = 'toast-item';
        el.textContent = message;
        container.appendChild(el);
        setTimeout(function () {
            el.classList.add('out');
            setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 200);
        }, 2600);
    }

    /* ---------------- modals ---------------- */

    function modal(id) {
        return bootstrap.Modal.getOrCreateInstance($(id));
    }

    function trackModals() {
        document.addEventListener('show.bs.modal', function () { state.openModals++; });
        document.addEventListener('hidden.bs.modal', function () { state.openModals = Math.max(0, state.openModals - 1); });

        $('confirmApply').addEventListener('click', function () {
            var resolver = state.confirmResolver;
            state.confirmResolver = null;
            modal('confirmModal').hide();
            if (resolver) resolver(true);
        });
        $('confirmModal').addEventListener('hidden.bs.modal', function () {
            var resolver = state.confirmResolver;
            state.confirmResolver = null;
            if (resolver) resolver(false);
        });

        $('conflictApply').addEventListener('click', function () { finishConflict(); });
        $('conflictSkip').addEventListener('click', function () { closeConflict('skip'); });
        $('conflictModal').addEventListener('hidden.bs.modal', function () {
            var resolver = state.conflictResolver;
            state.conflictResolver = null;
            state.conflictItem = null;
            if (resolver) resolver('skip');
            conflictBusy = false;
            showNextConflict();
        });

        $('renameApply').addEventListener('click', applyRename);
        $('renameInput').addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                applyRename();
            }
        });
        $('renameModal').addEventListener('hidden.bs.modal', function () {
            var ctx = state.renaming;
            state.renaming = null;
            if (ctx && ctx.resolve) ctx.resolve(false);
        });

        $('folderApply').addEventListener('click', applyFolderAction);
        $('folderInput').addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                applyFolderAction();
            }
        });
        $('folderModal').addEventListener('hidden.bs.modal', function () {
            $('folderApply').disabled = false;
            state.folderAction = null;
        });

        $('rosterApply').addEventListener('click', function () {
            var names = parseRosterNames($('rosterInput').value);
            if (!names.length) {
                $('rosterError').textContent = '名字不能为空';
                $('rosterError').classList.remove('d-none');
                return;
            }
            var resolver = state.rosterResolver;
            state.rosterResolver = null;
            modal('rosterModal').hide();
            if (resolver) resolver(names);
        });
        $('rosterClear').addEventListener('click', async function () {
            var ok = await askConfirm('清除讲者名单', '确定清除讲者名单并关闭？', true);
            if (!ok) return;
            var resolver = state.rosterResolver;
            state.rosterResolver = null;
            modal('rosterModal').hide();
            if (resolver) resolver(null);
            applyRoster([], false);
        });
        $('rosterModal').addEventListener('hidden.bs.modal', function () {
            var resolver = state.rosterResolver;
            state.rosterResolver = null;
            if (resolver) resolver(null);
        });
        $('personModal').addEventListener('hidden.bs.modal', function () {
            var resolver = state.personResolver;
            state.personResolver = null;
            if (resolver) resolver(null);
        });

        $('previewModal').addEventListener('hidden.bs.modal', function () {
            $('previewBody').textContent = '';
        });
    }

    function askConfirm(title, message, danger) {
        return new Promise(function (resolve) {
            state.confirmResolver = resolve;
            $('confirmTitle').textContent = title;
            $('confirmMessage').textContent = message;
            $('confirmApply').className = 'btn ' + (danger ? 'btn-danger' : 'btn-primary');
            modal('confirmModal').show();
        });
    }

    function parseRosterNames(text) {
        var normalized = String(text || '').replace(/　/g, ' ');
        return normalized.split(/[,，;；\r\n]+/).map(function (part) {
            return part.trim();
        }).filter(function (part) {
            return part.length > 0;
        });
    }

    function openRosterDialog(names) {
        return new Promise(function (resolve) {
            state.rosterResolver = resolve;
            $('rosterInput').value = (names || []).join(', ');
            $('rosterError').classList.add('d-none');
            modal('rosterModal').show();
        });
    }

    async function applyRoster(names, enabled) {
        var result;
        try {
            result = await postJson('/api/roster', {
                dirId: state.dirId,
                folder: state.folder,
                names: names,
                enabled: enabled
            });
        } catch (err) {
            toast(err && err.message ? err.message : '保存失败');
            result = null;
        }
        if (result && !result.ok) toast(result.error || '保存失败');
        else if (result && result.failed && result.failed.length) {
            toast(result.failed[0].name + ' ' + (result.failed[0].error || '创建失败'));
        }
        state.lastSignature = null;
        state.lastRosterSignature = null;
        loadList(true);
    }

    async function onRosterToggle() {
        var checkbox = $('rosterToggle');
        var on = checkbox.checked;
        var names = state.rosterNames.slice();
        if (on && !names.length) {
            names = await openRosterDialog(names);
            if (!names) {
                checkbox.checked = false;
                return;
            }
        }
        applyRoster(names, on);
    }

    async function editRoster() {
        var names = await openRosterDialog(state.rosterNames);
        if (!names) return;
        applyRoster(names, true);
    }

    function choosePerson() {
        return new Promise(function (resolve) {
            state.personResolver = resolve;
            var host = $('personList');
            host.textContent = '';
            state.rosterNames.forEach(function (name) {
                var button = document.createElement('button');
                button.type = 'button';
                button.className = 'list-group-item list-group-item-action';
                button.textContent = name;
                button.addEventListener('click', function () {
                    state.personResolver = null;
                    modal('personModal').hide();
                    resolve(name);
                });
                host.appendChild(button);
            });
            modal('personModal').show();
        });
    }

    function askConflict(kind, item, existing) {
        return new Promise(function (resolve) {
            conflictQueue.push({ kind: kind, item: item, existing: existing || null, resolve: resolve });
            showNextConflict();
        });
    }

    function formatDateTime(ms) {
        if (!ms) return '';
        var date = new Date(ms);
        function pad(value) { return value < 10 ? '0' + value : '' + value; }
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) +
            ' ' + pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
    }

    function addConflictLine(label, text) {
        var row = document.createElement('div');
        row.textContent = (label ? label + '：' : '') + (text || '');
        $('conflictInfo').appendChild(row);
    }

    function fillConflictInfo(kind, item, existing) {
        var host = $('conflictInfo');
        host.textContent = '';
        if (kind === 'conflict') {
            addConflictLine('旧', formatSize(existing && existing.size) + ' · ' +
                ((existing && existing.time) || ''));
            addConflictLine('新', formatSize(item.size) + ' · ' +
                formatDateTime(item.file && item.file.lastModified));
        } else if (existing && existing.path) {
            addConflictLine('', existing.path);
        }
    }

    function showNextConflict() {
        if (conflictBusy) return;
        var task = null;
        while (conflictQueue.length) {
            var next = conflictQueue.shift();
            if (next.item && (next.item.canceled || next.item.status === 'canceled')) {
                next.resolve('skip');
                continue;
            }
            var remembered = state.decisions[next.kind];
            if (remembered) {
                next.resolve(remembered);
                continue;
            }
            task = next;
            break;
        }
        if (!task) return;
        conflictBusy = true;
        state.conflictKind = task.kind;
        state.conflictItem = task.item;
        state.conflictResolver = task.resolve;
        $('conflictTitle').textContent = task.kind === 'duplicate' ? '重复文件' : '同名文件';
        $('conflictName').textContent = task.item.name;
        fillConflictInfo(task.kind, task.item, task.existing);
        $('conflictRemember').checked = false;

        var options;
        if (task.kind === 'duplicate') {
            options = [
                { value: 'skip', label: '跳过' },
                { value: 'rename', label: '改名保存' }
            ];
        } else {
            options = [
                { value: 'overwrite', label: '覆盖' },
                { value: 'rename', label: '改名保存' },
                { value: 'skip', label: '跳过' }
            ];
        }
        var host = $('conflictOptions');
        host.textContent = '';
        options.forEach(function (option, index) {
            var wrap = document.createElement('div');
            wrap.className = 'form-check';
            var radio = document.createElement('input');
            radio.className = 'form-check-input';
            radio.type = 'radio';
            radio.name = 'conflictOption';
            radio.id = 'conflictOption' + index;
            radio.value = option.value;
            radio.checked = index === 0;
            var label = document.createElement('label');
            label.className = 'form-check-label';
            label.htmlFor = radio.id;
            label.textContent = option.label;
            wrap.appendChild(radio);
            wrap.appendChild(label);
            host.appendChild(wrap);
        });
        modal('conflictModal').show();
    }

    function closeConflict(action, applyRemember) {
        var resolver = state.conflictResolver;
        if (!resolver) return;
        state.conflictResolver = null;
        state.conflictItem = null;
        if (applyRemember !== false && $('conflictRemember').checked) {
            state.decisions[state.conflictKind] = action;
        }
        modal('conflictModal').hide();
        resolver(action);
    }

    function finishConflict() {
        var selected = document.querySelector('input[name="conflictOption"]:checked');
        closeConflict(selected ? selected.value : 'skip');
    }

    function leafOf(path) {
        var index = path.lastIndexOf('\\');
        return index >= 0 ? path.substring(index + 1) : path;
    }

    function openFolderDialog(mode, path) {
        state.folderAction = { mode: mode, path: path || '' };
        $('folderTitle').textContent = mode === 'rename' ? '重命名文件夹' : '新建文件夹';
        $('folderInput').value = mode === 'rename' ? leafOf(path) : '';
        $('folderError').classList.add('d-none');
        $('folderApply').disabled = false;
        modal('folderModal').show();
        setTimeout(function () {
            var input = $('folderInput');
            input.focus();
            input.select();
        }, 200);
    }

    function openNewFolder() {
        if (!state.dirId) {
            toast('尚未添加目录');
            return;
        }
        openFolderDialog('create', '');
    }

    async function applyFolderAction() {
        var action = state.folderAction;
        if (!action) return;
        var name = $('folderInput').value.trim();
        var errorEl = $('folderError');
        if (!name) {
            errorEl.textContent = '文件夹名不能为空';
            errorEl.classList.remove('d-none');
            return;
        }
        if (action.mode === 'rename' && name === leafOf(action.path)) {
            modal('folderModal').hide();
            return;
        }
        var button = $('folderApply');
        button.disabled = true;
        try {
            var result;
            if (action.mode === 'rename') {
                result = await postJson('/api/folder/rename', {
                    dirId: state.dirId,
                    path: action.path,
                    newName: name
                });
            } else {
                result = await postJson('/api/folder/create', {
                    dirId: state.dirId,
                    folder: state.folder,
                    name: name
                });
            }
            if (!result.ok) {
                errorEl.textContent = result.error || '操作失败';
                errorEl.classList.remove('d-none');
                return;
            }
            modal('folderModal').hide();
            if (action.mode === 'rename') {
                rebaseFolder(action.path, result.path);
            } else {
                expandPath(state.folder);
                loadTree(true);
            }
        } catch (err) {
            errorEl.textContent = err && err.message ? err.message : '操作失败';
            errorEl.classList.remove('d-none');
        } finally {
            button.disabled = false;
        }
    }

    function expandPath(path) {
        state.expanded[''] = true;
        if (!path) return;
        var segments = path.split('\\');
        var current = '';
        for (var i = 0; i < segments.length; i++) {
            current = current ? current + '\\' + segments[i] : segments[i];
            state.expanded[current] = true;
        }
    }

    function rebaseFolder(oldPath, newPath) {
        function map(path) {
            if (path === oldPath) return newPath;
            if (path.indexOf(oldPath + '\\') === 0) return newPath + path.substring(oldPath.length);
            return path;
        }
        var next = {};
        Object.keys(state.expanded).forEach(function (key) {
            next[map(key)] = state.expanded[key];
        });
        state.expanded = next;

        var folder = map(state.folder || '');
        if (folder !== state.folder) {
            state.folder = folder;
            state.page = 1;
            state.lastSignature = null;
            state.lastRosterSignature = null;
        }
        expandPath(state.folder || '');
        loadTree(true);
        loadList(true);
    }

    function fullFolderPath(path) {
        var dir = state.dirs.find(function (d) { return d.id === state.dirId; });
        var root = dir && dir.path ? dir.path : '';
        if (!root) return path;
        if (!path) return root;
        return root.replace(/[\\\/]+$/, '') + '\\' + path;
    }

    async function deleteFolder(path) {
        if (!path) return;
        var ok = await askConfirm('删除文件夹', fullFolderPath(path), true);
        if (!ok) return;
        try {
            var result = await postJson('/api/folder/delete', {
                dirId: state.dirId,
                path: path
            });
            if (!result.ok) {
                toast(result.error || '删除失败');
                return;
            }
            if (state.folder === path || state.folder.indexOf(path + '\\') === 0) {
                state.folder = path.indexOf('\\') >= 0
                    ? path.substring(0, path.lastIndexOf('\\'))
                    : '';
                state.page = 1;
                state.lastSignature = null;
                state.lastRosterSignature = null;
                loadList(true);
            } else {
                state.lastSignature = null;
                state.lastRosterSignature = null;
                loadList(true);
            }
            loadTree(true);
        } catch (err) {
            toast(err && err.message ? err.message : '删除失败');
        }
    }

    var treeMenu = null;

    function hideTreeMenu() {
        if (!treeMenu) return;
        if (treeMenu.parentNode) treeMenu.parentNode.removeChild(treeMenu);
        treeMenu = null;
    }

    function showTreeMenu(x, y, path, isRoot) {
        hideTreeMenu();
        var menu = document.createElement('div');
        menu.className = 'tree-menu';

        function addItem(text, danger, handler) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'tree-menu-item' + (danger ? ' danger' : '');
            button.textContent = text;
            button.addEventListener('click', function () {
                hideTreeMenu();
                handler();
            });
            menu.appendChild(button);
        }

        addItem('下载', false, function () { downloadFolder(path); });
        if (!isRoot) {
            addItem('重命名', false, function () { openFolderDialog('rename', path); });
            addItem('删除', true, function () { deleteFolder(path); });
        }

        document.body.appendChild(menu);
        var left = Math.min(x, window.innerWidth - menu.offsetWidth - 8);
        var top = Math.min(y, window.innerHeight - menu.offsetHeight - 8);
        menu.style.left = Math.max(4, left) + 'px';
        menu.style.top = Math.max(4, top) + 'px';
        treeMenu = menu;
    }

    function openRename(file) {
        return new Promise(function (resolve) {
            var parts = splitName(file.name);
            state.renaming = { file: file, resolve: resolve };
            $('renameInput').value = parts.base;
            $('renameExt').textContent = parts.ext;
            $('renameError').classList.add('d-none');
            modal('renameModal').show();
            setTimeout(function () {
                var input = $('renameInput');
                input.focus();
                input.select();
            }, 200);
        });
    }

    async function applyRename() {
        var ctx = state.renaming;
        if (!ctx) return;
        var parts = splitName(ctx.file.name);
        var base = $('renameInput').value.trim();
        var errorEl = $('renameError');
        if (!base) {
            errorEl.textContent = '文件名不能为空';
            errorEl.classList.remove('d-none');
            return;
        }
        var newName = base + parts.ext;
        if (newName === ctx.file.name) {
            modal('renameModal').hide();
            return;
        }
        var button = $('renameApply');
        button.disabled = true;
        try {
            var result = await postJson('/api/file/rename', {
                dirId: state.dirId,
                path: ctx.file.path,
                newName: newName
            });
            if (!result.ok) {
                errorEl.textContent = result.error || '重命名失败';
                errorEl.classList.remove('d-none');
                return;
            }
            state.renaming = null;
            modal('renameModal').hide();
            ctx.resolve(true);
            loadTree(true);
            loadList(true);
        } catch (err) {
            errorEl.textContent = err.message || '重命名失败';
            errorEl.classList.remove('d-none');
        } finally {
            button.disabled = false;
        }
    }

    /* ---------------- status / tabs / tree ---------------- */

    async function refreshStatus() {
        var data = await getJson('/api/status');
        updateDisks(data.disks);
        state.concurrent = Math.max(1, data.concurrent || 3);
        state.dirs = data.dirs || [];
        if (!state.dirId || !state.dirs.some(function (d) { return d.id === state.dirId; })) {
            state.dirId = state.dirs.length ? state.dirs[0].id : null;
            state.folder = '';
            state.page = 1;
            state.expanded = { '': true };
            state.tree = null;
        }
        renderTabs();
        updatePickButton();
        return data;
    }

    function renderTabs() {
        var host = $('dirTabs');
        host.textContent = '';
        state.dirs.forEach(function (dir) {
            var li = document.createElement('li');
            li.className = 'nav-item';
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'nav-link' + (dir.id === state.dirId ? ' active' : '');
            var icon = document.createElement('i');
            icon.className = 'bi ' + (dir.id === state.dirId ? 'bi-folder2-open' : 'bi-folder2');
            var name = document.createElement('span');
            name.textContent = ' ' + dir.name;
            button.appendChild(icon);
            button.appendChild(name);
            if (dir.indexing) {
                var spin = document.createElement('i');
                spin.className = 'bi bi-arrow-repeat spin ms-1';
                spin.title = '索引中 ' + (dir.indexDone || 0) + ' / ' + (dir.indexTotal || 0);
                button.appendChild(spin);
            } else if (dir.count) {
                var count = document.createElement('span');
                count.className = 'tab-count';
                count.textContent = dir.count > 999 ? '999+' : dir.count;
                button.appendChild(count);
            }
            button.addEventListener('click', function () { switchDir(dir.id); });
            li.appendChild(button);
            host.appendChild(li);
        });
    }

    function updatePickButton() {
        var pick = $('btnPick');
        if (pick) pick.disabled = !state.dirId;
        var toggle = $('rosterToggle');
        if (toggle) toggle.disabled = !state.dirId;
        var sort = $('sortSelect');
        if (sort) sort.classList.toggle('d-none', state.rosterEnabled);
    }

    async function switchDir(id) {
        if (id === state.dirId) return;
        state.dirId = id;
        state.folder = '';
        state.page = 1;
        state.expanded = { '': true };
        state.tree = null;
        state.lastSignature = null;
        state.lastRosterSignature = null;
        state.listSeq++;
        renderTabs();
        await loadTree(true);
        await loadList(true);
    }

    async function loadTree(force) {
        if (!state.dirId) {
            $('tree').textContent = '';
            return;
        }
        try {
            var data = await getJson('/api/tree?dirId=' + encodeURIComponent(state.dirId));
            if (!data.tree) return;
            state.tree = data.tree;
            renderTree();
        } catch (err) {
            if (force) toast(err.message || '目录加载失败');
        }
    }

    function renderTree() {
        var host = $('tree');
        var scroll = host.scrollTop;
        host.textContent = '';
        if (!state.tree) return;
        host.appendChild(buildTreeNode(state.tree, true));
        host.scrollTop = scroll;
    }

    function buildTreeNode(node, isRoot) {
        var path = node.path || '';
        var box = document.createElement('div');
        box.className = 'tree-node' + (state.expanded[path] ? ' open' : '');

        var row = document.createElement('div');
        row.className = 'tree-row' + (state.folder === path ? ' active' : '');

        var toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'tree-toggle';
        var caret = document.createElement('i');
        caret.className = 'bi bi-chevron-right';
        toggle.appendChild(caret);
        if (!node.children || !node.children.length) toggle.style.visibility = 'hidden';
        toggle.addEventListener('click', function (event) {
            event.stopPropagation();
            state.expanded[path] = !state.expanded[path];
            box.classList.toggle('open', !!state.expanded[path]);
        });

        var icon = document.createElement('i');
        if (isRoot) {
            icon.className = 'bi bi-folder-fill';
        } else {
            icon.className = 'bi ' + (state.folder === path ? 'bi-folder2-open' : 'bi-folder2');
        }

        var label = document.createElement('span');
        label.className = 'tree-name';
        label.textContent = isRoot ? '根目录' : node.name;
        label.title = isRoot ? node.name : path;

        row.appendChild(toggle);
        row.appendChild(icon);
        row.appendChild(label);
        row.addEventListener('click', function () {
            selectFolder(path);
        });
        row.addEventListener('contextmenu', function (event) {
            event.preventDefault();
            showTreeMenu(event.clientX, event.clientY, path, isRoot);
        });

        box.appendChild(row);

        if (node.children && node.children.length) {
            var children = document.createElement('div');
            children.className = 'tree-children';
            node.children.forEach(function (child) {
                children.appendChild(buildTreeNode(child, false));
            });
            box.appendChild(children);
        }
        return box;
    }

    function selectFolder(path) {
        if (state.folder === path) return;
        state.folder = path;
        state.page = 1;
        state.lastSignature = null;
        state.lastRosterSignature = null;
        state.listSeq++;
        if (path && state.expanded[path] === undefined) {
            var segments = path.split('\\');
            var current = '';
            state.expanded[''] = true;
            for (var i = 0; i < segments.length; i++) {
                current = current ? current + '\\' + segments[i] : segments[i];
                state.expanded[current] = true;
            }
        }
        renderTree();
        loadList(true);
    }

    function renderCrumbs() {
        var host = $('crumbs');
        host.textContent = '';
        var segments = state.folder ? state.folder.split('\\') : [];
        var rootButton = document.createElement('button');
        rootButton.type = 'button';
        rootButton.className = 'crumb' + (segments.length === 0 ? ' current' : '');
        rootButton.textContent = '根目录';
        rootButton.addEventListener('click', function () { selectFolder(''); });
        host.appendChild(rootButton);

        var current = '';
        segments.forEach(function (segment, index) {
            var sep = document.createElement('i');
            sep.className = 'bi bi-chevron-right crumb-sep';
            host.appendChild(sep);
            current = current ? current + '\\' + segment : segment;
            var target = current;
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'crumb' + (index === segments.length - 1 ? ' current' : '');
            button.textContent = segment;
            button.title = target;
            button.addEventListener('click', function () { selectFolder(target); });
            host.appendChild(button);
        });
    }

    /* ---------------- file list ---------------- */

    function fileUrl(file) {
        return '/api/file?dirId=' + encodeURIComponent(state.dirId) +
            '&path=' + encodeURIComponent(file.path) + '&mode=attachment';
    }

    function inlineUrl(file) {
        return '/api/file?dirId=' + encodeURIComponent(state.dirId) +
            '&path=' + encodeURIComponent(file.path);
    }

    var packTasks = {};

    function downloadFolder(path) {
        var url = '/api/folder/prepare?dirId=' + encodeURIComponent(state.dirId) +
            '&path=' + encodeURIComponent(path || '');
        getJson(url).then(function (res) {
            if (!packTasks[res.token]) addPackTask(res.token, res.name);
            pollPack(res.token);
        }).catch(function (err) {
            toast(err.message || '打包失败');
        });
    }

    function addPackTask(token, name) {
        var row = document.createElement('div');
        row.className = 'download-item';

        var top = document.createElement('div');
        top.className = 'upload-top';
        var label = document.createElement('span');
        label.className = 'upload-name';
        label.textContent = name + '.zip';
        var stateEl = document.createElement('span');
        stateEl.className = 'upload-state';
        stateEl.textContent = '0%';
        var cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'download-cancel';
        cancel.title = '取消';
        var cancelIcon = document.createElement('i');
        cancelIcon.className = 'bi bi-x-lg';
        cancel.appendChild(cancelIcon);
        cancel.addEventListener('click', function () { cancelPack(token); });
        top.appendChild(label);
        top.appendChild(stateEl);
        top.appendChild(cancel);

        var prog = document.createElement('div');
        prog.className = 'progress download-progress';
        var bar = document.createElement('div');
        bar.className = 'progress-bar';
        bar.style.width = '0%';
        prog.appendChild(bar);

        row.appendChild(top);
        row.appendChild(prog);
        $('downloadList').appendChild(row);
        $('downloadPanel').classList.add('active');

        packTasks[token] = {
            el: row, bar: bar, state: stateEl, name: name, timer: null, cancel: cancel
        };
    }

    function cancelPack(token) {
        var task = packTasks[token];
        if (!task) return;
        task.cancelled = true;
        clearTimeout(task.timer);
        if (task.cancel.parentNode) task.cancel.parentNode.removeChild(task.cancel);
        task.state.textContent = '已取消';
        task.bar.classList.add('bg-secondary');
        removePackTask(token, 1500);
        postJson('/api/folder/cancel', { id: token }).catch(function () { });
    }

    function removePackTask(token, delay) {
        var task = packTasks[token];
        if (!task) return;
        if (delay) {
            task.timer = setTimeout(function () { removePackTask(token); }, delay);
            return;
        }
        clearTimeout(task.timer);
        if (task.el.parentNode) task.el.parentNode.removeChild(task.el);
        delete packTasks[token];
        if (!Object.keys(packTasks).length) $('downloadPanel').classList.remove('active');
    }

    function pollPack(token) {
        var task = packTasks[token];
        if (!task) return;
        getJson('/api/folder/status?token=' + encodeURIComponent(token)).then(function (res) {
            var current = packTasks[token];
            if (!current || current.cancelled) return;
            if (res.state === 'ready') {
                var link = document.createElement('a');
                link.href = '/api/folder/download?token=' + encodeURIComponent(token);
                link.download = current.name + '.zip';
                document.body.appendChild(link);
                link.click();
                link.remove();
                current.bar.style.width = '100%';
                current.state.textContent = '已开始下载';
                if (res.skipped) toast('已跳过 ' + res.skipped + ' 个无法读取的文件');
                removePackTask(token, 3000);
                return;
            }
            if (res.state === 'error') {
                current.state.textContent = res.error || '打包失败';
                current.bar.classList.add('bg-danger');
                toast(res.error || '打包失败');
                removePackTask(token, 8000);
                return;
            }
            var pct = res.total > 0 ? Math.min(99, Math.round(res.done * 100 / res.total)) : 0;
            current.bar.style.width = pct + '%';
            current.state.textContent = pct + '%';
            current.timer = setTimeout(function () { pollPack(token); }, 500);
        }).catch(function (err) {
            var current = packTasks[token];
            if (!current || current.cancelled) return;
            current.state.textContent = err.message || '打包失败';
            toast(err.message || '打包失败');
            removePackTask(token, 8000);
        });
    }

    var PREVIEW_KINDS = {
        png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image',
        mp4: 'video', webm: 'video', mov: 'video', m4v: 'video',
        mp3: 'audio', wav: 'audio', m4a: 'audio', ogg: 'audio',
        pdf: 'pdf', txt: 'text', html: 'html', htm: 'html'
    };

    function openPreview(file, kind) {
        var body = $('previewBody');
        body.textContent = '';
        $('previewTitle').textContent = file.name;
        var url = inlineUrl(file);
        if (kind === 'image') {
            var image = document.createElement('img');
            image.className = 'preview-media';
            image.src = url;
            image.alt = file.name;
            body.appendChild(image);
        } else if (kind === 'video') {
            var video = document.createElement('video');
            video.className = 'preview-media';
            video.src = url;
            video.controls = true;
            video.muted = true;
            body.appendChild(video);
        } else if (kind === 'audio') {
            var audio = document.createElement('audio');
            audio.src = url;
            audio.controls = true;
            body.appendChild(audio);
        } else if (kind === 'pdf') {
            var frame = document.createElement('iframe');
            frame.className = 'preview-frame';
            frame.src = url;
            body.appendChild(frame);
        } else if (kind === 'html') {
            var htmlFrame = document.createElement('iframe');
            htmlFrame.className = 'preview-frame';
            htmlFrame.setAttribute('sandbox', '');
            htmlFrame.src = url;
            body.appendChild(htmlFrame);
        } else if (kind === 'text') {
            var pre = document.createElement('pre');
            pre.className = 'preview-text';
            body.appendChild(pre);
            fetch(url).then(function (response) {
                return response.text();
            }).then(function (text) {
                pre.textContent = text;
            }).catch(function () {
                pre.textContent = '加载失败';
            });
        }
        modal('previewModal').show();
    }

    async function loadRosterInfo(dirId, folder) {
        var targetFolder = folder == null ? '' : folder;
        var data = null;
        try {
            data = await getJson('/api/roster?dirId=' + encodeURIComponent(dirId || '') +
                '&folder=' + encodeURIComponent(targetFolder));
        } catch (err) {
            data = null;
        }
        if (dirId !== state.dirId || targetFolder !== state.folder) return null;
        state.rosterEnabled = !!(data && data.enabled);
        state.rosterNames = (data && data.names) || [];
        $('rosterToggle').checked = state.rosterEnabled;
        $('btnRosterEdit').classList.toggle('d-none', !state.rosterEnabled);
        $('btnRosterShot').classList.toggle('d-none', !state.rosterEnabled);
        updatePickButton();
        return data;
    }

    async function loadList(force) {
        if (!state.dirId) {
            renderNoDirectories();
            return;
        }
        var seq = ++state.listSeq;
        var dirId = state.dirId;
        var folder = state.folder;
        var roster = await loadRosterInfo(dirId, folder);
        if (seq !== state.listSeq || dirId !== state.dirId || folder !== state.folder) return;
        if (roster && roster.enabled) {
            renderRoster(roster.stats || [], force);
            return;
        }
        var params = 'dirId=' + encodeURIComponent(dirId) +
            '&folder=' + encodeURIComponent(folder) +
            '&page=' + state.page + '&size=' + state.size +
            '&sort=' + encodeURIComponent(state.sort) +
            '&order=' + (state.desc ? 'desc' : 'asc');
        var data;
        try {
            data = await getJson('/api/list?' + params);
        } catch (err) {
            if (force) toast(err.message || '列表加载失败');
            return;
        }
        if (seq !== state.listSeq || dirId !== state.dirId || folder !== state.folder) return;
        var files = data.files || [];
        var folders = data.folders || [];
        state.total = data.total || 0;
        $('fileCount').textContent = state.total + ' 个文件';
        var pages = Math.max(1, Math.ceil(state.total / state.size));
        if (state.page > pages) {
            state.page = pages;
            loadList(true);
            return;
        }

        renderCrumbs();
        var signature = state.page + '|' + state.total + '|' +
            JSON.stringify(folders) + '|' + JSON.stringify(files);
        if (!force && signature === state.lastSignature) {
            renderPager();
            return;
        }
        state.lastSignature = signature;
        renderFiles(folders, files);
        renderPager();
    }

    function renderNoDirectories() {
        var host = $('fileList');
        host.textContent = '';
        var empty = document.createElement('div');
        empty.className = 'empty';
        var icon = document.createElement('i');
        icon.className = 'bi bi-folder-plus';
        empty.appendChild(icon);
        var text = document.createElement('div');
        text.textContent = '尚未添加目录';
        empty.appendChild(text);
        host.appendChild(empty);
        $('pager').textContent = '';
        $('pager').classList.add('d-none');
        $('fileCount').textContent = '';
        state.rosterEnabled = false;
        state.rosterNames = [];
        $('rosterToggle').checked = false;
        $('btnRosterEdit').classList.add('d-none');
        $('btnRosterShot').classList.add('d-none');
        updatePickButton();
        renderCrumbs();
    }

    function renderRoster(items, force) {
        var uploaded = items.filter(function (item) {
            return (item.count || 0) > 0 || (item.folders || 0) > 0;
        });
        var pending = items.filter(function (item) {
            return !(item.count > 0) && !(item.folders > 0);
        });
        $('fileCount').textContent = '已上传 ' + uploaded.length + ' / ' + items.length;
        $('pager').textContent = '';
        $('pager').classList.add('d-none');
        renderCrumbs();

        if (rosterDragging) return;
        var signature = 'roster|' + JSON.stringify(items);
        if (!force && signature === state.lastRosterSignature) return;
        state.lastRosterSignature = signature;

        var host = $('fileList');
        var scroll = host.scrollTop;
        host.textContent = '';
        if (!items.length) {
            var empty = document.createElement('div');
            empty.className = 'empty';
            var icon = document.createElement('i');
            icon.className = 'bi bi-people';
            empty.appendChild(icon);
            var text = document.createElement('div');
            text.textContent = '未设置名字';
            empty.appendChild(text);
            host.appendChild(empty);
            host.scrollTop = 0;
            return;
        }
        host.appendChild(buildRosterGroup('未上传', pending));
        host.appendChild(buildRosterGroup('已上传', uploaded));
        host.scrollTop = scroll;
    }

    function buildRosterGroup(title, items) {
        var group = document.createElement('div');
        group.className = 'roster-group';
        var head = document.createElement('div');
        head.className = 'roster-group-title';
        head.textContent = title + ' ' + items.length;
        group.appendChild(head);
        var grid = document.createElement('div');
        grid.className = 'roster-grid';
        items.forEach(function (item) {
            grid.appendChild(buildRosterCard(item));
        });
        group.appendChild(grid);
        return group;
    }

    function buildRosterCard(item) {
        var count = item.count || 0;
        var folders = item.folders || 0;
        var uploaded = count > 0 || folders > 0;
        var folderPath = state.folder ? state.folder + '\\' + item.name : item.name;

        var card = document.createElement('div');
        card.className = 'roster-card' + (uploaded ? ' uploaded' : '');

        var icon = document.createElement('i');
        icon.className = 'bi ' + (uploaded ? 'bi-folder2-open' : 'bi-folder2');

        var main = document.createElement('div');
        main.className = 'roster-main';
        var nameEl = document.createElement('div');
        nameEl.className = 'roster-name';
        nameEl.textContent = item.name;
        nameEl.title = folderPath;
        main.appendChild(nameEl);
        if (uploaded) {
            var meta = document.createElement('div');
            meta.className = 'roster-meta';
            var parts = [];
            if (count > 0) parts.push(count + ' 个文件');
            if (folders > 0) parts.push(folders + ' 个文件夹');
            meta.textContent = parts.join(' · ');
            main.appendChild(meta);
        }

        card.appendChild(icon);
        card.appendChild(main);
        card.addEventListener('click', function () { selectFolder(folderPath); });
        card.addEventListener('contextmenu', function (event) {
            event.preventDefault();
            showTreeMenu(event.clientX, event.clientY, folderPath);
        });
        card.addEventListener('dragover', function (event) {
            if (!event.dataTransfer || !hasFiles(event.dataTransfer)) return;
            event.preventDefault();
            event.stopPropagation();
            event.dataTransfer.dropEffect = 'copy';
            rosterDragging = true;
            card.classList.add('dragover');
        });
        card.addEventListener('dragleave', function (event) {
            if (event.relatedTarget && card.contains(event.relatedTarget)) return;
            rosterDragging = false;
            card.classList.remove('dragover');
        });
        card.addEventListener('drop', function (event) {
            if (!event.dataTransfer) return;
            event.preventDefault();
            event.stopPropagation();
            rosterDragging = false;
            card.classList.remove('dragover');
            dragDepth = 0;
            $('dropHint').classList.remove('show');
            addDroppedFiles(event.dataTransfer, state.dirId, folderPath);
        });
        return card;
    }

    var shotBusy = false;
    var shotText = '';

    function rosterText(source) {
        var groups = [];
        Array.prototype.forEach.call(source.children, function (node) {
            var title = node.querySelector('.roster-group-title');
            var grid = node.querySelector('.roster-grid');
            if (!title || !grid) return;
            var names = [];
            Array.prototype.forEach.call(grid.children, function (card) {
                var name = card.querySelector('.roster-name');
                if (name && name.textContent) names.push(name.textContent);
            });
            groups.push({ title: title.textContent, names: names });
        });
        if (!groups.length) return '';

        function block(prefix) {
            for (var i = 0; i < groups.length; i++) {
                if (groups[i].title.indexOf(prefix) === 0) {
                    return groups[i].title + '\n' +
                        (groups[i].names.length ? groups[i].names.join('，') : '无');
                }
            }
            return prefix + ' 0\n无';
        }
        return block('已上传') + '\n\n' + block('未上传');
    }

    function copyPlainText(text) {
        var copied = false;
        function onCopy(event) {
            if (!event.clipboardData) return;
            event.clipboardData.setData('text/plain', text);
            event.preventDefault();
            copied = true;
        }
        document.addEventListener('copy', onCopy);

        var area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.left = '0';
        area.style.top = '0';
        area.style.width = '2px';
        area.style.height = '2px';
        area.style.padding = '0';
        area.style.border = 'none';
        area.style.background = 'transparent';
        document.body.appendChild(area);
        area.focus();
        area.select();
        area.setSelectionRange(0, text.length);

        var ok = false;
        try { ok = document.execCommand('copy'); } catch (err) { }
        area.remove();
        document.removeEventListener('copy', onCopy);
        return ok || copied;
    }

    function showRosterShot() {
        if (shotBusy || !window.html2canvas) return;
        var source = $('fileList');
        if (!source || !source.children.length) return;
        shotBusy = true;
        shotText = rosterText(source);

        var maxItems = 0;
        Array.prototype.forEach.call(source.children, function (node) {
            var grid = node.querySelector('.roster-grid');
            if (grid) maxItems = Math.max(maxItems, grid.children.length);
        });

        var stride = 178;
        var padding = 28;
        var fit = Math.floor((source.clientWidth - padding + 8) / stride);
        if (fit < 1) fit = 1;
        var columns = Math.max(1, Math.min(maxItems, fit));
        var width = maxItems ? columns * stride - 8 + padding : source.clientWidth;

        var host = document.createElement('div');
        host.style.position = 'fixed';
        host.style.left = '-10000px';
        host.style.top = '0';
        host.style.width = width + 'px';
        host.style.background = getComputedStyle(document.body).backgroundColor;
        Array.prototype.forEach.call(source.children, function (node) {
            host.appendChild(node.cloneNode(true));
        });
        document.body.appendChild(host);

        html2canvas(host, { backgroundColor: null, scale: 2, logging: false }).then(function (canvas) {
            $('shotImage').src = canvas.toDataURL('image/png');
            modal('shotModal').show();
        }).catch(function (err) {
            toast(err && err.message ? err.message : '截图失败');
        }).then(function () {
            if (host.parentNode) host.parentNode.removeChild(host);
            shotBusy = false;
        });
    }

    function renderFiles(folders, files) {
        var host = $('fileList');
        var scroll = host.scrollTop;
        host.textContent = '';
        if (!folders.length && !files.length) {
            var empty = document.createElement('div');
            empty.className = 'empty';
            var icon = document.createElement('i');
            icon.className = 'bi bi-inbox';
            empty.appendChild(icon);
            var text = document.createElement('div');
            text.textContent = '空文件夹';
            empty.appendChild(text);
            host.appendChild(empty);
            host.scrollTop = 0;
            return;
        }
        folders.forEach(function (folder) {
            host.appendChild(buildFolderRow(folder));
        });
        files.forEach(function (file) {
            host.appendChild(buildFileRow(file));
        });
        host.scrollTop = scroll;
    }

    function buildFolderRow(folder) {
        var row = document.createElement('div');
        row.className = 'file-row folder-row';

        var iconBox = document.createElement('div');
        iconBox.className = 'fi-icon';
        var icon = document.createElement('i');
        icon.className = 'bi bi-folder2';
        iconBox.appendChild(icon);

        var main = document.createElement('div');
        main.className = 'fi-main';
        var nameEl = document.createElement('div');
        nameEl.className = 'fi-name';
        nameEl.textContent = folder.name;
        nameEl.title = folder.path;
        main.appendChild(nameEl);
        if (folder.mtime) {
            var metaEl = document.createElement('div');
            metaEl.className = 'fi-meta';
            metaEl.textContent = folder.mtime;
            main.appendChild(metaEl);
        }

        var actions = document.createElement('div');
        actions.className = 'fi-actions';
        var download = document.createElement('button');
        download.type = 'button';
        download.className = 'icon-btn';
        download.title = '下载';
        var downloadIcon = document.createElement('i');
        downloadIcon.className = 'bi bi-download';
        download.appendChild(downloadIcon);
        download.addEventListener('click', function (event) {
            event.stopPropagation();
            downloadFolder(folder.path);
        });
        actions.appendChild(download);

        row.appendChild(iconBox);
        row.appendChild(main);
        row.appendChild(actions);
        row.addEventListener('click', function () { selectFolder(folder.path); });
        row.addEventListener('contextmenu', function (event) {
            event.preventDefault();
            showTreeMenu(event.clientX, event.clientY, folder.path);
        });
        return row;
    }

    function buildFileRow(file) {
        var ext = extOf(file.name);
        var meta = iconOf(ext);

        var row = document.createElement('div');
        row.className = 'file-row';

        var iconBox = document.createElement('div');
        iconBox.className = 'fi-icon';
        var icon = document.createElement('i');
        icon.className = 'bi ' + meta.icon + (meta.cls ? ' ' + meta.cls : '');
        iconBox.appendChild(icon);

        var main = document.createElement('div');
        main.className = 'fi-main';
        var nameEl = document.createElement('div');
        nameEl.className = 'fi-name';
        nameEl.textContent = file.name;
        nameEl.title = file.path;
        var metaEl = document.createElement('div');
        metaEl.className = 'fi-meta';
        metaEl.textContent = formatSize(file.size) + ' · ' + file.mtime;
        if (file.alert) {
            var alertEl = document.createElement('span');
            alertEl.className = 'fi-alert';
            alertEl.textContent = ' · ' + file.alert;
            metaEl.appendChild(alertEl);
        }
        main.appendChild(nameEl);
        main.appendChild(metaEl);

        var actions = document.createElement('div');
        actions.className = 'fi-actions';

        var buttons = [];
        var previewKind = PREVIEW_KINDS[ext];
        if (previewKind) {
            buttons.push({
                icon: 'bi-eye', title: '预览', handler: function () {
                    openPreview(file, previewKind);
                }
            });
        }
        buttons.push(
            {
                icon: 'bi-download', title: '下载', handler: function () {
                    var link = document.createElement('a');
                    link.href = fileUrl(file);
                    link.download = file.name;
                    document.body.appendChild(link);
                    link.click();
                    link.remove();
                }
            },
            {
                icon: 'bi-pencil', title: '重命名', handler: function () {
                    openRename(file);
                }
            },
            {
                icon: 'bi-pc-display', title: '在服务端打开', handler: function () {
                    openOnServer(file);
                }
            },
            {
                icon: 'bi-trash', title: '删除', danger: true, handler: function () {
                    deleteFile(file);
                }
            }
        );

        buttons.forEach(function (definition) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'icon-btn' + (definition.danger ? ' danger' : '');
            button.title = definition.title;
            var buttonIcon = document.createElement('i');
            buttonIcon.className = 'bi ' + definition.icon;
            button.appendChild(buttonIcon);
            button.addEventListener('click', definition.handler);
            actions.appendChild(button);
        });

        row.appendChild(iconBox);
        row.appendChild(main);
        row.appendChild(actions);
        return row;
    }

    async function openOnServer(file) {
        var ok = await askConfirm('在服务端打开', file.name, false);
        if (!ok) return;
        try {
            var result = await postJson('/api/file/open', { dirId: state.dirId, path: file.path });
            if (!result.ok) toast(result.error || '无法打开');
        } catch (err) {
            toast(err.message || '无法打开');
        }
    }

    async function deleteFile(file) {
        var ok = await askConfirm('删除', file.name, true);
        if (!ok) return;
        try {
            var result = await postJson('/api/file/delete', { dirId: state.dirId, path: file.path });
            if (!result.ok) {
                toast(result.error || '删除失败');
                return;
            }
            loadTree(true);
            loadList(true);
        } catch (err) {
            toast(err.message || '删除失败');
        }
    }

    function renderPager() {
        var host = $('pager');
        host.textContent = '';
        var pages = Math.max(1, Math.ceil(state.total / state.size));
        if (pages <= 1) {
            host.classList.add('d-none');
            return;
        }
        host.classList.remove('d-none');
        var prev = document.createElement('button');
        prev.type = 'button';
        prev.className = 'btn btn-outline-secondary btn-sm';
        prev.disabled = state.page <= 1;
        prev.title = '上一页';
        var prevIcon = document.createElement('i');
        prevIcon.className = 'bi bi-chevron-left';
        prev.appendChild(prevIcon);
        prev.addEventListener('click', function () {
            if (state.page > 1) {
                state.page--;
                loadList(true);
            }
        });

        var info = document.createElement('span');
        info.textContent = state.page + ' / ' + pages;

        var next = document.createElement('button');
        next.type = 'button';
        next.className = 'btn btn-outline-secondary btn-sm';
        next.disabled = state.page >= pages;
        next.title = '下一页';
        var nextIcon = document.createElement('i');
        nextIcon.className = 'bi bi-chevron-right';
        next.appendChild(nextIcon);
        next.addEventListener('click', function () {
            if (state.page < pages) {
                state.page++;
                loadList(true);
            }
        });

        host.appendChild(prev);
        host.appendChild(info);
        host.appendChild(next);
    }

    /* ---------------- uploads ---------------- */

    function findUpload(key) {
        for (var i = 0; i < state.uploads.length; i++) {
            if (state.uploads[i].key === key) return state.uploads[i];
        }
        return null;
    }

    function addFiles(files, dirId, folder) {
        var entries = Array.prototype.map.call(files, function (file) {
            return { file: file, folder: '' };
        });
        addFileEntries(entries, dirId, folder);
    }

    function joinFolder(base, sub) {
        if (!sub) return base || '';
        return base ? base + '\\' + sub : sub;
    }

    function addFileEntries(entries, dirId, folder) {
        if (!state.dirId) {
            toast('尚未添加目录');
            return;
        }
        var target = dirId || state.dirId;
        var targetFolder = folder == null ? state.folder : folder;
        if (!entries.length) return;
        state.panelCollapsed = false;

        var ongoing = state.uploads.some(function (item) {
            return item.status === 'queued' || item.status === 'hashing' ||
                item.status === 'ready' || item.status === 'init' ||
                item.status === 'conflict' || item.status === 'uploading' ||
                item.status === 'completing';
        });
        if (!ongoing) {
            state.decisions = {};
            batchSeq++;
        }

        entries.forEach(function (entry) {
            var file = entry.file;
            var item = {
                key: ++uploadSeq,
                batch: batchSeq,
                file: file,
                dirId: target,
                folder: joinFolder(targetFolder, entry.folder),
                name: file.name,
                size: file.size,
                hash: null,
                status: 'queued',
                error: null,
                uploadId: null,
                targetName: null,
                action: '',
                received: 0,
                doneBytes: 0,
                hashLoaded: 0,
                hashTotal: file.size || 1,
                hashQueued: false,
                xhr: null,
                canceled: false
            };
            state.uploads.push(item);
            if (!file.size) {
                item.status = 'failed';
                item.error = '文件为空';
                renderUpload(item);
                return;
            }
            renderUpload(item);
            startItem(item);
        });
        updateQueueVisibility();
        updateSummary();
        pump();
    }

    /* ---------------- dropped folders ---------------- */

    var invalidNamePattern = /[\\/:*?"<>|\x00-\x1f]/;

    function droppedRoots(dataTransfer) {
        var roots = [];
        if (!dataTransfer) return roots;
        var items = dataTransfer.items;
        if (items && items.length) {
            for (var i = 0; i < items.length; i++) {
                var item = items[i];
                if (item.kind !== 'file') continue;
                var entry = null;
                if (typeof item.webkitGetAsEntry === 'function') {
                    try { entry = item.webkitGetAsEntry(); } catch (err) { entry = null; }
                }
                if (entry) {
                    roots.push(entry);
                    continue;
                }
                var file = item.getAsFile ? item.getAsFile() : null;
                if (file) roots.push({ fallbackFile: file });
            }
            if (roots.length) return roots;
        }
        var files = dataTransfer.files;
        if (!files) return roots;
        for (var j = 0; j < files.length; j++) roots.push({ fallbackFile: files[j] });
        return roots;
    }

    function readFileEntry(entry) {
        return new Promise(function (resolve) {
            try {
                entry.file(function (file) { resolve(file); }, function () { resolve(null); });
            } catch (err) {
                resolve(null);
            }
        });
    }

    function readDirectoryEntries(reader) {
        return new Promise(function (resolve) {
            var all = [];
            function step() {
                reader.readEntries(function (batch) {
                    if (!batch || !batch.length) {
                        resolve(all);
                        return;
                    }
                    for (var i = 0; i < batch.length; i++) all.push(batch[i]);
                    step();
                }, function () { resolve(all); });
            }
            step();
        });
    }

    async function walkDropRoot(root, folder, out) {
        if (!root) return;
        if (root.fallbackFile) {
            out.push({ file: root.fallbackFile, folder: folder });
            return;
        }
        if (root.isFile) {
            var file = await readFileEntry(root);
            if (file) out.push({ file: file, folder: folder });
            return;
        }
        if (!root.isDirectory) return;
        var name = root.name || '';
        if (!name || name === '.' || name === '..' || invalidNamePattern.test(name)) return;
        var next = folder ? folder + '\\' + name : name;
        var children = await readDirectoryEntries(root.createReader());
        for (var i = 0; i < children.length; i++) {
            await walkDropRoot(children[i], next, out);
        }
    }

    async function ensureFolderPath(dirId, rel) {
        if (!rel) return true;
        var segments = rel.split('\\');
        var current = '';
        for (var i = 0; i < segments.length; i++) {
            var name = segments[i];
            if (!name) continue;
            var result;
            try {
                result = await postJson('/api/folder/create', {
                    dirId: dirId,
                    folder: current,
                    name: name
                });
            } catch (err) {
                return false;
            }
            if (!result.ok && !/已存在/.test(result.error || '')) return false;
            current = current ? current + '\\' + name : name;
        }
        return true;
    }

    async function addDroppedFiles(dataTransfer, dirId, folder) {
        if (!state.dirId) {
            toast('尚未添加目录');
            return;
        }
        var roots = droppedRoots(dataTransfer);
        if (!roots.length) return;

        var all = [];
        for (var i = 0; i < roots.length; i++) {
            await walkDropRoot(roots[i], '', all);
        }
        if (!all.length) {
            toast('没有可上传的文件');
            return;
        }

        var target = dirId || state.dirId;
        var base = folder == null ? state.folder : folder;
        var entries = [];
        var folders = [];
        var seenFolders = {};
        var skipped = false;
        for (var j = 0; j < all.length; j++) {
            var entry = all[j];
            var segments = entry.folder ? entry.folder.split('\\') : [];
            var bad = false;
            for (var k = 0; k < segments.length; k++) {
                if (invalidNamePattern.test(segments[k])) {
                    bad = true;
                    break;
                }
            }
            if (bad) {
                skipped = true;
                continue;
            }
            entries.push(entry);
            if (!entry.folder) continue;
            var full = joinFolder(base, entry.folder);
            if (seenFolders[full]) continue;
            seenFolders[full] = true;
            folders.push(full);
        }
        if (skipped) toast('部分文件夹名无效，已跳过');
        if (!entries.length) {
            toast('没有可上传的文件');
            return;
        }

        if (folders.length) {
            var created = true;
            for (var m = 0; m < folders.length; m++) {
                if (!(await ensureFolderPath(target, folders[m]))) {
                    created = false;
                    break;
                }
            }
            if (!created) {
                toast('文件夹创建失败');
                return;
            }
            for (var n = 0; n < folders.length; n++) expandPath(folders[n]);
            loadTree(true);
            loadList(false);
        }

        addFileEntries(entries, target, base);
    }

    async function startItem(item) {
        var result = null;
        try {
            result = await postJson('/api/upload/precheck', {
                dirId: item.dirId,
                folder: item.folder,
                name: item.name,
                size: item.size
            });
        } catch (err) {
            if (item.canceled) return;
            requestHash(item);
            return;
        }
        if (item.canceled) return;

        if (result && result.nameConflict) {
            var decision = state.decisions.conflict;
            if (!decision) {
                item.status = 'conflict';
                renderUpload(item);
                decision = await askConflict('conflict', item, {
                    size: result.nameSize || 0,
                    time: result.nameTime || ''
                });
                renderUpload(item);
            }
            if (item.canceled || item.status === 'canceled') return;
            if (decision === 'skip') {
                item.status = 'skipped';
                renderUpload(item);
                updateQueueVisibility();
                updateSummary();
                return;
            }
            item.action = decision;
        }

        if (result && result.hashRequired) {
            requestHash(item);
            return;
        }
        item.hash = null;
        item.status = 'ready';
        renderUpload(item);
        updateQueueVisibility();
        updateSummary();
        pump();
    }

    function requestHash(item) {
        item.hashQueued = true;
        worker.postMessage({ type: 'hash', id: item.key, file: item.file });
    }

    function statusText(item) {
        switch (item.status) {
            case 'queued': return '等待';
            case 'hashing': return '校验 ' + percentOf(item.hashLoaded || 0, item.hashTotal || 1) + '%';
            case 'ready': return '等待';
            case 'init': return '准备';
            case 'conflict': return '待确认';
            case 'uploading': return percentOf(item.received, item.size) + '%';
            case 'completing': return '正在保存';
            case 'done': return '完成';
            case 'skipped': return '跳过';
            case 'failed': return '失败';
            case 'canceled': return '已取消';
            default: return '';
        }
    }

    function percentOf(done, total) {
        if (!total) return 0;
        return Math.max(0, Math.min(100, Math.floor(done * 100 / total)));
    }

    function updateQueueVisibility() {
        var panel = $('uploadPanel');
        panel.classList.remove('d-none');
        var empty = !state.uploads.length;
        var collapsed = empty || !!state.panelCollapsed;
        var list = $('uploadList');
        var animate = lastCollapsed !== null && lastCollapsed !== collapsed;
        var startHeight = list.scrollHeight;

        lastCollapsed = collapsed;
        panel.classList.toggle('collapsed', collapsed);

        if (!animate) {
            list.style.height = collapsed ? '0px' : '';
        } else if (collapsed) {
            clearTimeout(list._foldTimer);
            list.style.height = startHeight + 'px';
            void list.offsetHeight;
            list.style.overflow = 'hidden';
            list.style.height = '0px';
        } else {
            list.style.height = '0px';
            void list.offsetHeight;
            list.style.overflow = 'hidden';
            list.style.height = list.scrollHeight + 'px';
            clearTimeout(list._foldTimer);
            list._foldTimer = setTimeout(function () {
                list.style.height = '';
                list.style.overflow = '';
            }, 260);
        }

        $('uploadTitle').textContent = empty ? '上传队列：空' : '上传队列';
        var fold = $('btnFoldUploads');
        var foldIcon = $('foldIcon');
        if (fold && foldIcon) {
            fold.disabled = empty;
            fold.title = collapsed ? '展开' : '收起';
            foldIcon.className = 'bi ' + (collapsed ? 'bi-chevron-up' : 'bi-chevron-down');
        }
    }

    function updateSummary() {
        var total = state.uploads.length;
        if (!total) {
            $('uploadSummary').textContent = '';
            return;
        }
        var done = 0;
        var failed = 0;
        var skipped = 0;
        state.uploads.forEach(function (item) {
            if (item.status === 'done') done++;
            else if (item.status === 'failed') failed++;
            else if (item.status === 'skipped') skipped++;
        });
        var text = done + ' / ' + total;
        if (skipped) text += ' · 跳过 ' + skipped;
        if (failed) text += ' · 失败 ' + failed;
        $('uploadSummary').textContent = text;
    }

    function uploadTotals() {
        var done = 0;
        var total = 0;
        var uploading = false;
        state.uploads.forEach(function (item) {
            if (item.status === 'failed' || item.status === 'canceled' ||
                item.status === 'skipped') return;
            var size = item.size || 0;
            total += size;
            if (item.status === 'done' || item.status === 'completing') done += size;
            else if (item.status === 'uploading') done += Math.min(item.received || 0, size);
            if (item.status === 'uploading') uploading = true;
        });
        return { done: done, total: total, uploading: uploading };
    }

    function formatEta(seconds) {
        if (!isFinite(seconds) || seconds < 0) return '';
        var total = Math.max(1, Math.ceil(seconds));
        if (total < 60) return '剩余 ' + total + ' 秒';
        var minutes = Math.floor(total / 60);
        if (minutes < 60) return '剩余 ' + minutes + ' 分 ' + (total % 60) + ' 秒';
        return '剩余 ' + Math.floor(minutes / 60) + ' 时 ' + (minutes % 60) + ' 分';
    }

    function updateUploadStats() {
        var stats = uploadTotals();
        var now = Date.now();

        if (!stats.uploading) {
            speedSamples = [];
            speedValue = 0;
        } else {
            speedSamples.push({ time: now, done: stats.done });
            while (speedSamples.length > 2 && now - speedSamples[0].time > 3000)
                speedSamples.shift();
            var first = speedSamples[0];
            var elapsed = now - first.time;
            speedValue = elapsed >= 500
                ? Math.max(0, stats.done - first.done) * 1000 / elapsed
                : 0;
        }

        var text = '';
        if (stats.uploading && stats.total > 0) {
            var percent = Math.floor(stats.done * 100 / stats.total);
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            var parts = [formatSize(Math.round(speedValue)) + '/s'];
            if (speedValue > 0 && stats.total > stats.done) {
                parts.push(formatEta((stats.total - stats.done) / speedValue));
            }
            parts.push(percent + '%');
            text = parts.join(' · ');
        }
        $('uploadStats').textContent = text;
    }

    function collectProgress() {
        var map = {};
        var order = [];
        state.uploads.forEach(function (item) {
            if (item.batch !== batchSeq) return;
            if (item.status === 'failed' || item.status === 'canceled' ||
                item.status === 'skipped') return;
            var entry = map[item.dirId];
            if (!entry) {
                entry = { dirId: item.dirId, active: 0, doneBytes: 0, totalBytes: 0, saving: 0 };
                map[item.dirId] = entry;
                order.push(entry);
            }
            var size = item.size || 0;
            entry.totalBytes += size;
            if (item.status === 'done' || item.status === 'completing') {
                entry.doneBytes += size;
            } else if (item.status === 'uploading') {
                entry.doneBytes += Math.min(item.received || 0, size);
            }
            if (item.status !== 'done') entry.active++;
            if (item.status === 'completing') entry.saving++;
        });
        var dirs = [];
        order.forEach(function (entry) {
            if (entry.active > 0) dirs.push(entry);
        });
        return dirs;
    }

    function reportProgress() {
        var dirs = collectProgress();
        var signature = JSON.stringify(dirs);
        if (signature === progressSignature) return;
        progressSignature = signature;
        postJson('/api/upload/progress', { dirs: dirs }).catch(function () {
            progressSignature = '';
        });
    }

    function createUploadNode(item) {
        var node = document.createElement('div');
        node.className = 'upload-item';
        node.dataset.key = item.key;

        var top = document.createElement('div');
        top.className = 'upload-top';

        var name = document.createElement('span');
        name.className = 'upload-name';

        var stateEl = document.createElement('span');
        stateEl.className = 'upload-state';

        var action = document.createElement('button');
        action.type = 'button';
        action.className = 'upload-mini';
        var actionIcon = document.createElement('i');
        action.appendChild(actionIcon);

        top.appendChild(name);
        top.appendChild(stateEl);
        top.appendChild(action);

        var progress = document.createElement('div');
        progress.className = 'progress';
        var bar = document.createElement('div');
        bar.className = 'progress-bar';
        progress.appendChild(bar);

        node.appendChild(top);
        node.appendChild(progress);

        action.addEventListener('click', function () {
            if (item.status === 'failed') {
                retryUpload(item);
            } else if (item.status === 'done' || item.status === 'skipped' ||
                item.status === 'canceled') {
                removeUpload(item);
            } else {
                cancelUpload(item);
            }
        });

        node._parts = { name: name, state: stateEl, action: action, icon: actionIcon,
            bar: bar, progress: progress };
        return node;
    }

    function renderUpload(item) {
        var node = uploadNodes[item.key];
        if (!node) {
            var host = $('uploadList');
            node = createUploadNode(item);
            uploadNodes[item.key] = node;
            host.appendChild(node);
        }
        var parts = node._parts;
        parts.name.textContent = item.name;
        parts.name.title = item.name + (item.error ? '\n' + item.error : '');
        parts.state.textContent = statusText(item);
        parts.state.title = item.error || '';

        node.className = 'upload-item' +
            (item.status === 'done' ? ' done' : '') +
            (item.status === 'failed' ? ' failed' : '');

        var percent;
        if (item.status === 'hashing' || item.status === 'queued') {
            percent = item.hash ? 100 : percentOf(item.hashLoaded || 0, item.hashTotal || 1);
        } else if (item.status === 'done' || item.status === 'skipped') {
            percent = 100;
        } else {
            percent = percentOf(item.received, item.size);
        }
        parts.bar.style.width = percent + '%';
        parts.bar.className = 'progress-bar' +
            (item.status === 'done' ? ' bg-success' : '') +
            (item.status === 'failed' ? ' bg-danger' : '') +
            ((item.status === 'uploading' || item.status === 'completing')
                ? ' progress-bar-striped progress-bar-animated' : '');

        var iconName;
        var title;
        if (item.status === 'failed') {
            iconName = 'bi-arrow-clockwise';
            title = '重试';
        } else if (item.status === 'done' || item.status === 'skipped' || item.status === 'canceled') {
            iconName = 'bi-x-lg';
            title = '移除';
        } else {
            iconName = 'bi-x-lg';
            title = '取消';
        }
        parts.action.disabled = item.status === 'completing';
        if (item.status === 'completing') title = '正在保存';
        parts.action.title = title;
        parts.icon.className = 'bi ' + iconName;
    }

    function rebuildUploads() {
        var host = $('uploadList');
        host.textContent = '';
        uploadNodes = {};
        state.uploads.forEach(function (item) {
            var node = createUploadNode(item);
            uploadNodes[item.key] = node;
            host.appendChild(node);
            var parts = node._parts;
            parts.name.textContent = item.name;
            parts.state.textContent = statusText(item);
        });
        state.uploads.forEach(renderUpload);
        updateQueueVisibility();
        updateSummary();
    }

    function removeUpload(item) {
        if (item.uploadId) {
            postJson('/api/upload/cancel', { id: item.uploadId }).catch(function () { });
            item.uploadId = null;
        }
        var index = state.uploads.indexOf(item);
        if (index >= 0) state.uploads.splice(index, 1);
        var node = uploadNodes[item.key];
        if (node && node.parentNode) node.parentNode.removeChild(node);
        delete uploadNodes[item.key];
        updateQueueVisibility();
        updateSummary();
    }

    function cancelUpload(item) {
        item.canceled = true;
        item.action = '';
        if (item.xhr) {
            try { item.xhr.abort(); } catch (e) { }
        }
        if (item.hashQueued) {
            item.hashQueued = false;
            worker.postMessage({ type: 'cancel', id: item.key });
        }
        var hadSession = !!item.uploadId;
        if (hadSession) {
            postJson('/api/upload/cancel', { id: item.uploadId }).catch(function () { });
            item.uploadId = null;
        }
        if (state.conflictItem === item) closeConflict('skip', false);
        item.status = 'canceled';
        item.error = null;
        renderUpload(item);
        updateQueueVisibility();
        updateSummary();
    }

    function retryUpload(item) {
        state.panelCollapsed = false;
        item.canceled = false;
        item.error = null;
        item.action = '';
        item.hash = null;
        item.hashLoaded = 0;
        item.hashTotal = item.size || 1;
        item.hashQueued = false;
        item.batch = batchSeq;
        item.uploadId = null;
        item.targetName = null;
        item.received = 0;
        item.doneBytes = 0;
        item.status = 'queued';
        renderUpload(item);
        startItem(item);
        updateQueueVisibility();
        updateSummary();
        pump();
    }

    function pump() {
        while (state.running < state.concurrent) {
            var next = null;
            for (var i = 0; i < state.uploads.length; i++) {
                var candidate = state.uploads[i];
                if (candidate.status === 'ready' && !candidate.canceled) {
                    next = candidate;
                    break;
                }
            }
            if (!next) return;
            state.running++;
            runUpload(next);
        }
    }

    async function runUpload(item) {
        try {
            await uploadOne(item);
        } catch (err) {
            if (item.canceled || item.status === 'canceled') {
                item.status = 'canceled';
            } else {
                item.status = 'failed';
                item.error = err && err.message ? err.message : String(err);
            }
        } finally {
            if (item.xhr) {
                item.xhr = null;
            }
            if (item.uploadId && (item.status === 'failed' || item.status === 'canceled')) {
                var staleId = item.uploadId;
                item.uploadId = null;
                postJson('/api/upload/cancel', { id: staleId }).catch(function () { });
            }
            renderUpload(item);
            updateQueueVisibility();
            updateSummary();
            state.running = Math.max(0, state.running - 1);
            setTimeout(pump, 0);
        }
    }

    async function uploadOne(item) {
        item.status = 'init';
        renderUpload(item);

        var action = item.action || '';
        item.action = '';
        var result = null;
        for (var guard = 0; guard < 6; guard++) {
            if (item.canceled) throw new Error('已取消');
            result = await postJson('/api/upload/init', {
                dirId: item.dirId,
                folder: item.folder,
                name: item.name,
                size: item.size,
                hash: item.hash,
                action: action
            });
            if (result.error) throw new Error(result.error);
            if (result.status === 'ok') break;
            if (result.status === 'skipped') {
                item.status = 'skipped';
                return;
            }
            if (result.status === 'duplicate' || result.status === 'nameConflict') {
                var kind = result.status === 'duplicate' ? 'duplicate' : 'conflict';
                var existing = kind === 'duplicate'
                    ? { path: result.existingPath || '' }
                    : { size: result.existingSize || 0, time: result.existingTime || '' };
                var decision = state.decisions[kind];
                if (!decision) {
                    item.status = 'conflict';
                    renderUpload(item);
                    decision = await askConflict(kind, item, existing);
                    renderUpload(item);
                }
                if (item.canceled) throw new Error('已取消');
                if (decision === 'skip') {
                    item.status = 'skipped';
                    return;
                }
                action = decision;
                item.status = 'init';
                renderUpload(item);
                continue;
            }
            throw new Error(result.error || '上传失败');
        }
        if (!result || result.status !== 'ok') throw new Error('上传失败');
        if (item.canceled) throw new Error('已取消');

        item.uploadId = result.uploadId;
        item.targetName = result.targetName;
        item.received = 0;
        item.doneBytes = 0;
        renderUpload(item);

        var chunkSize = result.chunkSize || 4 * 1024 * 1024;
        var totalChunks = Math.max(1, Math.ceil(item.size / chunkSize));
        var uploaded = {};
        (result.uploaded || []).forEach(function (index) { uploaded[index] = true; });

        for (var i = 0; i < totalChunks; i++) {
            var length = i === totalChunks - 1 ? item.size - i * chunkSize : chunkSize;
            if (uploaded[i]) item.doneBytes += length;
        }
        item.received = item.doneBytes;

        item.status = 'uploading';
        renderUpload(item);

        for (var chunk = 0; chunk < totalChunks; chunk++) {
            if (uploaded[chunk]) continue;
            if (item.canceled) throw new Error('已取消');
            await sendChunkWithRetry(item, chunk, chunkSize);
            var size = chunk === totalChunks - 1 ? item.size - chunk * chunkSize : chunkSize;
            item.doneBytes += size;
            item.received = item.doneBytes;
            renderUpload(item);
        }

        if (item.canceled) throw new Error('已取消');

        item.received = item.size;
        item.doneBytes = item.size;
        item.status = 'completing';
        renderUpload(item);
        reportProgress();

        var completed = await postJson('/api/upload/complete', { id: item.uploadId });
        if (!completed.ok) throw new Error(completed.error || '上传失败');
        item.status = 'done';
        item.uploadId = null;
        item.targetName = completed.savedName || item.targetName;
        renderUpload(item);
        reportProgress();

        if (item.dirId === state.dirId && item.folder === state.folder) {
            scheduleListRefresh();
        } else if (item.dirId === state.dirId && state.rosterEnabled) {
            var prefix = state.folder ? state.folder + '\\' : '';
            var inRoster = state.rosterNames.some(function (name) {
                return item.folder === prefix + name;
            });
            if (inRoster) scheduleListRefresh();
        }
    }

    async function sendChunkWithRetry(item, index, chunkSize) {
        var attempt = 0;
        while (true) {
            try {
                await sendChunk(item, index, chunkSize);
                return;
            } catch (err) {
                if (item.canceled) throw err;
                if (err && err.status === 401) {
                    location.href = '/';
                    throw err;
                }
                attempt++;
                if (attempt >= 5) throw err;
                await delay(attempt === 1 ? 400 : 1200 * attempt);
            }
        }
    }

    function sendChunk(item, index, chunkSize) {
        return new Promise(function (resolve, reject) {
            if (item.canceled) {
                reject(new Error('已取消'));
                return;
            }
            var start = index * chunkSize;
            var end = Math.min(start + chunkSize, item.size);
            var blob = item.file.slice(start, end);
            var xhr = new XMLHttpRequest();
            item.xhr = xhr;
            xhr.open('POST', '/api/upload/chunk?id=' + encodeURIComponent(item.uploadId) +
                '&index=' + index, true);
            xhr.setRequestHeader('Content-Type', 'application/octet-stream');
            xhr.upload.onprogress = function (event) {
                if (!event.lengthComputable) return;
                item.received = item.doneBytes + Math.min(event.loaded, blob.size);
                renderUpload(item);
            };
            xhr.onload = function () {
                item.xhr = null;
                if (xhr.status >= 200 && xhr.status < 300) {
                    resolve();
                    return;
                }
                var message = '上传失败';
                try {
                    var data = JSON.parse(xhr.responseText);
                    if (data && data.error) message = data.error;
                } catch (e) { }
                if (xhr.status === 429) message = '排队中';
                var error = new Error(message);
                error.status = xhr.status;
                reject(error);
            };
            xhr.onerror = function () {
                item.xhr = null;
                var error = new Error('网络中断');
                error.status = 0;
                reject(error);
            };
            xhr.onabort = function () {
                item.xhr = null;
                var error = new Error('已取消');
                error.status = -1;
                reject(error);
            };
            xhr.send(blob);
        });
    }

    function scheduleListRefresh() {
        if (refreshSoon) return;
        refreshSoon = setTimeout(function () {
            refreshSoon = null;
            loadList(false);
        }, 800);
    }

    /* ---------------- events ---------------- */

    var dragDepth = 0;

    function bindEvents() {
        trackModals();

        $('btnPick').addEventListener('click', function () {
            $('fileInput').click();
        });
        $('fileInput').addEventListener('change', async function () {
            var list = this.files ? Array.prototype.slice.call(this.files) : [];
            this.value = '';
            if (!list.length) return;
            var dirId = state.dirId;
            var folder = state.folder;
            if (state.rosterEnabled && state.rosterNames.length) {
                var name = await choosePerson();
                if (!name) return;
                addFiles(list, dirId, folder ? folder + '\\' + name : name);
                return;
            }
            addFiles(list);
        });

        $('btnNewFolder').addEventListener('click', openNewFolder);

        $('btnRefresh').addEventListener('click', function () {
            doRefresh(true);
        });

        $('rosterToggle').addEventListener('change', onRosterToggle);
        $('btnRosterEdit').addEventListener('click', editRoster);
        $('btnRosterShot').addEventListener('click', showRosterShot);
        $('btnShotCopy').addEventListener('click', function () {
            if (!shotText) {
                toast('暂无名单');
                return;
            }
            toast(copyPlainText(shotText) ? '已复制' : '复制失败');
        });

        $('sortSelect').addEventListener('change', function () {
            state.sort = this.value;
            state.desc = this.value !== 'name';
            state.page = 1;
            state.lastSignature = null;
            loadList(true);
        });

        $('btnQr').addEventListener('click', function () {
            $('qrImage').src = '/api/qrcode?t=' + Date.now();
            modal('qrModal').show();
        });

        $('btnDiskSpace').addEventListener('click', function () { showDiskSpace(); });

        $('btnFoldUploads').addEventListener('click', function () {
            if (!state.uploads.length) return;
            state.panelCollapsed = !state.panelCollapsed;
            updateQueueVisibility();
        });

        $('btnClearDone').addEventListener('click', function () {
            var keep = state.uploads.filter(function (item) {
                return item.status !== 'done' && item.status !== 'skipped' && item.status !== 'canceled';
            });
            state.uploads = keep;
            rebuildUploads();
        });

        $('btnClearAll').addEventListener('click', function () {
            var keep = state.uploads.filter(function (item) {
                return item.status === 'queued' || item.status === 'hashing' ||
                    item.status === 'ready' || item.status === 'init' ||
                    item.status === 'conflict' || item.status === 'uploading' ||
                    item.status === 'completing';
            });
            state.uploads = keep;
            rebuildUploads();
        });

        $('btnClearDownloads').addEventListener('click', function () {
            var tokens = Object.keys(packTasks);
            for (var i = 0; i < tokens.length; i++) removePackTask(tokens[i]);
        });

        window.addEventListener('dragenter', function (event) {
            if (!event.dataTransfer || !hasFiles(event.dataTransfer)) return;
            event.preventDefault();
            if (state.rosterEnabled) return;
            dragDepth++;
            $('dropHint').classList.add('show');
        });
        window.addEventListener('dragover', function (event) {
            if (!event.dataTransfer || !hasFiles(event.dataTransfer)) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
        });
        window.addEventListener('dragleave', function (event) {
            if (state.rosterEnabled) {
                rosterDragging = false;
                return;
            }
            dragDepth--;
            if (dragDepth <= 0) {
                dragDepth = 0;
                $('dropHint').classList.remove('show');
            }
        });
        window.addEventListener('drop', function (event) {
            if (!event.dataTransfer) return;
            event.preventDefault();
            dragDepth = 0;
            $('dropHint').classList.remove('show');
            if (state.rosterEnabled) {
                rosterDragging = false;
                loadList(false);
                return;
            }
            addDroppedFiles(event.dataTransfer, null, undefined);
        });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') {
                $('dropHint').classList.remove('show');
                dragDepth = 0;
                hideTreeMenu();
            }
        });

        document.addEventListener('mousedown', function (event) {
            if (treeMenu && !treeMenu.contains(event.target)) hideTreeMenu();
        });
        document.addEventListener('scroll', function () { hideTreeMenu(); }, true);
        window.addEventListener('resize', function () { hideTreeMenu(); });

        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                refreshLeft = 10;
                loadList(false);
            }
        });

        setInterval(onSecond, 1000);
        setInterval(reportProgress, 1000);
        setInterval(updateUploadStats, 1000);
        heartbeat();
        setInterval(heartbeat, 5000);
    }

    function hasFiles(dataTransfer) {
        if (!dataTransfer) return false;
        var types = dataTransfer.types;
        if (!types) return true;
        for (var i = 0; i < types.length; i++) {
            if (types[i] === 'Files') return true;
        }
        return false;
    }

    async function doRefresh(manual) {
        if (refreshing) return;
        refreshing = true;
        refreshLeft = 10;
        var icon = $('refreshIcon');
        if (icon) icon.classList.add('spin');
        var started = Date.now();
        try {
            if (manual) {
                await refreshStatus();
                await loadTree(true);
                await loadList(true);
            } else {
                tickCount++;
                if (tickCount % 3 === 0) await refreshStatus();
                if (!state.tree || tickCount % 6 === 0) await loadTree(false);
                await loadList(false);
            }
        } catch (e) {
            /* 忽略刷新错误 */
        } finally {
            var remain = 500 - (Date.now() - started);
            if (remain > 0) {
                await new Promise(function (resolve) { setTimeout(resolve, remain); });
            }
            refreshing = false;
            if (icon) icon.classList.remove('spin');
            refreshLeft = 10;
        }
    }

    function onSecond() {
        if (document.hidden || state.openModals > 0 || refreshing) return;
        refreshLeft--;
        if (refreshLeft <= 0) doRefresh(false);
    }

    /* ---------------- init ---------------- */

    async function init() {
        worker = new Worker('/sha256-worker.js');
        worker.onmessage = function (event) {
            var message = event.data;
            if (!message) return;
            var item = findUpload(message.id);
            if (!item) return;
            if (message.type !== 'progress') item.hashQueued = false;
            if (message.type === 'progress') {
                item.hashLoaded = message.loaded;
                item.hashTotal = message.total || item.hashTotal;
                if (item.status === 'queued') item.status = 'hashing';
                renderUpload(item);
            } else if (message.type === 'done') {
                if (item.canceled) {
                    item.status = 'canceled';
                } else {
                    item.hash = message.hash;
                    item.status = 'ready';
                }
                renderUpload(item);
                pump();
            } else if (message.type === 'error') {
                item.status = 'failed';
                item.error = message.message || '校验失败';
                renderUpload(item);
            } else if (message.type === 'canceled') {
                item.status = 'canceled';
                renderUpload(item);
            }
            updateQueueVisibility();
            updateSummary();
        };
        worker.onerror = function () {
            toast('校验组件加载失败');
        };

        bindEvents();
        updateQueueVisibility();

        try {
            await refreshStatus();
            if (state.dirId) {
                await loadTree(true);
                await loadList(true);
            } else {
                renderNoDirectories();
            }
        } catch (err) {
            if (err && err.message !== '登录已失效') toast(err.message || '加载失败');
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
