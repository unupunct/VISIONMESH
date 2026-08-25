using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VisionMesh.Api.Auth;
using VisionMesh.Core.Models;
using VisionMesh.Core.Util;
using VisionMesh.Database.Repositories;

namespace VisionMesh.Api.Endpoints;

/// <summary>User accounts and roles.</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAdministrator();

        group.MapGet("/", (UserRepository users) => Results.Ok(users.GetAll().Select(UserDto.From)))
            .WithName("ListUsers");

        group.MapPost("/", (HttpContext http, CreateUserRequest request, UserRepository users, AuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username))
                return Results.BadRequest(new { error = "Enter a username." });

            if (users.GetByUsername(request.Username) is not null)
                return Results.Conflict(new { error = "A user with that name already exists." });

            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
                return Results.BadRequest(new { error = $"'{request.Role}' is not a role. Use Viewer, Operator or Administrator." });

            var problem = SystemEndpoints.ValidatePassword(request.Password);
            if (problem is not null) return Results.BadRequest(new { error = problem });

            var user = new User
            {
                Id = Ids.NewId("usr"),
                Username = request.Username.Trim(),
                PasswordHash = PasswordHasher.Hash(request.Password),
                Role = role,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            users.Insert(user);

            auth.Audit(http.CurrentUser(), "user.create", user.Id, http.ClientAddress(), $"{user.Username} as {role}");
            return Results.Created($"/api/users/{user.Id}", UserDto.From(user));
        })
        .WithName("CreateUser");

        group.MapPatch("/{id}", (HttpContext http, string id, UpdateUserRequest request, UserRepository users, AuthService auth) =>
        {
            var user = users.GetById(id);
            if (user is null) return Results.NotFound(new { error = "That user does not exist." });

            var caller = http.CurrentUser();
            var wasAdministrator = user.Role == UserRole.Administrator && !user.Disabled;

            if (request.Role is { } roleText)
            {
                if (!Enum.TryParse<UserRole>(roleText, ignoreCase: true, out var role))
                    return Results.BadRequest(new { error = $"'{roleText}' is not a role." });
                user.Role = role;
            }

            if (request.Disabled is { } disabled)
            {
                if (disabled && user.Id == caller.Id)
                    return Results.BadRequest(new { error = "You cannot disable your own account." });
                user.Disabled = disabled;
            }

            // Losing the last administrator would leave the server unmanageable with no way back
            // short of editing the database by hand.
            var stillAdministrator = user.Role == UserRole.Administrator && !user.Disabled;
            if (wasAdministrator && !stillAdministrator && users.CountActiveAdministrators() <= 1)
            {
                return Results.BadRequest(new { error = "This is the only administrator. Promote another user first." });
            }

            var passwordChanged = false;
            if (!string.IsNullOrEmpty(request.Password))
            {
                var problem = SystemEndpoints.ValidatePassword(request.Password);
                if (problem is not null) return Results.BadRequest(new { error = problem });
                user.PasswordHash = PasswordHasher.Hash(request.Password);
                passwordChanged = true;
            }

            users.Update(user);

            // A password change or a role reduction must not leave old sessions alive.
            if (passwordChanged || !stillAdministrator) users.DeleteSessionsForUser(user.Id);

            auth.Audit(caller, "user.update", user.Id, http.ClientAddress(),
                passwordChanged ? "password changed" : $"role={user.Role} disabled={user.Disabled}");

            return Results.Ok(UserDto.From(user));
        })
        .WithName("UpdateUser");

        group.MapDelete("/{id}", (HttpContext http, string id, UserRepository users, AuthService auth) =>
        {
            var user = users.GetById(id);
            if (user is null) return Results.NotFound(new { error = "That user does not exist." });

            var caller = http.CurrentUser();
            if (user.Id == caller.Id) return Results.BadRequest(new { error = "You cannot delete your own account." });

            if (user.Role == UserRole.Administrator && !user.Disabled && users.CountActiveAdministrators() <= 1)
                return Results.BadRequest(new { error = "This is the only administrator. Promote another user first." });

            users.DeleteSessionsForUser(user.Id);
            users.Delete(user.Id);
            auth.Audit(caller, "user.delete", user.Id, http.ClientAddress(), user.Username);

            return Results.Ok(new { ok = true });
        })
        .WithName("DeleteUser");

        // Changing your own password needs no administrator role, only your current password.
        app.MapPost("/api/account/password", (HttpContext http, ChangePasswordRequest request, UserRepository users, AuthService auth) =>
        {
            var caller = http.CurrentUser();
            var user = users.GetById(caller.Id);
            if (user is null) return Results.NotFound(new { error = "Your account no longer exists." });

            if (!PasswordHasher.Verify(request.CurrentPassword ?? "", user.PasswordHash))
                return Results.BadRequest(new { error = "Your current password is not correct." });

            var problem = SystemEndpoints.ValidatePassword(request.NewPassword ?? "");
            if (problem is not null) return Results.BadRequest(new { error = problem });

            user.PasswordHash = PasswordHasher.Hash(request.NewPassword!);
            users.Update(user);

            // Drop every session including this one: the user signs in again with the new password.
            users.DeleteSessionsForUser(user.Id);
            AuthService.ClearSessionCookie(http);
            auth.Audit(caller, "account.password", user.Id, http.ClientAddress());

            return Results.Ok(new { ok = true, signedOut = true });
        })
        .RequireViewer()
        .WithTags("Users")
        .WithName("ChangeOwnPassword");
    }
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}
