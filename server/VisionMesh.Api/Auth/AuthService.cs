using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;

namespace VisionMesh.Api.Auth;

/// <summary>The signed-in user attached to a request, or null for anonymous requests.</summary>
public sealed record AuthenticatedUser(string Id, string Username, UserRole Role)
{
    public bool IsAtLeast(UserRole required) => Role >= required;
}

public sealed record LoginResult(bool Success, string? Token, DateTimeOffset? ExpiresUtc, AuthenticatedUser? User, string? Error);

/// <summary>
/// Password login, session tokens and short-lived stream tokens.
///
/// Sessions are opaque random tokens stored hashed, not JWTs. For a self-hosted single-server
/// product that is strictly better: revocation is immediate (delete the row), there is no signing
/// key to leak or rotate, and a stolen token cannot be replayed after an administrator disables
/// the account.
/// </summary>
public sealed class AuthService(
    UserRepository users,
    AuditRepository audit,
    ILogger<AuthService> log)
{
    public const string SessionCookieName = "vm_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan StreamTokenLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Failed login attempts per username, used for throttling.
    /// Kept in memory on purpose: this is abuse resistance for a LAN service, not an audit trail,
    /// and it should reset when the server restarts.
    /// </summary>
    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Single-camera, short-lived tokens for clients that cannot send an Authorization header on a
    /// media request (an &lt;img&gt; tag, a native video player). Scoping them to one camera and two
    /// minutes means a token that leaks into a proxy log grants almost nothing.
    /// </summary>
    private readonly ConcurrentDictionary<string, StreamGrant> _streamTokens = new(StringComparer.Ordinal);

    private sealed record FailureRecord(int Count, DateTimeOffset LastAttemptUtc);
    private sealed record StreamGrant(string CameraId, string UserId, UserRole Role, DateTimeOffset ExpiresUtc);

    public LoginResult Login(string username, string password, string? address, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new LoginResult(false, null, null, null, "Enter a username and password.");

        if (IsThrottled(username, out var retryAfter))
        {
            log.LogWarning("Login for {Username} throttled from {Address}.", username, address);
            return new LoginResult(false, null, null, null, $"Too many failed attempts. Try again in {retryAfter.TotalSeconds:0} seconds.");
        }

        var user = users.GetByUsername(username);

        // Verify even when the user does not exist, against a dummy hash, so response time does
        // not reveal which usernames are real.
        var stored = user?.PasswordHash ?? DummyHash.Value;
        var passwordOk = PasswordHasher.Verify(password, stored);

        if (user is null || !passwordOk || user.Disabled)
        {
            RecordFailure(username);
            audit.Write(new AuditEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Username = username,
                Action = "login.failed",
                Address = address,
                Detail = user is null ? "unknown user" : user.Disabled ? "account disabled" : "wrong password",
            });
            return new LoginResult(false, null, null, null, "That username or password is not correct.");
        }

        _failures.TryRemove(username, out _);

        // Opportunistically upgrade the stored hash if the iteration count has since been raised.
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(password);
        }

        user.LastLoginUtc = DateTimeOffset.UtcNow;
        users.Update(user);

        var token = Ids.NewSecret();
        var expires = DateTimeOffset.UtcNow.Add(SessionLifetime);
        users.CreateSession(token, user.Id, expires, address, userAgent);

        audit.Write(new AuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            UserId = user.Id,
            Username = user.Username,
            Action = "login.success",
            Address = address,
        });

        log.LogInformation("User {Username} signed in from {Address}.", user.Username, address ?? "unknown");
        return new LoginResult(true, token, expires, new AuthenticatedUser(user.Id, user.Username, user.Role), null);
    }

    public void Logout(string token, AuthenticatedUser? user, string? address)
    {
        users.DeleteSession(token);
        if (user is null) return;

        audit.Write(new AuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            UserId = user.Id,
            Username = user.Username,
            Action = "logout",
            Address = address,
        });
    }

    /// <summary>Resolves the caller from the Authorization header or the session cookie.</summary>
    public AuthenticatedUser? Authenticate(HttpContext context)
    {
        var token = ExtractSessionToken(context);
        if (token is null) return null;

        var user = users.GetUserBySession(token);
        return user is null ? null : new AuthenticatedUser(user.Id, user.Username, user.Role);
    }

    public static string? ExtractSessionToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            if (value.Length > 0) return value;
        }

        return context.Request.Cookies.TryGetValue(SessionCookieName, out var cookie) && cookie.Length > 0 ? cookie : null;
    }

    /// <summary>
    /// Issues a token that authorises exactly one camera's live stream for a couple of minutes.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresUtc) IssueStreamToken(string cameraId, AuthenticatedUser user)
    {
        PruneStreamTokens();

        var token = Ids.NewSecret();
        var expires = DateTimeOffset.UtcNow.Add(StreamTokenLifetime);
        _streamTokens[token] = new StreamGrant(cameraId, user.Id, user.Role, expires);
        return (token, expires);
    }

    /// <summary>Validates a stream token against the camera being requested.</summary>
    public AuthenticatedUser? ValidateStreamToken(string token, string cameraId)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_streamTokens.TryGetValue(token, out var grant)) return null;

        if (grant.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            _streamTokens.TryRemove(token, out _);
            return null;
        }

        // A token for one camera must not open another, or the scoping would be decorative.
        if (!string.Equals(grant.CameraId, cameraId, StringComparison.Ordinal)) return null;

        var user = users.GetById(grant.UserId);
        if (user is null || user.Disabled)
        {
            _streamTokens.TryRemove(token, out _);
            return null;
        }

        return new AuthenticatedUser(user.Id, user.Username, user.Role);
    }

    public void WriteSessionCookie(HttpContext context, string token, DateTimeOffset expiresUtc)
    {
        context.Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            // Secure only over HTTPS: forcing it on a plain-HTTP LAN install would silently
            // break sign-in, which is the most common self-hosted setup on day one.
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = expiresUtc,
            Path = "/",
        });
    }

    public static void ClearSessionCookie(HttpContext context)
        => context.Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });

    public void Audit(AuthenticatedUser? user, string action, string? target = null, string? address = null, string? detail = null)
        => audit.Write(new AuditEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            UserId = user?.Id,
            Username = user?.Username,
            Action = action,
            Target = target,
            Address = address,
            Detail = detail,
        });

    // ---- throttling --------------------------------------------------------

    private bool IsThrottled(string username, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_failures.TryGetValue(username, out var record)) return false;

        // Five free attempts, then a delay that doubles up to five minutes. Slow enough to make
        // online guessing pointless, forgiving enough not to lock out a mistyped password.
        if (record.Count < 5) return false;

        var penaltySeconds = Math.Min(300, Math.Pow(2, Math.Min(record.Count - 4, 8)));
        var unlockAt = record.LastAttemptUtc.AddSeconds(penaltySeconds);
        if (DateTimeOffset.UtcNow >= unlockAt) return false;

        retryAfter = unlockAt - DateTimeOffset.UtcNow;
        return true;
    }

    private void RecordFailure(string username)
        => _failures.AddOrUpdate(
            username,
            _ => new FailureRecord(1, DateTimeOffset.UtcNow),
            (_, existing) => new FailureRecord(existing.Count + 1, DateTimeOffset.UtcNow));

    private void PruneStreamTokens()
    {
        if (_streamTokens.Count < 256) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _streamTokens.Where(e => e.Value.ExpiresUtc <= now).ToArray())
        {
            _streamTokens.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>A real hash of a random value, so failed lookups cost the same as real verifications.</summary>
    private static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(Ids.NewSecret()));
}
