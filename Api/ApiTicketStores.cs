using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OptiumPlusFileManager.Api;

public sealed class UploadTicketStore
{
    private readonly ConcurrentDictionary<string, DateTime> _tickets = new(StringComparer.Ordinal);

    public (string Token, DateTime ExpiresAtUtc) Create()
    {
        RemoveExpired();
        var token = NewToken();
        var expires = DateTime.UtcNow.AddMinutes(15);
        _tickets[token] = expires;
        return (token, expires);
    }

    public bool TryConsume(string token) => _tickets.TryRemove(token, out var expires) && expires > DateTime.UtcNow;

    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var ticket in _tickets)
            if (ticket.Value <= now) _tickets.TryRemove(ticket.Key, out _);
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class DownloadTicketStore
{
    private readonly ConcurrentDictionary<string, (string FileId, DateTime ExpiresAtUtc)> _tickets = new(StringComparer.Ordinal);

    public (string Token, DateTime ExpiresAtUtc) Create(string fileId)
    {
        RemoveExpired();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTime.UtcNow.AddMinutes(5);
        _tickets[token] = (fileId, expires);
        return (token, expires);
    }

    public bool TryConsume(string token, out string fileId)
    {
        fileId = string.Empty;
        if (!_tickets.TryRemove(token, out var ticket) || ticket.ExpiresAtUtc <= DateTime.UtcNow)
            return false;
        fileId = ticket.FileId;
        return true;
    }

    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var ticket in _tickets)
            if (ticket.Value.ExpiresAtUtc <= now) _tickets.TryRemove(ticket.Key, out _);
    }
}
