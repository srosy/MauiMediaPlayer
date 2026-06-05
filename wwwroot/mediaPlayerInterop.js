window.mauiMediaPlayer = {
    initializeImage(image, dotNetReference, displayName) {
        if (!image) {
            return;
        }

        const log = (level, message) => {
            dotNetReference.invokeMethodAsync('OnMediaDebugEvent', level, message).catch(() => { });
        };

        const reportLoaded = () => {
            log('INFO', `Image loaded: ${displayName}; naturalSize=${image.naturalWidth}x${image.naturalHeight}`);
        };

        image.onload = () => {
            if (!image._mauiLoadLogged) {
                image._mauiLoadLogged = true;
                reportLoaded();
            }
        };

        image.onerror = () => {
            log('ERROR', `Image error: ${displayName}; currentSrc=${image.currentSrc || '(empty)'}`);
        };

        if (image.complete && image.naturalWidth > 0) {
            image._mauiLoadLogged = true;
            reportLoaded();
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

        const zoomLayer = pane.querySelector('.media-pane-zoom-layer');
        const isZoomed = zoomLayer?.classList.contains('is-zoomed') === true;
        const paneRect = pane.getBoundingClientRect();
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

        // Default: report the pane rect minus caption (existing behavior).
        let rect = {
            left: paneRect.left,
            top: paneRect.top,
            right: paneRect.right,
            bottom: paneRect.bottom - captionHeight
        };

        // When the zoom layer is scaled, its post-transform bounding box can exceed
        // the pane in any direction. Intersecting with the pane rect prevents the
        // native MediaElement overlay from being positioned outside the pane (which
        // would either hide it or paint over neighbour panes).
        if (isZoomed && zoomLayer) {
            const zoomRect = zoomLayer.getBoundingClientRect();
            rect = {
                left: Math.max(paneRect.left, zoomRect.left),
                top: Math.max(paneRect.top, zoomRect.top),
                right: Math.min(paneRect.right, zoomRect.right),
                bottom: Math.min(paneRect.bottom - captionHeight, zoomRect.bottom)
            };
        }

        return {
            x: rect.left,
            y: rect.top,
            width: Math.max(0, rect.right - rect.left),
            height: Math.max(0, rect.bottom - rect.top)
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

        document.querySelectorAll('.media-pane:not(.is-video-pane) img.media-image-ready').forEach((image) => {
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

            const zoomLayer = pane.querySelector('.media-pane-zoom-layer');
            if (zoomLayer && !zoomLayer._mauiBoundsObserved) {
                zoomLayer._mauiBoundsObserved = true;
                observer.observe(zoomLayer);
            }
        });
    },

    installPaneZoom() {
        // Idempotent install: reuse existing state so per-pane content changes
        // (which re-trigger this from MediaViewport.OnAfterRenderAsync) don't wipe
        // active zoom or detach the wheel handler from panes that are still mounted.
        // We only attach a wheel listener to panes that don't already have one.
        let state = window._mauiMediaPlayerPaneZoom;
        if (state) {
            document.querySelectorAll('[data-media-pane]').forEach((pane) => {
                if (pane._mauiPaneZoomWheel) {
                    return;
                }

                pane._mauiPaneZoomWheel = state.wheelHandler;
                pane.addEventListener('wheel', state.wheelHandler, { passive: false });
            });
            return;
        }

        const minScale = 1;
        const maxScale = 4;
        const wheelStep = 0.12;
        const releaseTolerancePx = 80;

        state = {
            slots: [null, null, null],
            activeSlot: null,
            wheelHandler: null,
            moveHandler: null
        };

        const getLayer = (slotIndex) =>
            document.querySelector(`[data-media-pane][data-slot="${slotIndex}"] .media-pane-zoom-layer`);

        const resetPaneZoom = (slotIndex, requestFlush) => {
            const layer = getLayer(slotIndex);
            const slotState = state.slots[slotIndex];

            if (slotState) {
                slotState.scale = minScale;
            }

            if (layer) {
                layer.style.transform = '';
                layer.style.transformOrigin = '';
                layer.classList.remove('is-zoomed');
            }

            if (state.activeSlot === slotIndex) {
                state.activeSlot = null;
            }

            if (requestFlush) {
                window.mauiMediaPlayer.requestMediaPaneBoundsFlush();
            }
        };

        const resetAllPaneZoom = (requestFlush) => {
            for (let slotIndex = 0; slotIndex < 3; slotIndex++) {
                resetPaneZoom(slotIndex, false);
            }

            state.activeSlot = null;

            if (requestFlush) {
                window.mauiMediaPlayer.requestMediaPaneBoundsFlush();
            }
        };

        const applyZoom = (slotIndex, layer, slotState) => {
            layer.style.transformOrigin = `${slotState.originX}px ${slotState.originY}px`;
            layer.style.transform = `scale(${slotState.scale})`;
            layer.classList.toggle('is-zoomed', slotState.scale > minScale + 0.001);
            window.mauiMediaPlayer.requestMediaPaneBoundsFlush();
        };

        state.wheelHandler = (event) => {
            const pane = event.currentTarget;
            const slotIndex = Number.parseInt(pane.getAttribute('data-slot') ?? '', 10);

            if (Number.isNaN(slotIndex) || slotIndex < 0 || slotIndex > 2) {
                return;
            }

            // Caption row hosts scrollable/click controls (back, skip, etc.).
            // Don't hijack wheel events that originate over the caption.
            if (typeof event.target?.closest === 'function'
                && event.target.closest('.media-caption')) {
                return;
            }

            const layer = getLayer(slotIndex);
            if (!layer) {
                return;
            }

            event.preventDefault();

            if (state.activeSlot !== null && state.activeSlot !== slotIndex) {
                resetPaneZoom(state.activeSlot, false);
            }

            let slotState = state.slots[slotIndex];
            if (!slotState) {
                slotState = {
                    scale: minScale,
                    anchorClientX: event.clientX,
                    anchorClientY: event.clientY,
                    originX: 0,
                    originY: 0
                };
                state.slots[slotIndex] = slotState;
            }

            const layerRect = layer.getBoundingClientRect();
            slotState.originX = event.clientX - layerRect.left;
            slotState.originY = event.clientY - layerRect.top;

            if (slotState.scale <= minScale + 0.001) {
                slotState.anchorClientX = event.clientX;
                slotState.anchorClientY = event.clientY;
            }

            const delta = event.deltaY < 0 ? wheelStep : -wheelStep;
            slotState.scale = Math.min(maxScale, Math.max(minScale, slotState.scale + delta));

            if (slotState.scale <= minScale + 0.001) {
                resetPaneZoom(slotIndex, true);
                return;
            }

            state.activeSlot = slotIndex;
            applyZoom(slotIndex, layer, slotState);
        };

        state.moveHandler = (event) => {
            const active = state.activeSlot;
            if (active === null) {
                return;
            }

            const slotState = state.slots[active];
            if (!slotState || slotState.scale <= minScale + 0.001) {
                return;
            }

            const dx = event.clientX - slotState.anchorClientX;
            const dy = event.clientY - slotState.anchorClientY;
            if (Math.hypot(dx, dy) > releaseTolerancePx) {
                resetPaneZoom(active, true);
            }
        };

        document.querySelectorAll('[data-media-pane]').forEach((pane) => {
            if (pane._mauiPaneZoomWheel) {
                pane.removeEventListener('wheel', pane._mauiPaneZoomWheel);
            }

            pane._mauiPaneZoomWheel = state.wheelHandler;
            pane.addEventListener('wheel', state.wheelHandler, { passive: false });
        });

        document.addEventListener('pointermove', state.moveHandler, { passive: true });
        window._mauiMediaPlayerPaneZoom = state;
    },

    disposePaneZoom() {
        const state = window._mauiMediaPlayerPaneZoom;
        if (!state) {
            return;
        }

        document.querySelectorAll('[data-media-pane]').forEach((pane) => {
            if (pane._mauiPaneZoomWheel) {
                pane.removeEventListener('wheel', pane._mauiPaneZoomWheel);
                delete pane._mauiPaneZoomWheel;
            }
        });

        if (state.moveHandler) {
            document.removeEventListener('pointermove', state.moveHandler);
        }

        for (let slotIndex = 0; slotIndex < 3; slotIndex++) {
            const layer = document.querySelector(
                `[data-media-pane][data-slot="${slotIndex}"] .media-pane-zoom-layer`);
            if (layer) {
                layer.style.transform = '';
                layer.style.transformOrigin = '';
                layer.classList.remove('is-zoomed');
                delete layer._mauiBoundsObserved;
            }
        }

        window._mauiMediaPlayerPaneZoom = null;
    },

    _paneSwapDrag: null,

    _paneSwapLog(dotNetReference, level, message) {
        if (!dotNetReference) {
            return;
        }

        dotNetReference.invokeMethodAsync('OnMediaDebugEvent', level, `PaneSwap: ${message}`).catch(() => { });
    },

    _setupPaneSwapDragSession(dotNetReference, sourceSlot, startX, startY, thresholdPx, ghostOffsetPx, options) {
        const threshold = typeof thresholdPx === 'number' && thresholdPx > 0 ? thresholdPx : 8;
        const ghostOffset = typeof ghostOffsetPx === 'number' ? ghostOffsetPx : 18;
        const nativeDriven = options?.nativeDriven === true;
        const log = (level, msg) => this._paneSwapLog(dotNetReference, level, msg);

        const viewport = document.querySelector('.media-viewport');
        if (!viewport) {
            log('ERROR', 'JS session begin failed — .media-viewport not found.');
            dotNetReference.invokeMethodAsync(
                'OnPaneSwapDragComplete',
                sourceSlot,
                -1,
                false,
                'begin-failed').catch(() => { });
            return null;
        }

        const sourcePane = document.querySelector(`[data-media-pane][data-slot="${sourceSlot}"]`);
        let armed = false;
        let dropTarget = -1;
        let ghostEl = null;
        let lastMoveLogAt = 0;

        const clearDropTarget = () => {
            document.querySelectorAll('.media-pane.is-pane-drop-target').forEach((pane) => {
                pane.classList.remove('is-pane-drop-target');
            });
            dropTarget = -1;
        };

        const setDropTarget = (slot) => {
            const next = typeof slot === 'number' && slot >= 0 && slot !== sourceSlot ? slot : -1;
            if (next === dropTarget) {
                return;
            }

            clearDropTarget();
            dropTarget = next;

            if (next >= 0) {
                const pane = document.querySelector(`[data-media-pane][data-slot="${next}"]`);
                pane?.classList.add('is-pane-drop-target');
            }

            log('DEBUG', `drop target ${next >= 0 ? `slot ${next + 1}` : 'none'} (source slot ${sourceSlot + 1}).`);
        };

        const createGhost = () => {
            ghostEl = document.createElement('div');
            ghostEl.className = 'media-pane-drag-ghost';

            const img = sourcePane?.querySelector(
                '.media-pane-zoom-layer img.media-image-ready, .media-pane-zoom-layer img.media-image-holdover');
            if (img) {
                const clone = img.cloneNode(true);
                clone.removeAttribute('id');
                ghostEl.appendChild(clone);
            } else {
                const title = sourcePane?.querySelector('.media-caption-title')?.textContent?.trim() ?? '';
                const fallback = document.createElement('div');
                fallback.className = 'media-pane-drag-ghost-fallback';
                if (sourcePane?.classList.contains('is-video-pane')) {
                    const film = document.createElement('span');
                    film.textContent = 'Video';
                    film.style.opacity = '0.55';
                    fallback.appendChild(film);
                }

                if (title) {
                    const titleEl = document.createElement('span');
                    titleEl.className = 'media-pane-drag-ghost-title';
                    titleEl.textContent = title;
                    fallback.appendChild(titleEl);
                }

                ghostEl.appendChild(fallback);
            }

            document.body.appendChild(ghostEl);
        };

        const moveGhost = (clientX, clientY) => {
            if (!ghostEl) {
                return;
            }

            ghostEl.style.left = `${clientX + ghostOffset}px`;
            ghostEl.style.top = `${clientY + ghostOffset}px`;
        };

        const hitTestSlot = (clientX, clientY, logDetails) => {
            if (ghostEl) {
                ghostEl.style.pointerEvents = 'none';
            }

            const hit = document.elementFromPoint(clientX, clientY);
            const pane = hit?.closest?.('[data-media-pane][data-slot]');
            if (!pane) {
                if (logDetails) {
                    const hitLabel = hit
                        ? `${hit.tagName}${hit.className ? '.' + String(hit.className).trim().split(/\s+/).slice(0, 2).join('.') : ''}`
                        : '(none)';
                    log('DEBUG', `hit-test miss at (${clientX.toFixed(0)},${clientY.toFixed(0)}); top=${hitLabel}.`);
                }

                return -1;
            }

            const slotIndex = Number.parseInt(pane.getAttribute('data-slot') ?? '', 10);
            const resolved = Number.isNaN(slotIndex) ? -1 : slotIndex;

            if (logDetails) {
                log('DEBUG', `hit-test at (${clientX.toFixed(0)},${clientY.toFixed(0)}) → slot ${resolved >= 0 ? resolved + 1 : '—'}.`);
            }

            return resolved;
        };

        const armDrag = () => {
            armed = true;
            viewport.classList.add('is-pane-swap-active');
            sourcePane?.classList.add('is-pane-drag-source');
            createGhost();
            log('DEBUG', `armed; source slot ${sourceSlot + 1}.`);
            dotNetReference.invokeMethodAsync('OnPaneSwapDragArmed', sourceSlot).catch(() => { });
        };

        const finishDrag = (reason, clientX, clientY) => {
            const session = this._paneSwapDrag;
            if (!session || session.sourceSlot !== sourceSlot) {
                return;
            }

            if (!nativeDriven && session.pointerId !== options?.pointerId) {
                return;
            }

            if (session.onMove) {
                window.removeEventListener('pointermove', session.onMove, { capture: true });
            }

            if (session.onUp) {
                window.removeEventListener('pointerup', session.onUp, { capture: true });
            }

            if (armed) {
                const releaseHit = hitTestSlot(clientX, clientY, true);
                if (releaseHit >= 0 && releaseHit !== sourceSlot) {
                    dropTarget = releaseHit;
                }
            }

            const targetSlot = armed ? dropTarget : -1;

            sourcePane?.classList.remove('is-pane-drag-source');
            clearDropTarget();
            viewport.classList.remove('is-pane-swap-active');
            ghostEl?.remove();
            ghostEl = null;

            log('DEBUG', `JS session end (${reason}); armed=${armed}; source=${sourceSlot + 1}; target=${targetSlot >= 0 ? targetSlot + 1 : 'none'}.`);

            this._paneSwapDrag = null;

            dotNetReference.invokeMethodAsync(
                'OnPaneSwapDragComplete',
                sourceSlot,
                targetSlot,
                armed,
                reason).catch(() => { });
        };

        const moveTo = (clientX, clientY) => {
            if (!armed) {
                const dx = clientX - startX;
                const dy = clientY - startY;
                if (dx * dx + dy * dy < threshold * threshold) {
                    return false;
                }

                armDrag();
            }

            moveGhost(clientX, clientY);

            const now = performance.now();
            const logDetails = now - lastMoveLogAt > 400;
            if (logDetails) {
                lastMoveLogAt = now;
            }

            setDropTarget(hitTestSlot(clientX, clientY, logDetails));

            if (logDetails) {
                log('TRACE', `move at=(${clientX.toFixed(0)},${clientY.toFixed(0)}); target=${dropTarget >= 0 ? dropTarget + 1 : '—'}.`);
            }

            return armed;
        };

        return {
            nativeDriven,
            sourceSlot,
            dotNetReference,
            threshold,
            startX,
            startY,
            moveTo,
            finishDrag,
            isArmed: () => armed
        };
    },

    // Document-level pointer listeners during pane drag-swap. Drag visuals (ghost,
    // source dim, drop-target ring) are applied purely in JS so Blazor never
    // re-renders mid-drag — a StateHasChanged during capture causes pointercancel
    // in WebView2 and kills the session.
    startPaneSwapDrag(dotNetReference, pointerId, sourceSlot, startX, startY, thresholdPx, ghostOffsetPx) {
        this.stopPaneSwapDrag(dotNetReference, 'restart');

        const log = (level, msg) => this._paneSwapLog(dotNetReference, level, msg);
        log('DEBUG', `JS session begin; pointerId=${pointerId}; sourceSlot=${sourceSlot + 1}; at=(${startX.toFixed(0)},${startY.toFixed(0)}).`);

        const core = this._setupPaneSwapDragSession(
            dotNetReference,
            sourceSlot,
            startX,
            startY,
            thresholdPx,
            ghostOffsetPx,
            { nativeDriven: false, pointerId });

        if (!core) {
            return;
        }

        const onMove = (event) => {
            if (event.pointerId !== pointerId) {
                return;
            }

            event.preventDefault();
            core.moveTo(event.clientX, event.clientY);
        };

        const onUp = (event) => {
            if (event.pointerId !== pointerId) {
                return;
            }

            event.preventDefault();
            core.finishDrag('up', event.clientX, event.clientY);
        };

        // Do NOT listen for pointercancel — WebView2 fires spurious cancels when Blazor
        // re-renders or when native overlays participate. pointerup always follows a real release.
        window.addEventListener('pointermove', onMove, { capture: true });
        window.addEventListener('pointerup', onUp, { capture: true });

        this._paneSwapDrag = {
            pointerId,
            onMove,
            onUp,
            sourceSlot,
            dotNetReference,
            finishDrag: core.finishDrag
        };
    },

    // Native MediaElement pan gestures cannot reach the WebView pointer pipeline.
    // MainPage forwards move/up in WebView client coordinates via these entry points.
    startPaneSwapDragFromNative(dotNetReference, sourceSlot, startX, startY, thresholdPx, ghostOffsetPx) {
        this.stopPaneSwapDrag(dotNetReference, 'restart');

        const log = (level, msg) => this._paneSwapLog(dotNetReference, level, msg);
        log('DEBUG', `JS native session begin; sourceSlot=${sourceSlot + 1}; at=(${startX.toFixed(0)},${startY.toFixed(0)}).`);

        const core = this._setupPaneSwapDragSession(
            dotNetReference,
            sourceSlot,
            startX,
            startY,
            thresholdPx,
            ghostOffsetPx,
            { nativeDriven: true });

        if (!core) {
            return;
        }

        this._paneSwapDrag = {
            nativeDriven: true,
            sourceSlot,
            dotNetReference,
            moveTo: core.moveTo,
            finishDrag: core.finishDrag
        };
    },

    updatePaneSwapDragFromNative(clientX, clientY) {
        const session = this._paneSwapDrag;
        if (!session?.nativeDriven || typeof session.moveTo !== 'function') {
            return;
        }

        session.moveTo(clientX, clientY);
    },

    finishPaneSwapDragFromNative(clientX, clientY, reason) {
        const session = this._paneSwapDrag;
        if (!session?.nativeDriven || typeof session.finishDrag !== 'function') {
            return;
        }

        session.finishDrag(reason || 'native-up', clientX, clientY);
    },

    stopPaneSwapDrag(dotNetReference, reason) {
        const session = this._paneSwapDrag;
        if (!session) {
            return;
        }

        if (session.onMove) {
            window.removeEventListener('pointermove', session.onMove, { capture: true });
        }

        if (session.onUp) {
            window.removeEventListener('pointerup', session.onUp, { capture: true });
        }

        document.querySelector('.media-viewport')?.classList.remove('is-pane-swap-active');
        document.querySelectorAll('.media-pane.is-pane-drag-source').forEach((pane) => {
            pane.classList.remove('is-pane-drag-source');
        });
        document.querySelectorAll('.media-pane.is-pane-drop-target').forEach((pane) => {
            pane.classList.remove('is-pane-drop-target');
        });
        document.querySelectorAll('.media-pane-drag-ghost').forEach((ghost) => {
            ghost.remove();
        });

        const sessionLabel = session.nativeDriven
            ? 'native'
            : `pointerId=${session.pointerId}`;
        this._paneSwapLog(dotNetReference ?? session.dotNetReference, 'DEBUG', `JS session aborted (${reason}); ${sessionLabel}.`);
        this._paneSwapDrag = null;
    },

    // Resolves which media pane sits under the cursor. Used during pane drag-swap
    // because setPointerCapture on the viewport retargets pointer events away from
    // child panes, so Blazor pointerenter/leave on zoom layers never fire.
    getMediaPaneSlotAtPoint(clientX, clientY, dotNetReference, logDetails) {
        if (typeof clientX !== 'number' || typeof clientY !== 'number') {
            if (logDetails) {
                this._paneSwapLog(dotNetReference, 'DEBUG', 'hit-test rejected — invalid coordinates.');
            }

            return -1;
        }

        const hit = document.elementFromPoint(clientX, clientY);
        const pane = hit?.closest?.('[data-media-pane][data-slot]');
        if (!pane) {
            if (logDetails) {
                const hitLabel = hit
                    ? `${hit.tagName}${hit.className ? '.' + String(hit.className).trim().split(/\s+/).slice(0, 2).join('.') : ''}`
                    : '(none)';
                this._paneSwapLog(dotNetReference, 'DEBUG', `hit-test miss at (${clientX.toFixed(0)},${clientY.toFixed(0)}); top=${hitLabel}.`);
            }

            return -1;
        }

        const slotIndex = Number.parseInt(pane.getAttribute('data-slot') ?? '', 10);
        const resolved = Number.isNaN(slotIndex) ? -1 : slotIndex;

        if (logDetails) {
            this._paneSwapLog(
                dotNetReference,
                'DEBUG',
                `hit-test at (${clientX.toFixed(0)},${clientY.toFixed(0)}) → slot ${resolved >= 0 ? resolved + 1 : '—'}.`);
        }

        return resolved;
    },

    // Legacy entry points kept for any stale callers.
    setViewportPointerCapture(pointerId) {
        if (typeof pointerId !== 'number' || pointerId < 0) {
            return;
        }

        const viewport = document.querySelector('.media-viewport');
        if (!viewport || typeof viewport.setPointerCapture !== 'function') {
            return;
        }

        try {
            viewport.setPointerCapture(pointerId);
        } catch (_) {
        }
    },

    releaseViewportPointerCapture(pointerId) {
        if (typeof pointerId !== 'number' || pointerId < 0) {
            return;
        }

        const viewport = document.querySelector('.media-viewport');
        if (!viewport || typeof viewport.releasePointerCapture !== 'function') {
            return;
        }

        try {
            if (viewport.hasPointerCapture?.(pointerId)) {
                viewport.releasePointerCapture(pointerId);
            }
        } catch (_) {
        }
    },

    invalidatePaneZoom() {
        const state = window._mauiMediaPlayerPaneZoom;
        if (!state) {
            return;
        }

        for (let slotIndex = 0; slotIndex < 3; slotIndex++) {
            const layer = document.querySelector(
                `[data-media-pane][data-slot="${slotIndex}"] .media-pane-zoom-layer`);
            if (layer) {
                layer.style.transform = '';
                layer.style.transformOrigin = '';
                layer.classList.remove('is-zoomed');
            }

            state.slots[slotIndex] = null;
        }

        state.activeSlot = null;
        window.mauiMediaPlayer.requestMediaPaneBoundsFlush();
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
        window.mauiMediaPlayer.disposePaneZoom();

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
