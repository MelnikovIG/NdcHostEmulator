// Convert Unicode Control Pictures back to original control bytes on copy
document.addEventListener('copy', function (e) {
    const selection = window.getSelection().toString();
    if (!selection) return;
    let raw = '';
    for (let i = 0; i < selection.length; i++) {
        const code = selection.charCodeAt(i);
        if (code >= 0x2400 && code <= 0x241F)
            raw += String.fromCharCode(code - 0x2400);
        else if (code === 0x2421)
            raw += String.fromCharCode(0x7F);
        else
            raw += selection[i];
    }
    e.clipboardData.setData('text/plain', raw);
    e.preventDefault();
});

window.scrollToBottom = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'end' });
};

window.initResizable = function (handleId, scrollId, minHeight, maxHeight) {
    const handle = document.getElementById(handleId);
    const scroll = document.getElementById(scrollId);
    if (!handle || !scroll) return;

    let startY = 0;
    let startHeight = 0;

    handle.addEventListener('mousedown', function (e) {
        startY = e.clientY;
        startHeight = scroll.offsetHeight;
        document.body.style.userSelect = 'none';
        document.body.style.cursor = 'ns-resize';

        function onMove(e) {
            const delta = startY - e.clientY;
            const newHeight = Math.min(maxHeight, Math.max(minHeight, startHeight + delta));
            scroll.style.height = newHeight + 'px';
        }

        function onUp() {
            document.body.style.userSelect = '';
            document.body.style.cursor = '';
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        }

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
        e.preventDefault();
    });
};

window.contentEditor = {
    _initialized: new WeakSet(),

    _formatHtml: function (text) {
        let html = '';
        for (let i = 0; i < text.length; i++) {
            const code = text.charCodeAt(i);
            if (code < 0x20 && code !== 0x0A && code !== 0x0D && code !== 0x09) {
                html += '<span class="ctrl-pic">' + String.fromCharCode(0x2400 + code) + '</span>';
            } else if (code === 0x7F) {
                html += '<span class="ctrl-pic">\u2421</span>';
            } else if (text[i] === '<') {
                html += '&lt;';
            } else if (text[i] === '>') {
                html += '&gt;';
            } else if (text[i] === '&') {
                html += '&amp;';
            } else {
                html += text[i];
            }
        }
        return html;
    },

    _escapeCtrlPicSpan: function (el) {
        const selection = window.getSelection();
        if (!selection || !selection.rangeCount) return;
        const range = selection.getRangeAt(0);
        let node = range.startContainer;
        while (node && node !== el) {
            if (node.nodeType === Node.ELEMENT_NODE && node.classList && node.classList.contains('ctrl-pic')) {
                const newRange = document.createRange();
                newRange.setStartAfter(node);
                newRange.collapse(true);
                selection.removeAllRanges();
                selection.addRange(newRange);
                return;
            }
            node = node.parentNode;
        }
    },

    init: function (el) {
        if (!el || this._initialized.has(el)) return;
        this._initialized.add(el);
        const self = this;
        el.addEventListener('paste', function (e) {
            e.preventDefault();
            const text = (e.clipboardData || window.clipboardData).getData('text/plain');
            const html = self._formatHtml(text);
            document.execCommand('insertHTML', false, html);
            self._escapeCtrlPicSpan(el);
        });
        el.addEventListener('keydown', function (e) {
            if (e.ctrlKey || e.metaKey || e.altKey || e.key.length !== 1) return;
            self._escapeCtrlPicSpan(el);
        });
    },

    setHtml: function (el, html) {
        if (el) {
            this.init(el);
            el.innerHTML = html;
        }
    },

    getText: function (el) {
        return el ? el.innerText : '';
    }
};
