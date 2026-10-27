using System.Security.Cryptography;
using System.Text;
using VLMulti.Core.Accounts;

namespace VLMulti.Desktop;

// Mã hoá bằng DPAPI của Windows (CurrentUser): chỉ giải mã được trên đúng máy này,
// đúng tài khoản Windows này. Giữ nguyên cách cũ để mật khẩu đã lưu vẫn dùng được.
internal sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] data, string purpose) =>
        ProtectedData.Protect(data, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data, string purpose) =>
        ProtectedData.Unprotect(data, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
}
