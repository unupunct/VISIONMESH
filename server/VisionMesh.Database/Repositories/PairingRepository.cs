using VisionMesh.Core.Models;

namespace VisionMesh.Database.Repositories;

/// <summary>
/// Short-lived pairing codes. A code is single-use and time-limited, so a QR code
/// photographed off a screen stops being useful within minutes.
/// </summary>
public sealed class PairingRepository(VisionMeshDatabase database)
{
    private const string SelectColumns = "code, created_utc, expires_utc, used, issued_by_user_id, consumed_by_device";

    public void Insert(PairingToken token)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "INSERT INTO pairing_tokens (code, created_utc, expires_utc, used, issued_by_user_id, consumed_by_device) VALUES ($c, $cr, $e, 0, $u, NULL);")
            .With("$c", token.Code).With("$cr", token.CreatedUtc.ToDb())
            .With("$e", token.ExpiresUtc.ToDb()).With("$u", token.IssuedByUserId.OrNull());
        command.ExecuteNonQuery();
    }

    public PairingToken? Get(string code)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command($"SELECT {SelectColumns} FROM pairing_tokens WHERE code = $c;").With("$c", code);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// Atomically marks a code used. Returns false when it was already consumed or has expired,
    /// which is what stops two devices racing to claim the same code.
    /// </summary>
    public bool TryConsume(string code, string deviceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command(
            "UPDATE pairing_tokens SET used = 1, consumed_by_device = $d WHERE code = $c AND used = 0 AND expires_utc > $now;")
            .With("$c", code).With("$d", deviceId).With("$now", DateTimeOffset.UtcNow.ToDb());
        return command.ExecuteNonQuery() > 0;
    }

    public List<PairingToken> GetActive()
    {
        using var connection = database.OpenConnection();
        using var command = connection
            .Command($"SELECT {SelectColumns} FROM pairing_tokens WHERE used = 0 AND expires_utc > $now ORDER BY created_utc DESC;")
            .With("$now", DateTimeOffset.UtcNow.ToDb());
        using var reader = command.ExecuteReader();
        var result = new List<PairingToken>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public int PurgeExpired()
    {
        using var connection = database.OpenConnection();
        using var command = connection.Command("DELETE FROM pairing_tokens WHERE expires_utc <= $now;").With("$now", DateTimeOffset.UtcNow.ToDb());
        return command.ExecuteNonQuery();
    }

    private static PairingToken Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Code = r.GetString(0),
        CreatedUtc = r.GetTimestamp(1),
        ExpiresUtc = r.GetTimestamp(2),
        Used = r.GetBool(3),
        IssuedByUserId = r.GetStringOrNull(4),
        ConsumedByDeviceId = r.GetStringOrNull(5),
    };
}
