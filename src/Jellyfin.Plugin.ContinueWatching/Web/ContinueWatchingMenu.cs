using System;

namespace Jellyfin.Plugin.ContinueWatching.Web;

public sealed class PatchRequestPayload
{
    public string? Contents { get; set; }
}

public static class ContinueWatchingMenuTransformation
{
    private const string ScriptOpenMarker = "<script data-continue-watching-remove=\"true\">";
    private const string ScriptCloseMarker = "</script>";

    public static string IndexTransformation(PatchRequestPayload payload)
    {
        string contents = payload.Contents ?? string.Empty;

        int existingStart = contents.IndexOf(ScriptOpenMarker, StringComparison.Ordinal);
        if (existingStart >= 0)
        {
            int existingEnd = contents.IndexOf(ScriptCloseMarker, existingStart, StringComparison.Ordinal);
            if (existingEnd >= 0)
            {
                contents = contents.Remove(existingStart, existingEnd + ScriptCloseMarker.Length - existingStart);
            }
        }

        const string ClosingBody = "</body>";
        int bodyIndex = contents.LastIndexOf(ClosingBody, StringComparison.OrdinalIgnoreCase);
        if (bodyIndex < 0)
        {
            return contents;
        }

        string script = ScriptOpenMarker + InjectedScript + ScriptCloseMarker;
        return contents.Insert(bodyIndex, script);
    }

