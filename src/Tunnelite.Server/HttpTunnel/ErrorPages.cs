using Tunnelite.Protocol;

namespace Tunnelite.Server.HttpTunnel;

/// <summary>Cached relay error documents. Rendering is independent of requests and local application responses.</summary>
internal static class ErrorPages
{
    internal static readonly string NotFound = BrowserPage.Render(
        title: "Tunnel unavailable",
        message: "There’s no active tunnel or page at this address.",
        badge: "HTTP 404",
        state: "not-found",
        icons: """
            <svg class="icon icon-static" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4M8.5 10.5h4"/>
            </svg>
            """,
        help: """
            <div class="steps">
              <div class="steps-title">What to check</div>
              <div class="step"><span class="step-num">1</span><span>Check that the link is complete and spelled correctly.</span></div>
              <div class="step"><span class="step-num">2</span><span>If this is a tunnel, ask its owner to start <strong>tunnelite</strong> and share the current link.</span></div>
            </div>
            <div class="actions"><a class="button" href="https://tunnelite.com">Go to Tunnelite</a></div>
            """,
        footer: "<span>No active route</span><a href=\"https://tunnelite.com\">tunnelite.com</a>");

    internal static readonly string BadGateway = BrowserPage.Render(
        title: "Can’t reach the local app",
        message: "Tunnelite couldn’t get a response from the app behind this tunnel.",
        badge: "HTTP 502",
        state: "gateway-error",
        icons: """
            <svg class="icon icon-static" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <rect x="3" y="3" width="18" height="13" rx="2"/><path d="M8 21h8M12 16v5M12 7v3M12 13h.01"/>
            </svg>
            """,
        help: """
            <div class="steps">
              <div class="steps-title">On the desktop</div>
              <div class="step"><span class="step-num">1</span><span>Make sure the local app is running and opens on the desktop.</span></div>
              <div class="step"><span class="step-num">2</span><span>Check that the tunnel points to the app’s current address and port.</span></div>
              <div class="step"><span class="step-num">3</span><span>Once the app is ready, try this page again.</span></div>
            </div>
            <div class="actions"><a class="button" href="">Try again</a></div>
            """,
        footer: "<span>Waiting for the local app</span><a href=\"https://tunnelite.com\">tunnelite.com</a>");
}
