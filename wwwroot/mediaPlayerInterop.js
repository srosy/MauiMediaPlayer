window.mauiMediaPlayer = {
    initializeImage(image, dotNetReference, displayName) {
        if (!image) {
            return;
        }

        const log = (level, message) => {
            dotNetReference.invokeMethodAsync('OnMediaDebugEvent', level, message).catch(() => { });
        };

        image.onload = () => {
            log('INFO', `Image loaded: ${displayName}; naturalSize=${image.naturalWidth}x${image.naturalHeight}`);
        };

        image.onerror = () => {
            log('ERROR', `Image error: ${displayName}; currentSrc=${image.currentSrc || '(empty)'}`);
        };

        if (image.complete && image.naturalWidth > 0) {
            log('DEBUG', `Image already complete: ${displayName}; naturalSize=${image.naturalWidth}x${image.naturalHeight}`);
        }
    },

    // Decodes the data URI into the WebView's internal image cache off the main thread
    // BEFORE we attach a real <img> for it. Returns whether the decode succeeded so the
    // .NET side can fall back gracefully on failures. Bounded so it can never block the
    // pipeline if the WebView misbehaves on a malformed image.
    prewarmImage(dataUri, timeoutMs) {
        return new Promise((resolve) => {
            if (!dataUri) {
                resolve(false);
                return;
            }

            const limit = Math.max(250, Number(timeoutMs) || 3000);
            const img = new Image();
            img.decoding = 'async';

            let settled = false;
            const finish = (success) => {
                if (settled) {
                    return;
                }
                settled = true;
                clearTimeout(timer);
                resolve(success);
            };

            const timer = setTimeout(() => finish(false), limit);

            img.onload = () => {
                if (typeof img.decode === 'function') {
                    img.decode().then(() => finish(true)).catch(() => finish(false));
                } else {
                    finish(true);
                }
            };
            img.onerror = () => finish(false);

            try {
                img.src = dataUri;
            } catch (_) {
                finish(false);
            }
        });
    },

    getMediaViewportBounds() {
        const viewport = document.querySelector('.media-viewport');

        if (!viewport) {
            return { x: 0, y: 0, width: 0, height: 0 };
        }

        const rect = viewport.getBoundingClientRect();
        return {
            x: rect.left,
            y: rect.top,
            width: rect.width,
            height: rect.height
        };
    },

    getMediaPaneBounds(slotIndex) {
        const pane = document.querySelector(`[data-media-pane][data-slot="${slotIndex}"]`);

        if (!pane) {
            return { x: 0, y: 0, width: 0, height: 0 };
        }

        const rect = pane.getBoundingClientRect();
        const caption = pane.querySelector('.media-caption');
        const captionHeight = caption?.getBoundingClientRect().height ?? 0;

        // Publish the caption inset to CSS as a custom property on the pane. The
        // native MediaElement (on Windows the SwapChainPanel rendered by MAUI's
        // MediaElement handler) is drawn ABOVE the WebView at the rect we return
        // below; everything past `rect.height - captionHeight` is HTML space
        // reserved for the caption strip. Without this exposure, an HTML
        // <img class="media-image-holdover"> rendered full-pane bleeds through
        // the caption's transparent gradient (visible as a thin strip of the
        // previous item below the caption text). The CSS clip-path on
        // .is-video-pane .media-image-holdover uses this value to bound the
        // holdover to the exact same region as the native MediaElement.
        pane.style.setProperty('--caption-inset', `${captionHeight}px`);

        return {
            x: rect.left,
            y: rect.top,
            width: rect.width,
            height: Math.max(0, rect.height - captionHeight)
        };
    },

    // Sub-pixel jitter from ResizeObserver during neighbour image swaps was nudging
    // native MediaElements and causing visible "jumping". Coalesce + epsilon-filter.
    _boundsEpsilon: 0.75,

    _boundsNearlyEqual(a, b) {
        if (!a || !b) {
            return false;
        }

        const eps = window.mauiMediaPlayer._boundsEpsilon;
        return Math.abs(a.x - b.x) < eps
            && Math.abs(a.y - b.y) < eps
            && Math.abs(a.width - b.width) < eps
            && Math.abs(a.height - b.height) < eps;
    },

    requestMediaPaneBoundsFlush() {
        if (typeof window._mauiMediaPlayerBoundsHandler === 'function') {
            window._mauiMediaPlayerBoundsHandler();
        }
    },

    invalidateMediaPaneBoundsCache() {
        if (Array.isArray(window._mauiMediaPlayerBoundsLastReported)) {
            window._mauiMediaPlayerBoundsLastReported = [null, null, null];
        }
    },

    watchImages(dotNetReference) {
        if (!dotNetReference) {
            return;
        }

        document.querySelectorAll('.media-pane:not(.is-video-pane) img.media-image-ready, .media-pane:not(.is-video-pane) img.media-image-holdover').forEach((image) => {
            if (image._mauiImageWatched) {
                return;
            }

            image._mauiImageWatched = true;
            window.mauiMediaPlayer.initializeImage(image, dotNetReference, image.alt || 'image');
        });
    },

    ensureMediaPaneObservers() {
        const observer = window._mauiMediaPlayerBoundsObserver;
        if (!observer) {
            return;
        }

        document.querySelectorAll('[data-media-pane]').forEach((pane) => {
            if (!pane._mauiBoundsObserved) {
                pane._mauiBoundsObserved = true;
                observer.observe(pane);
            }
        });
    },

    observeMediaViewportBounds(dotNetReference) {
        if (window._mauiMediaPlayerBoundsNotifyTimer) {
            clearTimeout(window._mauiMediaPlayerBoundsNotifyTimer);
            window._mauiMediaPlayerBoundsNotifyTimer = null;
        }

        if (window._mauiMediaPlayerBoundsHandler) {
            window.removeEventListener('resize', window._mauiMediaPlayerBoundsHandler);
            window.visualViewport?.removeEventListener('resize', window._mauiMediaPlayerBoundsHandler);
            window.visualViewport?.removeEventListener('scroll', window._mauiMediaPlayerBoundsHandler);
        }

        if (window._mauiMediaPlayerBoundsObserver) {
            window._mauiMediaPlayerBoundsObserver.disconnect();
        }

        window._mauiMediaPlayerBoundsDotNet = dotNetReference;
        window._mauiMediaPlayerBoundsLastReported = [null, null, null];

        const flushBounds = () => {
            window._mauiMediaPlayerBoundsNotifyTimer = null;
            window.mauiMediaPlayer.ensureMediaPaneObservers();

            const ref = window._mauiMediaPlayerBoundsDotNet;
            if (!ref) {
                return;
            }

            for (let slotIndex = 0; slotIndex < 3; slotIndex++) {
                const bounds = window.mauiMediaPlayer.getMediaPaneBounds(slotIndex);
                const last = window._mauiMediaPlayerBoundsLastReported[slotIndex];
                if (window.mauiMediaPlayer._boundsNearlyEqual(bounds, last)) {
                    continue;
                }

                window._mauiMediaPlayerBoundsLastReported[slotIndex] = bounds;
                ref.invokeMethodAsync(
                    'OnMediaPaneBoundsChanged',
                    slotIndex,
                    bounds.x,
                    bounds.y,
                    bounds.width,
                    bounds.height).catch(() => { });
            }
        };

        const scheduleBoundsNotify = () => {
            if (window._mauiMediaPlayerBoundsNotifyTimer) {
                return;
            }

            window._mauiMediaPlayerBoundsNotifyTimer = setTimeout(flushBounds, 48);
        };

        window._mauiMediaPlayerBoundsHandler = scheduleBoundsNotify;
        window.addEventListener('resize', scheduleBoundsNotify);
        window.visualViewport?.addEventListener('resize', scheduleBoundsNotify);
        window.visualViewport?.addEventListener('scroll', scheduleBoundsNotify);

        const viewport = document.querySelector('.media-viewport');
        if (viewport && window.ResizeObserver) {
            window._mauiMediaPlayerBoundsObserver = new ResizeObserver(scheduleBoundsNotify);
            window._mauiMediaPlayerBoundsObserver.observe(viewport);
            document.querySelectorAll('[data-media-pane]').forEach((pane) => {
                pane._mauiBoundsObserved = true;
                window._mauiMediaPlayerBoundsObserver.observe(pane);
            });
        }

        setTimeout(flushBounds, 0);
    },

    disposeMediaViewportBoundsObserver() {
        if (window._mauiMediaPlayerBoundsNotifyTimer) {
            clearTimeout(window._mauiMediaPlayerBoundsNotifyTimer);
            window._mauiMediaPlayerBoundsNotifyTimer = null;
        }

        window._mauiMediaPlayerBoundsDotNet = null;
        window._mauiMediaPlayerBoundsLastReported = [null, null, null];

        if (window._mauiMediaPlayerBoundsHandler) {
            window.removeEventListener('resize', window._mauiMediaPlayerBoundsHandler);
            window.visualViewport?.removeEventListener('resize', window._mauiMediaPlayerBoundsHandler);
            window.visualViewport?.removeEventListener('scroll', window._mauiMediaPlayerBoundsHandler);
            window._mauiMediaPlayerBoundsHandler = null;
        }

        if (window._mauiMediaPlayerBoundsObserver) {
            window._mauiMediaPlayerBoundsObserver.disconnect();
            window._mauiMediaPlayerBoundsObserver = null;
        }

        document.querySelectorAll('[data-media-pane]').forEach((pane) => {
            delete pane._mauiBoundsObserved;
        });
    },

    // Toggles an `is-mouse-active` class on the player root so CSS can fade caption
    // strips in/out during fullscreen. The class is set on input activity and cleared
    // after `idleMs` of no movement. Idempotent: a second install replaces the first.
    installFullscreenIdleTracker(idleMs) {
        window.mauiMediaPlayer.uninstallFullscreenIdleTracker();

        const limit = Math.max(500, Number(idleMs) || 5000);
        const root = document.querySelector('[data-player-root]');

        if (!root) {
            return;
        }

        const setActive = () => {
            root.classList.add('is-mouse-active');

            if (window._mauiMediaPlayerIdleTimer) {
                clearTimeout(window._mauiMediaPlayerIdleTimer);
            }

            window._mauiMediaPlayerIdleTimer = setTimeout(() => {
                root.classList.remove('is-mouse-active');
                window._mauiMediaPlayerIdleTimer = null;
            }, limit);
        };

        window._mauiMediaPlayerIdleHandler = setActive;
        document.addEventListener('mousemove', setActive, { passive: true });
        document.addEventListener('mousedown', setActive, { passive: true });
        document.addEventListener('wheel', setActive, { passive: true });
        document.addEventListener('keydown', setActive);
        document.addEventListener('touchstart', setActive, { passive: true });

        // Captions start visible when entering fullscreen, then fade out after the
        // idle window. Without this priming the user would need an initial mouse
        // movement to see the captions at all.
        setActive();
    },

    uninstallFullscreenIdleTracker() {
        if (window._mauiMediaPlayerIdleHandler) {
            const handler = window._mauiMediaPlayerIdleHandler;
            document.removeEventListener('mousemove', handler);
            document.removeEventListener('mousedown', handler);
            document.removeEventListener('wheel', handler);
            document.removeEventListener('keydown', handler);
            document.removeEventListener('touchstart', handler);
            window._mauiMediaPlayerIdleHandler = null;
        }

        if (window._mauiMediaPlayerIdleTimer) {
            clearTimeout(window._mauiMediaPlayerIdleTimer);
            window._mauiMediaPlayerIdleTimer = null;
        }

        const root = document.querySelector('[data-player-root]');
        root?.classList.remove('is-mouse-active');
    },

    registerAppShortcuts(dotNetReference) {
        window.mauiMediaPlayer.unregisterAppShortcuts();

        const keyHandler = (event) => {
            // Inside a modal, only Escape is forwarded (so the modal can close);
            // other shortcuts are suppressed so typing in an input doesn't trigger
            // playback toggles or layout changes.
            const inModal = !!event.target?.closest?.('.modal');
            const inEditable = !!event.target?.closest?.('input, textarea, select, [contenteditable=true]');

            // Escape always reaches the app handler; PlayerShell decides whether to
            // close a modal, exit fullscreen, or just refocus the canvas.
            if (event.key === 'Escape') {
                dotNetReference.invokeMethodAsync('OnAppShortcut', 'exitFullscreen').catch(() => { });
                return;
            }

            if (inModal || inEditable) {
                return;
            }

            // Modifier combos (Ctrl/Meta/Alt) are reserved for OS/browser shortcuts;
            // never consume them or we'd break user expectations like Ctrl+L for
            // address-bar focus in a future browser host or DevTools shortcuts here.
            // Shift is allowed because '?' is literally Shift+/ on US layouts.
            const hasReservedModifier = event.ctrlKey || event.metaKey || event.altKey;

            // '?' shortcut opens the keyboard help modal. Shift+/ on US layouts emits
            // '?' as the key; we also accept event.code === 'Slash' with Shift.
            if (!hasReservedModifier
                && (event.key === '?' || (event.code === 'Slash' && event.shiftKey))) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync('OnAppShortcut', 'showShortcuts').catch(() => { });
                return;
            }

            // 'L' toggles the playlist panel visibility.
            if (!hasReservedModifier && (event.key === 'l' || event.key === 'L')) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync('OnAppShortcut', 'togglePlaylist').catch(() => { });
                return;
            }

            if (!hasReservedModifier && (event.key === 'c' || event.key === 'C')) {
                event.preventDefault();
                dotNetReference.invokeMethodAsync('OnAppShortcut', 'toggleCompact').catch(() => { });
            }
        };

        let clickCount = 0;
        let clickTimer = null;
        const clickDelayMs = 280;

        const clearClickTimer = () => {
            if (clickTimer) {
                clearTimeout(clickTimer);
                clickTimer = null;
            }

            clickCount = 0;
        };

        const clickHandler = (event) => {
            // Anything inside an interactive widget or chrome region is excluded so
            // those clicks don't double-fire as canvas click/pause. The selector
            // list MUST stay in sync with the regions of the player shell defined
            // in the .razor templates.
            const ignoredTarget = event.target?.closest?.('a, button, input, label, select, textarea, .modal, .app-popover, .transport-bar, .player-header, .playlist-panel, .debug-footer, .toast-host');

            if (ignoredTarget) {
                clearClickTimer();
                return;
            }

            clickCount++;

            if (clickTimer) {
                clearTimeout(clickTimer);
                clickTimer = null;
            }

            clickTimer = setTimeout(() => {
                const shortcut = clickCount >= 3
                    ? 'previousFullscreenSingleMode'
                    : clickCount === 2
                        ? 'toggleFullscreen'
                        : 'togglePlayback';

                clickCount = 0;
                clickTimer = null;
                dotNetReference.invokeMethodAsync('OnAppShortcut', shortcut).catch(() => { });
            }, clickDelayMs);
        };

        window._mauiMediaPlayerKeyHandler = keyHandler;
        window._mauiMediaPlayerClickHandler = clickHandler;
        window._mauiMediaPlayerClearClickTimer = clearClickTimer;
        window.addEventListener('keydown', keyHandler);
        document.addEventListener('click', clickHandler);
    },

    unregisterAppShortcuts() {
        if (window._mauiMediaPlayerKeyHandler) {
            window.removeEventListener('keydown', window._mauiMediaPlayerKeyHandler);
            window._mauiMediaPlayerKeyHandler = null;
        }

        if (window._mauiMediaPlayerClickHandler) {
            document.removeEventListener('click', window._mauiMediaPlayerClickHandler);
            window._mauiMediaPlayerClickHandler = null;
        }

        if (window._mauiMediaPlayerClearClickTimer) {
            window._mauiMediaPlayerClearClickTimer();
            window._mauiMediaPlayerClearClickTimer = null;
        }
    },

    focusPlayerRoot() {
        const root = document.querySelector('[data-player-root]');

        if (root) {
            root.focus();
        }
    },

    // Switches Bootstrap's theme attribute at the document root. Only 'dark' and
    // 'light' are valid; anything else falls back to 'dark' so we never end up
    // with an unstyled tree if a stored preference goes corrupt.
    setTheme(theme) {
        const safe = theme === 'light' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-bs-theme', safe);
    },

    // Bridge the last drop event's file payload to Blazor. WebView2 (and most
    // browser hosts) intentionally do NOT expose the dropped file's OS path to
    // JavaScript for security reasons. We capture what's available — File.name
    // values — so Blazor can detect that something was dropped and surface a
    // helpful "use Add Media" message. A future platform-specific drop handler
    // could replace this with real file paths.
    _lastDroppedFileNames: [],

    captureDroppedFileNames(event) {
        try {
            const files = event?.dataTransfer?.files;
            const names = [];
            if (files && files.length) {
                for (let i = 0; i < files.length; i++) {
                    names.push(files[i].name || '');
                }
            }
            window.mauiMediaPlayer._lastDroppedFileNames = names;
        } catch (_) {
            window.mauiMediaPlayer._lastDroppedFileNames = [];
        }
    },

    collectDroppedPaths() {
        const names = window.mauiMediaPlayer._lastDroppedFileNames || [];
        window.mauiMediaPlayer._lastDroppedFileNames = [];
        return names;
    },

    // Installs a document-level capture listener so we can populate
    // _lastDroppedFileNames BEFORE Blazor's @ondrop fires (Blazor's handler runs
    // after the native drop, and by that time event.dataTransfer.files is no
    // longer the original FileList instance). Idempotent.
    installDropListener() {
        if (window._mauiMediaPlayerDropListener) {
            return;
        }

        const listener = (event) => window.mauiMediaPlayer.captureDroppedFileNames(event);
        document.addEventListener('drop', listener, true);
        window._mauiMediaPlayerDropListener = listener;
    },

    uninstallDropListener() {
        if (window._mauiMediaPlayerDropListener) {
            document.removeEventListener('drop', window._mauiMediaPlayerDropListener, true);
            window._mauiMediaPlayerDropListener = null;
        }
    },

    // Closes any open popover (.app-popover or its anchor) when a click lands
    // outside of all anchors. The .NET side passes a callback that resets state.
    installOutsideClickHandler(dotNetReference) {
        window.mauiMediaPlayer.uninstallOutsideClickHandler();

        const handler = (event) => {
            if (event.target?.closest?.('.icon-btn-anchor, .app-popover, .modal')) {
                return;
            }

            dotNetReference.invokeMethodAsync('OnOutsideClick').catch(() => { });
        };

        document.addEventListener('mousedown', handler, true);
        window._mauiMediaPlayerOutsideClickHandler = handler;
    },

    uninstallOutsideClickHandler() {
        if (window._mauiMediaPlayerOutsideClickHandler) {
            document.removeEventListener('mousedown', window._mauiMediaPlayerOutsideClickHandler, true);
            window._mauiMediaPlayerOutsideClickHandler = null;
        }
    }
};
