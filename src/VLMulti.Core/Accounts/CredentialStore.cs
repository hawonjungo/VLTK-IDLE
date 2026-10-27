using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VLMulti.Core.Accounts;

// Cách mã hoá mật khẩu: bản Desktop dùng DPAPI của Windows, bản Server dùng AES với khoá riêng.
// `purpose` khác nhau cho từng acc để không lấy được mật khẩu acc này đem giải mã cho acc khác.
public interface ISecretProtector
{
    byte[] Protect(byte[] data, string purpose);
    byte[] Unprotect(byte[] data, string purpose);
}

// Lưu tài khoản/mật khẩu game; mật khẩu được mã hoá bằng ISecretProtector truyền vào
public sealed class CredentialStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly Dictionary<string, StoredCredential> _items;

    public CredentialStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        _items = Load(path);
    }

    public bool Has(string accountId) => _items.ContainsKey(accountId);

    public string? UserOf(string accountId) => _items.TryGetValue(accountId, out var c) ? c.User : null;

    public (string User, string Password)? Get(string accountId)
    {
        if (!_items.TryGetValue(accountId, out var c)) return null;
        try
        {
            var bytes = _protector.Unprotect(Convert.FromBase64String(c.Secret), Purpose(accountId));
            return (c.User, Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Ví dụ file chép từ máy khác sang → không giải mã được, coi như chưa lưu
            Log.Write($"credentials: cannot decrypt {accountId}: {ex.Message}");
            return null;
        }
    }

    public void Set(string accountId, string user, string password)
    {
        var secret = _protector.Protect(Encoding.UTF8.GetBytes(password), Purpose(accountId));
        _items[accountId] = new StoredCredential { User = user, Secret = Convert.ToBase64String(secret) };
        Save();
    }

    public void Remove(string accountId)
    {
        if (_items.Remove(accountId)) Save();
    }

    private static string Purpose(string accountId) => "VLMulti.v1/" + accountId;

    private static Dictionary<string, StoredCredential> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, StoredCredential>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"credentials: load failed: {ex.Message}");
        }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"credentials: save failed: {ex.Message}");
        }
    }

    private sealed class StoredCredential
    {
        public string User { get; set; } = "";
        public string Secret { get; set; } = "";
    }
}

// Mã hoá AES-GCM với khoá 256 bit ngẫu nhiên lưu trong một file (bản Server, chạy được trên Linux).
// Khoá nằm cạnh dữ liệu nên chỉ chống việc mở file credentials.json ra đọc lén, không chống người đã
// vào được máy chủ. Trên Linux file khoá chỉ cho chủ sở hữu đọc (chmod 600).
public sealed class AesKeyFileProtector : ISecretProtector
{
    private const int NonceSize = 12, TagSize = 16;
    private readonly byte[] _key;

    public AesKeyFileProtector(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            if (_key.Length != 32) throw new CryptographicException($"File khoá {keyPath} không hợp lệ");
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, _key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Kết quả = nonce (12) + tag (16) + dữ liệu mã hoá
    public byte[] Protect(byte[] data, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data, string purpose)
    {
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Dữ liệu mã hoá bị hỏng");
        var nonce = data.AsSpan(0, NonceSize);
        var tag = data.AsSpan(NonceSize, TagSize);
        var cipher = data.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }
}
