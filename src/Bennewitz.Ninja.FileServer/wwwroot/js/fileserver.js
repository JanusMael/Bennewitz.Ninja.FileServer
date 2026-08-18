/*
 * Colour-scheme toggle for rendered Markdown.
 *
 * Cycles auto -> light -> dark and remembers the choice. "auto" removes the attribute
 * altogether so the stylesheet's prefers-color-scheme rules govern again, rather than pinning
 * whatever the OS happened to prefer at the moment of the click.
 *
 * Written as a classic script with no dependencies: it is served from the component's own
 * asset endpoint and must run in a host that supplies no JavaScript stack of its own.
 */
(function () {
    'use strict';

    var KEY = 'bnfs-colour-scheme';
    var STATES = ['auto', 'light', 'dark'];
    var LABELS = { auto: 'Auto', light: 'Light', dark: 'Dark' };

    var root = document.querySelector('.bnfs-root');
    var button = document.querySelector('[data-bnfs-theme-toggle]');

    // The page may legitimately have neither: the same script is safe to serve for a listing,
    // which has no toggle.
    if (!root || !button) return;

    // localStorage access throws rather than returning null when storage is blocked — private
    // browsing, or a cross-site iframe under third-party storage restrictions. Losing the
    // remembered choice is acceptable there; losing the toggle is not, so both sides are guarded.
    function readStored() {
        try {
            return localStorage.getItem(KEY);
        } catch (e) {
            return null;
        }
    }

    function store(state) {
        try {
            localStorage.setItem(KEY, state);
        } catch (e) {
            /* not fatal: the toggle still works for this page view */
        }
    }

    var stored = readStored();
    var current = STATES.indexOf(stored) >= 0 ? stored : 'auto';

    function apply(state) {
        if (state === 'auto') {
            root.removeAttribute('data-bnfs-theme');
        } else {
            root.setAttribute('data-bnfs-theme', state);
        }

        button.textContent = LABELS[state];
        button.setAttribute('aria-label', 'Colour scheme: ' + LABELS[state]);
        current = state;
    }

    apply(current);

    button.addEventListener('click', function () {
        var next = STATES[(STATES.indexOf(current) + 1) % STATES.length];
        apply(next);
        store(next);
    });
}());
