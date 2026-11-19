using System.Text.Json;

namespace ApiAggregator.Gateway;

/// <summary>
/// The "API docs" page (Swagger UI) served at /bff/docs. It is one small HTML string that loads
/// Swagger UI from unpkg.com and reads the spec from ./openapi.json (same folder, so the same gateway).
/// </summary>
public static class ApiDocsPage
{
    /// <summary>
    /// Only this page's own files, Swagger UI from unpkg.com, and the one small inline script.
    /// Calls go to the gateway itself (connect-src 'self'); nothing can frame the page.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self' https://unpkg.com 'unsafe-inline'; " +
        "style-src 'self' https://unpkg.com 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>
    /// <paramref name="devTokenScopes"/>: in Development only, the scopes to ask /dev/token for, so the page can sign in by
    /// itself and "Execute" works at once. Null everywhere else: people use the Authorize button and paste a token.
    /// </summary>
    public static string Html(string? devTokenScopes)
    {
        // Encoded as a JavaScript string (the default encoder escapes < > & ' so it can't break out of the script).
        var tokenUrl = devTokenScopes is null
            ? "null"
            : JsonSerializer.Serialize("/dev/token?sub=api-docs&scope=" + Uri.EscapeDataString(devTokenScopes));

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Aggregated endpoints - API docs</title>
              <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5/swagger-ui.css">
              <style>
                body { margin: 0; }
                #banner { background: #e8f3ff; color: #12395f; padding: 8px 16px; font: 14px system-ui, sans-serif; border-bottom: 1px solid #b9d6f2; }
              </style>
            </head>
            <body>
              <div id="banner" hidden>Development: signed in automatically with a test token.</div>
              <div id="swagger-ui"></div>
              <script src="https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
              <script>
                var tokenUrl = {{tokenUrl}};
                var ui = SwaggerUIBundle({
                  url: './openapi.json',
                  dom_id: '#swagger-ui',
                  deepLinking: true,
                  tryItOutEnabled: true,
                  persistAuthorization: true,
                  displayRequestDuration: true,
                  filter: true,
                  // Sign in once the spec has loaded, because the "bearer" scheme comes from the spec.
                  onComplete: function () {
                    if (!tokenUrl) return;
                    fetch(tokenUrl).then(function (r) { return r.json(); }).then(function (t) {
                      ui.preauthorizeApiKey('bearer', t.access_token);
                      document.getElementById('banner').hidden = false;
                    }).catch(function () { /* no token: use the Authorize button */ });
                  }
                });
              </script>
            </body>
            </html>
            """;
    }
}
