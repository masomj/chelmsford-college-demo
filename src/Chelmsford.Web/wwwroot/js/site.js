/* Progressive enhancement only: every feature here works without JavaScript. */
(function () {
  'use strict';
  var doc = document.documentElement;
  doc.classList.add('js');

  // ---------- Mobile menu ----------
  var toggle = document.querySelector('[data-menu-toggle]');
  var menu = document.getElementById('mobile-nav');
  var noJsToggle = document.querySelector('.menu-toggle--nojs');
  if (toggle && menu) {
    toggle.hidden = false;
    if (noJsToggle) { noJsToggle.hidden = true; noJsToggle.style.display = 'none'; }
    var setMenu = function (open) {
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      menu.classList.toggle('is-open', open);
    };
    toggle.addEventListener('click', function () {
      setMenu(toggle.getAttribute('aria-expanded') !== 'true');
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && toggle.getAttribute('aria-expanded') === 'true') {
        setMenu(false);
        toggle.focus();
      }
    });
  }

  // ---------- Course filters: collapsible on small screens ----------
  var filterToggle = document.querySelector('[data-filters-toggle]');
  var filters = document.getElementById('filters');
  if (filterToggle && filters) {
    var startOpen = filterToggle.getAttribute('data-start-open') === 'true';
    filterToggle.hidden = false;
    var setFilters = function (open) {
      filterToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      filters.setAttribute('data-collapsed', open ? 'false' : 'true');
    };
    setFilters(startOpen);
    filterToggle.addEventListener('click', function () {
      var open = filterToggle.getAttribute('aria-expanded') !== 'true';
      setFilters(open);
      if (open) {
        var first = filters.querySelector('input, select, button');
        if (first) { first.focus(); }
      }
    });
  }

  // ---------- Course page sections: the disclosure pattern ----------
  // The HTML has every section open, with a button inside each heading, so the page reads in full
  // without JavaScript. Here all sections except the first are closed, and the buttons start working.
  var sections = Array.prototype.slice.call(document.querySelectorAll('[data-acc-section]'));
  var setSection = function (section, open) {
    var trigger = section.querySelector('[data-acc-trigger]');
    var panel = section.querySelector('[data-acc-panel]');
    if (!trigger || !panel) { return; }
    trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
    panel.hidden = !open;
  };
  sections.forEach(function (section, index) {
    var trigger = section.querySelector('[data-acc-trigger]');
    if (!trigger) { return; }
    trigger.removeAttribute('tabindex');
    setSection(section, index === 0);
    trigger.addEventListener('click', function () {
      setSection(section, trigger.getAttribute('aria-expanded') !== 'true');
    });
  });

  // Jump links (and a #section address) open the section they point to, then move focus to its heading button.
  var openTarget = function (hash, moveFocus) {
    if (!hash || hash.length < 2) { return false; }
    var target;
    try { target = document.getElementById(decodeURIComponent(hash.slice(1))); } catch (e) { return false; }
    if (!target || !target.hasAttribute('data-acc-section')) { return false; }
    setSection(target, true);
    var trigger = target.querySelector('[data-acc-trigger]');
    target.scrollIntoView({ block: 'start' });
    if (moveFocus && trigger) { trigger.focus({ preventScroll: true }); }
    return true;
  };
  document.addEventListener('click', function (e) {
    var link = e.target.closest && e.target.closest('a[href^="#"]');
    if (!link) { return; }
    var hash = link.getAttribute('href');
    if (openTarget(hash, true)) {
      e.preventDefault();
      history.replaceState(null, '', hash);
    }
  });
  openTarget(location.hash, false);

  // ---------- Payment link: "Someone else is paying" reveals payer fields ----------
  var someoneElse = document.querySelectorAll('input[name="someoneElse"]');
  var payerFields = document.getElementById('payer-fields');
  if (someoneElse.length && payerFields) {
    var sync = function () {
      var checked = document.querySelector('input[name="someoneElse"]:checked');
      payerFields.hidden = !(checked && checked.value === 'true');
    };
    someoneElse.forEach(function (r) { r.addEventListener('change', sync); });
    sync();
  }

  // ---------- Error summaries and confirmations take focus so screen readers announce them ----------
  var summary = document.querySelector('[data-error-summary]');
  var autofocus = document.querySelector('[data-autofocus]');
  if (summary) {
    summary.focus();
  } else if (autofocus) {
    autofocus.focus();
  }

  // ---------- Click-to-load video (privacy-friendly embed only after a click) ----------
  document.querySelectorAll('[data-video-embed]').forEach(function (button) {
    button.addEventListener('click', function () {
      var frame = document.createElement('iframe');
      frame.src = button.getAttribute('data-video-embed');
      frame.title = button.getAttribute('data-video-title') || 'Video';
      frame.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen';
      frame.allowFullscreen = true;
      var container = button.parentNode;
      container.replaceChild(frame, button);
      frame.focus();
    });
  });

  // ---------- Cookie banner ----------
  // The banner is a normal block at the top of the page. It opens on a first visit and from the
  // "Cookie settings" link in the footer, so a choice can be changed as easily as it was made.
  var COOKIE = 'cc_consent';
  var readConsent = function () {
    var match = document.cookie.match(/(?:^|;\s*)cc_consent=([^;]+)/);
    return match ? decodeURIComponent(match[1]) : null;
  };
  var loadAnalytics = function () {
    // Placeholder: on the live site the college's analytics tag loads here, and only here.
    if (window.__ccAnalyticsLoaded) { return; }
    window.__ccAnalyticsLoaded = true;
    window.dataLayer = window.dataLayer || [];
    window.dataLayer.push({ event: 'consent_granted' });
  };
  var banner = document.getElementById('cookie-banner');
  var consent = readConsent();
  if (consent === 'accepted') { loadAnalytics(); }

  if (banner) {
    var status = banner.querySelector('[data-cookie-status]');
    var choices = banner.querySelectorAll('[data-cookie-choice]');
    var heading = banner.querySelector('#cookie-heading');
    var hideButton = null;

    var showBanner = function (moveFocus) {
      banner.hidden = false;
      choices.forEach(function (b) { b.hidden = false; });
      if (hideButton) { hideButton.hidden = true; }
      if (status) { status.textContent = ''; }
      var current = readConsent();
      if (status && current) {
        status.textContent = current === 'accepted'
          ? 'You have accepted analytics cookies. You can change your choice here.'
          : 'You have rejected analytics cookies. You can change your choice here.';
      }
      if (moveFocus) {
        window.scrollTo(0, 0);
        if (heading) { heading.focus(); }
      }
    };

    choices.forEach(function (button) {
      button.addEventListener('click', function () {
        var choice = button.getAttribute('data-cookie-choice');
        var secure = location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = COOKIE + '=' + choice + '; Max-Age=31536000; Path=/; SameSite=Lax' + secure;
        if (choice === 'accepted') { loadAnalytics(); }
        choices.forEach(function (b) { b.hidden = true; });
        if (status) {
          status.textContent = choice === 'accepted'
            ? 'You have accepted analytics cookies. You can change this at any time with Cookie settings in the footer.'
            : 'You have rejected analytics cookies. Any analytics cookies already set are no longer used. You can change this at any time with Cookie settings in the footer.';
        }
        if (!hideButton) {
          hideButton = document.createElement('button');
          hideButton.type = 'button';
          hideButton.className = 'btn btn--ghost';
          hideButton.textContent = 'Hide cookie message';
          hideButton.addEventListener('click', function () {
            banner.hidden = true;
            var main = document.getElementById('main-content');
            if (main) { main.focus(); }
          });
          banner.querySelector('.cookie-banner__actions').appendChild(hideButton);
        }
        hideButton.hidden = false;
        hideButton.focus();
      });
    });

    if (!consent) { showBanner(false); }

    document.querySelectorAll('[data-cookie-reopen]').forEach(function (link) {
      link.addEventListener('click', function (e) {
        e.preventDefault();
        showBanner(true);
      });
    });
  }
})();
