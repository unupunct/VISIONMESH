using VisionMesh.Core.Models;
using VisionMesh.Core.Util;

namespace VisionMesh.Database.Repositories;

/// <summary>Users plus their bearer sessions. Sessions are stored hashed, exactly like device tokens.</summary>
public sealed class UserRepository(VisionMeshDatabase database)
{
    private const string SelectColumns = "id, username, password_hash, role, created_utc, last_login_utc, disabled";

    public List<User> GetAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM users ORDER BY username COLLATE NOCASE;");
        using var reader = command.ExecuteReader();
        var result = new List<User>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public User? GetById(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM users WHERE id = $id;").With("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public User? GetByUsername(string username)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM users WHERE username = $u COLLATE NOCASE;").With("$u", username);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public int Count()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT COUNT(*) FROM users;");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Insert(User user)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "INSERT INTO users (id, username, password_hash, role, created_utc, last_login_utc, disabled) VALUES ($id, $u, $p, $r, $c, $l, $d);")
            .With("$id", user.Id).With("$u", user.Username).With("$p", user.PasswordHash)
            .With("$r", (int)user.Role).With("$c", user.CreatedUtc.ToDb())
            .With("$l", user.LastLoginUtc.ToDbOrNull()).With("$d", user.Disabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void Update(User user)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "UPDATE users SET username = $u, password_hash = $p, role = $r, last_login_utc = $l, disabled = $d WHERE id = $id;")
            .With("$id", user.Id).With("$u", user.Username).With("$p", user.PasswordHash)
            .With("$r", (int)user.Role).With("$l", user.LastLoginUtc.ToDbOrNull()).With("$d", user.Disabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public bool Delete(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM users WHERE id = $id;").With("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Number of enabled administrators. Used to refuse deleting or demoting the last admin.</summary>
    public int CountActiveAdministrators()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("SELECT COUNT(*) FROM users WHERE role = $r AND disabled = 0;")
            .With("$r", (int)UserRole.Administrator);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // ---- sessions ----------------------------------------------------------

    public void CreateSession(string token, string userId, DateTimeOffset expiresUtc, string? address, string? userAgent)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "INSERT INTO sessions (token_hash, user_id, created_utc, expires_utc, address, user_agent) VALUES ($t, $u, $c, $e, $a, $ua);")
            .With("$t", TokenHasher.Hash(token)).With("$u", userId)
            .With("$c", DateTimeOffset.UtcNow.ToDb()).With("$e", expiresUtc.ToDb())
            .With("$a", address.OrNull()).With("$ua", userAgent.OrNull());
        command.ExecuteNonQuery();
    }

    /// <summary>Resolves a session token to its user, or null when the token is unknown, expired or disabled.</summary>
    public User? GetUserBySession(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            $"""
            SELECT u.id, u.username, u.password_hash, u.role, u.created_utc, u.last_login_utc, u.disabled
            FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = $t AND s.expires_utc > $now AND u.disabled = 0;
            """)
            .With("$t", TokenHasher.Hash(token)).With("$now", DateTimeOffset.UtcNow.ToDb());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void DeleteSession(string token)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM sessions WHERE token_hash = $t;").With("$t", TokenHasher.Hash(token));
        command.ExecuteNonQuery();
    }

    /// <summary>Invalidates every session for a user, e.g. after a password change or role downgrade.</summary>
    public void DeleteSessionsForUser(string userId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM sessions WHERE user_id = $u;").With("$u", userId);
        command.ExecuteNonQuery();
    }

    public int PurgeExpiredSessions()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM sessions WHERE expires_utc <= $now;").With("$now", DateTimeOffset.UtcNow.ToDb());
        return command.ExecuteNonQuery();
    }

    private static User Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Username = r.GetString(1),
        PasswordHash = r.GetString(2),
        Role = (UserRole)r.GetInt(3),
        CreatedUtc = r.GetTimestamp(4),
        LastLoginUtc = r.GetTimestampOrNull(5),
        Disabled = r.GetBool(6),
    };
}