    private const string InjectedScript = """

(function () {
  function isVisible(el) { return !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length); }

  // ---------------------------------------------------------------------------
  // Why this file does what it does (verified against Jellyfin Web 12.1.0)
  //
  // The home page's Continue Watching row is NOT rendered from a live request. The
  // section's fetchData calls queryClient.fetchQuery() with the key
  //   ["User", <userId>, "ResumeItems", <params>]
  // and Jellyfin Web builds its QueryClient with staleTime 60_000 / gcTime 24h, then
  // persists the whole cache to IndexedDB ("keyval-store" -> "jellyfin-query-cache").
  //
  // Two consequences, both confirmed live:
  //   1. While that entry is fresh, fetchQuery returns the cached array and issues no
  //      HTTP request at all -- so busting the request URL cannot help, and neither can
  //      any amount of server-side correctness.
  //   2. Every watched-state change in Jellyfin Web invalidates ["User", <userId>,
  //      "Items"], which does NOT prefix-match "ResumeItems". So nothing ever
  //      invalidates this row. Because the cache is restored from IndexedDB, even a full
  //      page reload keeps serving the stale row until the 60s window elapses.
  //
  // That is the "the section doesn't update, or updates only after ~30s of refreshing"
  // behaviour. The fix is to reach the QueryClient, keep this one key always-stale, and
  // explicitly refresh the row whenever watched state changes.
  // ---------------------------------------------------------------------------

  var RESUME_KEY_SEGMENT = 'ResumeItems';
  var queryClient = null;
  var queryDefaultsApplied = false;

  function currentUserId() {
    try {
      return window.ApiClient && window.ApiClient.getCurrentUserId
        ? window.ApiClient.getCurrentUserId()
        : null;
    } catch (err) {
      return null;
    }
  }

  // React does not expose the QueryClient globally. It is reachable from the fiber tree:
  // QueryClientProvider is rendered with the client as its `client` prop. Duck-type on the
  // public QueryClient API rather than on any minified name, so a Jellyfin Web rebuild
  // (which reshuffles minified identifiers) cannot silently break this.
  function findQueryClient() {
    if (queryClient) return queryClient;
    try {
      var rootFiber = null;
      var nodes = document.querySelectorAll('div');
      for (var i = 0; i < nodes.length && !rootFiber; i++) {
        var keys = Object.keys(nodes[i]);
        for (var k = 0; k < keys.length; k++) {
          if (keys[k].indexOf('__reactFiber$') === 0 || keys[k].indexOf('__reactContainer$') === 0) {
            rootFiber = nodes[i][keys[k]];
            break;
          }
        }
      }
      if (!rootFiber) return null;
      while (rootFiber.return) rootFiber = rootFiber.return;

      var seen = new Set();
      var stack = [rootFiber];
      while (stack.length) {
        var fiber = stack.pop();
        if (!fiber || seen.has(fiber)) continue;
        seen.add(fiber);
        var props = fiber.memoizedProps;
        if (props && props.client &&
            typeof props.client.invalidateQueries === 'function' &&
            typeof props.client.getQueryCache === 'function' &&
            typeof props.client.setQueryDefaults === 'function') {
          queryClient = props.client;
          return queryClient;
        }
        if (fiber.child) stack.push(fiber.child);
        if (fiber.sibling) stack.push(fiber.sibling);
      }
    } catch (err) {
      console.warn('[ContinueWatching] could not reach the query cache', err);
    }
    return null;
  }

  // Pin this one key to staleTime 0. Every other query keeps Jellyfin's own caching, so
  // this does not increase load anywhere else -- it only stops the Continue Watching row
  // from rendering a cached (or IndexedDB-restored) list after the cursor has moved.
  function applyQueryDefaults(client) {
    if (queryDefaultsApplied) return;
    var userId = currentUserId();
    if (!client || !userId) return;
    try {
      client.setQueryDefaults(['User', userId, RESUME_KEY_SEGMENT], { staleTime: 0 });
      queryDefaultsApplied = true;
      // Nothing is invalidated here on purpose. A query rehydrated from IndexedDB carries
      // data but no queryFn until the home section asks for it again, so forcing a refetch
      // at startup makes React Query throw "Missing queryFn", park the query in an error
      // state, and the Continue Watching row disappears entirely. staleTime 0 is enough:
      // the next fetchQuery supplies the queryFn and refetches because the data is stale.
    } catch (err) {
      console.warn('[ContinueWatching] could not pin resume query freshness', err);
    }
  }

  function invalidateResumeQueries(client, userId) {
    try {
      // refetchType 'none' is deliberate: never ask React Query to refetch this itself. The
      // row has no observers (the home screen pulls it with fetchQuery rather than
      // subscribing) and a rehydrated query has no queryFn, so a forced refetch would throw.
      // Marking it stale is all that is needed -- refreshItems() below does the real fetch
      // through the section's own fetchData, which does supply a queryFn.
      client.invalidateQueries({ queryKey: ['User', userId, RESUME_KEY_SEGMENT], refetchType: 'none' });
      // Belt and braces for the Home Screen Sections layout, which wraps the same data.
      client.invalidateQueries({ queryKey: ['SuggestionSectionWithItems'], refetchType: 'none' });
    } catch (err) {
      console.warn('[ContinueWatching] failed to invalidate resume queries', err);
    }
  }

  function getContinueWatchingScroller() {
    var headers = document.querySelectorAll('h2');
    var header = null;
    for (var i = 0; i < headers.length; i++) {
      if (isVisible(headers[i]) && headers[i].textContent.trim() === 'Continue Watching') {
        header = headers[i];
        break;
      }
    }
    if (!header) return null;
    var section = header.closest('div');
    return section ? section.parentElement.querySelector('.itemsContainer, .scrollSlider') : null;
  }

  // Jellyfin attaches refreshItems() to each home section's .itemsContainer; calling it
  // re-runs that section's fetchData and rebuilds its cards in place. Combined with the
  // invalidation above it re-renders the row without a page reload.
  function continueWatchingContainers() {
    var containers = [];
    var scroller = getContinueWatchingScroller();
    if (scroller) {
      var container = scroller.classList.contains('itemsContainer')
        ? scroller
        : scroller.closest('.itemsContainer');
      if (container && typeof container.refreshItems === 'function') {
        skipUnchangedRefreshes(container);
        containers.push(container);
      }
    }
    return containers;
  }

  // refreshItems() always rebuilds the row's HTML, so every card image lazy-loads again --
  // a visible flash even when the list is identical. One mark-played triggers several
  // refreshes (Jellyfin's own, one per UserDataChanged echo, ours and its follow-up), and
  // most return the list already on screen, so the row flashed three or four times.
  // Wrap this container's refreshItems: fetch through the section's own fetchData, and only
  // hand the result to Jellyfin's renderer when the cards or their progress differ.
  function renderedSignature(container) {
    var cards = container.querySelectorAll('.card[data-id]');
    var parts = [];
    for (var i = 0; i < cards.length; i++) {
      var bar = cards[i].querySelector('.itemProgressBarForeground');
      var pct = bar ? parseFloat(bar.style.width) || 0 : 0;
      parts.push(cards[i].getAttribute('data-id') + ':' + Math.round(pct));
    }
    return parts.join(',');
  }

  function fetchedSignature(result) {
    var items = result && result.Items ? result.Items : result;
    if (!Array.isArray(items)) return null;
    var parts = [];
    for (var i = 0; i < items.length; i++) {
      var userData = items[i].UserData;
      var pct = userData && !userData.Played && userData.PlayedPercentage ? userData.PlayedPercentage : 0;
      parts.push(items[i].Id + ':' + Math.round(pct));
    }
    return parts.join(',');
  }

  function skipUnchangedRefreshes(container) {
    if (container.__cwSkipsUnchanged) return;
    container.__cwSkipsUnchanged = true;
    var originalRefresh = container.refreshItems;
    var inFlight = null;
    var again = false;

    function run() {
      // A paused container (home tab not visible) only records needsRefresh; let Jellyfin
      // handle that as usual.
      if (container.paused || typeof container.fetchData !== 'function') {
        return Promise.resolve(originalRefresh.call(container));
      }
      var fetchData = container.fetchData;
      return Promise.resolve(fetchData.call(container)).then(function (result) {
        if (fetchedSignature(result) === renderedSignature(container)) {
          container.needsRefresh = false;
          return;
        }
        // Render the data we already have rather than fetching it a second time.
        container.fetchData = function () { return Promise.resolve(result); };
        try {
          return originalRefresh.call(container);
        } finally {
          container.fetchData = fetchData;
        }
      });
    }

    container.refreshItems = function () {
      // Collapse overlapping refreshes into one follow-up run so the last one still sees
      // the newest data.
      if (inFlight) {
        again = true;
        return inFlight;
      }
      inFlight = run().finally(function () {
        inFlight = null;
        if (again) {
          again = false;
          container.refreshItems().catch(function (err) {
            console.warn('[ContinueWatching] section refresh failed', err);
          });
        }
      });
      return inFlight;
    };
  }

  var refreshTimer = null;

  function refreshContinueWatching() {
    var client = findQueryClient();
    if (!client) return;
    var userId = currentUserId();
    if (!userId) return;

    applyQueryDefaults(client);
    invalidateResumeQueries(client, userId);

    var containers = continueWatchingContainers();
    for (var i = 0; i < containers.length; i++) {
      try {
        var result = containers[i].refreshItems();
        if (result && typeof result.catch === 'function') {
          result.catch(function (err) {
            console.warn('[ContinueWatching] section refresh failed', err);
          });
        }
      } catch (err) {
        console.warn('[ContinueWatching] section refresh threw', err);
      }
    }
  }

  function scheduleRefresh() {
    if (refreshTimer) clearTimeout(refreshTimer);
    // Coalesce the burst of events a single mark-played produces, then refresh once more a
    // little later: watched-state changes that arrive over the WebSocket are applied by a
    // fire-and-forget server-side handler, which can land just after the first refresh.
    refreshTimer = setTimeout(function () {
      refreshTimer = null;
      refreshContinueWatching();
      setTimeout(refreshContinueWatching, 1500);
    }, 150);
  }

  // Acquire the client as soon as React has mounted. Polling (rather than a one-shot read)
  // is required because this script runs before the deferred app bundle executes.
  var clientPollAttempts = 0;
  var clientPollTimer = setInterval(function () {
    clientPollAttempts++;
    var client = findQueryClient();
    if (client) {
      applyQueryDefaults(client);
      if (queryDefaultsApplied) {
        clearInterval(clientPollTimer);
        return;
      }
    }
    if (clientPollAttempts > 200) clearInterval(clientPollTimer);
  }, 100);

  // Watched state can also change from another device or another Jellyfin client. The
  // server announces those over the WebSocket, so refresh on them too instead of only on
  // this page's own button clicks.
  if (!window.__continueWatchingSocketHook && typeof window.WebSocket === 'function') {
    window.__continueWatchingSocketHook = true;
    try {
      var NativeWebSocket = window.WebSocket;
      var PatchedWebSocket = class extends NativeWebSocket {
        constructor() {
          super(...arguments);
          this.addEventListener('message', function (event) {
            try {
              if (typeof event.data !== 'string' || event.data.indexOf('UserDataChanged') < 0) return;
              var message = JSON.parse(event.data);
              if (message && message.MessageType === 'UserDataChanged') scheduleRefresh();
            } catch (err) { /* not a message we care about */ }
          });
        }
      };
      window.WebSocket = PatchedWebSocket;
    } catch (err) {
      console.warn('[ContinueWatching] could not observe user-data messages', err);
    }
  }

  var pending = null;

  function delay(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  // ApiClient.ajax resolves with the Response object rather than rejecting on a non-2xx
  // status, so success must be read from the resolved value. A falsy/empty result is NOT
  // proof of success: both a 204 and a failed request can resolve with an empty body.
  function succeeded(response) {
    return !!response && response.ok === true;
  }

  async function notifyWhenPlayed(itemId, userId) {
    try {
      // Do not advance the plugin cursor unless Jellyfin's own endpoint really persisted the
      // watched state. A few short retries cover the normal request/render ordering.
      for (var attempt = 0; attempt < 20; attempt++) {
        var item = await window.ApiClient.getItem(userId, itemId);
        if (item && item.UserData && item.UserData.Played) {
          // The first call to this endpoint after a period of idleness has been seen to fail
          // intermittently; an immediate retry has been reliable. The endpoint awaits the
          // cursor update before returning 204, so a success here is a genuine guarantee that
          // a subsequent read returns the advanced cursor.
          for (var postAttempt = 0; postAttempt < 4; postAttempt++) {
            var postResult = null;
            try {
              postResult = await window.ApiClient.ajax({
                type: 'POST',
                url: window.ApiClient.getUrl('ContinueWatching/' + itemId + '/played', { userId: userId })
              });
            } catch (postErr) {
              postResult = null;
            }
            if (succeeded(postResult)) return;
            if (postAttempt < 3) await delay(250 * (postAttempt + 1));
          }
          console.warn('[ContinueWatching] could not notify the server for item', itemId);
          return;
        }
        await delay(100);
      }
      console.warn('[ContinueWatching] watched state was not persisted for item', itemId);
    } catch (err) {
      console.error('[ContinueWatching] failed to advance watched item', err);
    } finally {
      // Always refresh, even when the notification failed. The resume endpoint reconciles
      // played cursors as it reads, so the refetch still returns the correct episode -- and
      // without this the row would sit on cached data for the full staleTime window.
      scheduleRefresh();
    }
  }

  function detailPageItemId() {
    var hash = window.location.hash || '';
    var query = hash.indexOf('?') >= 0 ? hash.slice(hash.indexOf('?') + 1) : window.location.search.slice(1);
    var id = new URLSearchParams(query).get('id');
    return id ? id.replace(/-/g, '').toLowerCase() : null;
  }

  // Resolves to the id of the row entry this item belongs to (the episode for a series or
  // season), or null when the item is not in Continue Watching.
  function findResumeEntry(itemId, userId) {
    return window.ApiClient.getJSON(window.ApiClient.getUrl('UserItems/Resume', { userId: userId, enableImages: false }))
      .then(function (result) {
        var items = (result && result.Items) || [];
        for (var i = 0; i < items.length; i++) {
          var ids = [items[i].Id, items[i].SeriesId, items[i].SeasonId];
          for (var j = 0; j < ids.length; j++) {
            if (ids[j] && ids[j].replace(/-/g, '').toLowerCase() === itemId) return items[i].Id;
          }
        }
        return null;
      })
      .catch(function (err) {
        console.warn('[ContinueWatching] could not read the Continue Watching list', err);
        return null;
      });
  }

  document.addEventListener('click', function (e) {
    var playStateButton = e.target.closest('button[is="emby-playstatebutton"], .playstatebutton');
    if (playStateButton) {
      var playStateScroller = getContinueWatchingScroller();
      var playStateCard = playStateButton.closest('.card');
      if (playStateScroller && playStateCard && playStateScroller.contains(playStateCard) && window.ApiClient) {
        var wasPlayed = playStateButton.getAttribute('data-played') === 'true' ||
          (playStateButton.getAttribute('title') || '').toLowerCase() === 'mark unplayed';
        var itemId = playStateCard.getAttribute('data-id') || playStateButton.getAttribute('data-id');
        if (itemId) {
          if (wasPlayed) {
            // Marking unplayed moves the cursor back; the row needs the same refresh.
            scheduleRefresh();
          } else {
            notifyWhenPlayed(itemId, window.ApiClient.getCurrentUserId());
          }
        }
      }
    }

    // Details page "..." menu. Themes such as NetFin hide the home cards' overlay buttons, so
    // this is the one entry point that survives a restyled home screen. The page shows a
    // movie, series, season or episode, but only one display item per title is in the row, so
    // look the row up and resolve the page's id to that entry before offering the button.
    var moreButton = e.target.closest('.btnMoreCommands');
    if (moreButton && window.ApiClient) {
      var detailId = detailPageItemId();
      if (!detailId) { pending = null; return; }
      var detailUserId = window.ApiClient.getCurrentUserId();
      pending = { userId: detailUserId, card: null, ready: findResumeEntry(detailId, detailUserId) };
      return;
    }

    var btn = e.target.closest('button[data-action="menu"]');
    if (!btn) return;
    var scroller = getContinueWatchingScroller();
    if (!scroller) { pending = null; return; }
    var card = btn.closest('.card');
    if (card && scroller.contains(card) && window.ApiClient) {
      pending = { userId: window.ApiClient.getCurrentUserId(), card: card, ready: Promise.resolve(card.getAttribute('data-id')) };
    } else {
      pending = null;
    }
  }, true);

  // Jellyfin refreshes the row on its own too, before this script ever calls it, so wrap the
  // container as soon as the home screen renders it -- not only on our first refresh.
  var wrapCheckQueued = false;

  var observer = new MutationObserver(function (mutations) {
    if (!wrapCheckQueued) {
      wrapCheckQueued = true;
      requestAnimationFrame(function () {
        wrapCheckQueued = false;
        continueWatchingContainers();
      });
    }
    for (var m = 0; m < mutations.length; m++) {
      var added = mutations[m].addedNodes;
      for (var n = 0; n < added.length; n++) {
        var node = added[n];
        if (node.nodeType !== 1) continue;
        var sheet = (node.classList && node.classList.contains('actionSheet')) ? node : (node.querySelector ? node.querySelector('.actionSheet') : null);
        if (sheet && pending) {
          injectButton(sheet, pending);
        }
      }
    }
  });
  observer.observe(document.body, { childList: true, subtree: true });

  // The details page resolves its row entry over the network, which can finish after the
  // sheet has opened; add the button once it does, as long as the sheet is still showing.
  function injectButton(sheet, ctx) {
    ctx.ready.then(function (itemId) {
      if (itemId && sheet.isConnected) appendRemoveButton(sheet, ctx, itemId);
    });
  }

  function appendRemoveButton(sheet, ctx, itemId) {
    if (sheet.querySelector('.cw-remove-btn')) return;
    var scroller = sheet.querySelector('.actionSheetScroller');
    if (!scroller) return;
    var btn = document.createElement('button');
    btn.className = 'listItem listItem-button actionSheetMenuItem emby-button cw-remove-btn';
    btn.setAttribute('is', 'emby-button');
    btn.innerHTML = '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons delete" aria-hidden="true"></span><div class="listItemBody actionsheetListItemBody"><div class="listItemBodyText actionSheetItemText">Remove from Continue Watching</div></div>';
    btn.addEventListener('click', function () {
      function reportFailure(err) {
        console.error('[ContinueWatching] failed to remove item', err);
        if (window.Dashboard && window.Dashboard.alert) {
          window.Dashboard.alert('Failed to remove item from Continue Watching.');
        } else {
          alert('Failed to remove item from Continue Watching.');
        }
      }

      window.ApiClient.ajax({
        type: 'POST',
        url: window.ApiClient.getUrl('ContinueWatching/' + itemId + '/remove', { userId: ctx.userId })
      }).then(function (response) {
        // ajax resolves for 4xx/5xx too, so check the status before claiming success --
        // otherwise the card vanishes from the DOM and silently returns on the next load.
        if (!succeeded(response)) {
          reportFailure(response);
          return;
        }
        if (ctx.card) ctx.card.remove();
        scheduleRefresh();
      }).catch(reportFailure);

      var container = sheet.closest('.dialogContainer');
      if (container) container.remove();
      var backdrop = document.querySelector('.dialogBackdrop');
      if (backdrop) backdrop.remove();
    });
    scroller.appendChild(btn);
  }
})();

""";
}
