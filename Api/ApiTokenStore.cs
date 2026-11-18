using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace OptiumPlusFileManager.Api;

public sealed class ApiTokenStore
{
    public ApiTokenStore()
    {
        ReadToken = GetEnvironmentToken("FILE_SORTER_API_READ_TOKEN") ?? CreateToken();
        WriteToken = GetEnvironmentToken("FILE_SORTER_API_WRITE_TOKEN") ?? CreateToken();
        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ReadToken), Encoding.UTF8.GetBytes(WriteToken)))
            throw new InvalidOperationException("Read- и write-токены должны быть разными.");
    }

    public string ReadToken { get; }
    public string WriteToken { get; }

    public bool HasRead(string? token) => Matches(token, ReadToken) || Matches(token, WriteToken);
    public bool HasWrite(string? token) => Matches(token, WriteToken);

    public static string? ReadBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static string? GetEnvironmentToken(string variableName)
    {
        var token = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (Encoding.UTF8.GetByteCount(token) < 32)
            throw new InvalidOperationException($"{variableName} должен содержать не менее 32 байт.");
        return token;
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool Matches(string? token, string expected)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var actualBytes = Encoding.UTF8.GetBytes(token);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
