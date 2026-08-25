using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VisionMesh.Core.Models;

namespace VisionMesh.Api.Auth;

/// <summary>
/// Requires an authenticated caller with at least a given role, and stashes the resolved user
/// in HttpContext.Items so handlers do not authenticate a second time.
/// </summary>
public sealed class AuthEndpointFilter(UserRole minimumRole) : IEndpointFilter
{
    public const string UserItemKey = "visionmesh.user";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var auth = http.RequestServices.GetRequiredService<AuthService>();

        var user = auth.Authenticate(http);
        if (user is null)
        {
            return Results.Json(
                new { error = "Sign in to continue.", code = "unauthenticated" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!user.IsAtLeast(minimumRole))
        {
            // Deliberately explicit about what the action needs: a viewer clicking a PTZ control
            // should be told their role cannot do that, not given a blank failure.
            return Results.Json(
                new { error = $"This action needs the {minimumRole} role.", code = "forbidden", required = minimumRole.ToString() },
                statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[UserItemKey] = user;
        return await next(context);
    }
}

public static class AuthEndpointExtensions
{
    /// <summary>Requires any signed-in user (Viewer or above).</summary>
    public static TBuilder RequireViewer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new AuthEndpointFilter(UserRole.Viewer));

    /// <summary>Requires an Operator or Administrator: anything that changes camera behaviour.</summary>
    public static TBuilder RequireOperator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new AuthEndpointFilter(UserRole.Operator));

    /// <summary>Requires an Administrator: users, devices, storage, integrations, settings.</summary>
    public static TBuilder RequireAdministrator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new AuthEndpointFilter(UserRole.Administrator));

    /// <summary>The user resolved by the auth filter. Throws if called on an unprotected endpoint.</summary>
    public static AuthenticatedUser CurrentUser(this HttpContext context)
        => context.Items[AuthEndpointFilter.UserItemKey] as AuthenticatedUser
           ?? throw new InvalidOperationException("This endpoint is not protected by an auth filter.");

    /// <summary>The user, or null when the endpoint is open. Used by endpoints that adapt to the caller.</summary>
    public static AuthenticatedUser? CurrentUserOrNull(this HttpContext context)
        => context.Items[AuthEndpointFilter.UserItemKey] as AuthenticatedUser;

    /// <summary>Client address for audit entries, honouring a reverse proxy's forwarded header.</summary>
    public static string? ClientAddress(this HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            // The left-most entry is the original client; the rest are proxies.
            var first = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        return context.Connection.RemoteIpAddress?.ToString();
    }
}
