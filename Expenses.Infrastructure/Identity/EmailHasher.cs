using System.Security.Cryptography;
using System.Text;
using Expenses.Application.Identity;

namespace Expenses.Infrastructure.Identity;

public sealed class EmailHasher : IEmailHasher
{
    private readonly byte[] key;
    public EmailHasher(string base64Key)
    {
        try { key = Convert.FromBase64String(base64Key); }
        catch { throw new InvalidOperationException("Identity email HMAC key must be configured privately."); }
        if (key.Length < 32) throw new InvalidOperationException("Identity email HMAC key must contain at least 32 bytes.");
    }
    public byte[] Hash(string normalizedEmail) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalizedEmail));
}
