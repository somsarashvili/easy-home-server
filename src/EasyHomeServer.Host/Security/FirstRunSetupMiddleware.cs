namespace EasyHomeServer.Host.Security;

/// <summary>
/// Funnels a freshly installed host to <c>/setup</c> until an admin password exists.
/// </summary>
/// <remarks>
/// A default password would leave every install reachable with a known credential until
/// someone changed it. Instead the tool has no credential at all until the first visitor sets
/// one, which on a LAN-facing box is a much smaller window to get wrong.
/// </remarks>
internal sealed class FirstRunSetupMiddleware(RequestDelegate next)
{
    private const string SetupPath = "/setup";

    public async Task InvokeAsync(HttpContext context, AdminAccount adminAccount)
    {
        var path = context.Request.Path;

        // Framework and static requests must keep working, or the setup page cannot render.
        if (path.StartsWithSegments("/_blazor")
            || path.StartsWithSegments("/_framework")
            || path.StartsWithSegments("/_content")
            || Path.HasExtension(path.Value))
        {
            await next(context);

            return;
        }

        var configured = await adminAccount.IsConfiguredAsync(context.RequestAborted);
        var isSetup = path.StartsWithSegments(SetupPath);

        if (!configured && !isSetup)
        {
            context.Response.Redirect(SetupPath);

            return;
        }

        if (configured && isSetup)
        {
            context.Response.Redirect("/");

            return;
        }

        await next(context);
    }
}
