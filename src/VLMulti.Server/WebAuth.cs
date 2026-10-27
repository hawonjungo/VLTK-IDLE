using System.Security.Cryptography;
using System.Text;
using VLMulti.Core;
using VLMulti.Core.Recovery;

namespace VLMulti.Server;

// Mật khẩu trang web (PBKDF2) và phiên đăng nhập bằng cookie ký HMAC (còn hiệu lực sau khi khởi động lại)
public sealed class WebAuth
{
    public const string CookieName = "vlmulti_auth";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private readonly ServerOptions _options;
    private readonly byte[] _key;
    private readonly AttemptLimiter _failures = new(10, TimeSpan.FromMinutes(10)); // chống dò mật khẩu

    public WebAuth(ServerOptions options)
    {
        _options = options;
        var keyPath = Path.Combine(AppPaths.DataDir, "auth.key");
        if (File.Exists(keyPath)) _key = File.ReadAllBytes(keyPath);
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllBytes(keyPath, _key);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void SetPassword(ServerOptions options, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        options.PasswordSalt = Convert.ToBase64String(salt);
        options.PasswordHash = Convert.ToBase64String(Hash(password, salt));
        options.Save();
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);

    // Trả về token cookie nếu đúng mật khẩu; null nếu sai hoặc thử sai quá nhiều lần
    public string? Login(string password)
    {
        if (_options.PasswordHash is null || _options.PasswordSalt is null || TooManyFailures) return null;
        var expected = Convert.FromBase64String(_options.PasswordHash);
        var actual = Hash(password ?? "", Convert.FromBase64String(_options.PasswordSalt));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            _failures.TryAcquire(DateTime.Now);
            return null;
        }
        return IssueToken(DateTimeOffset.UtcNow + SessionLifetime);
    }

    public bool TooManyFailures => !_failures.CanAcquire(DateTime.Now);

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var expiry)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < DateTimeOffset.UtcNow) return false;
        var expectedSig = Sign(parts[0]);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedSig), Encoding.ASCII.GetBytes(parts[1]));
    }

    private string IssueToken(DateTimeOffset expiry)
    {
        var payload = expiry.ToUnixTimeSeconds().ToString();
        return payload + "." + Sign(payload);
    }

    private string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
